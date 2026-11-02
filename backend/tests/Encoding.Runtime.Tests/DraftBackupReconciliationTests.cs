using System.Text.Json.Nodes;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class DraftBackupReconciliationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingSnapshotPreservesLocalStatusAndRejectsDivergence(bool divergent)
    {
        await using var source = await TestDatabase.CreateAsync(RuntimeFixture.ConnectionString!, "draft_source");
        await using var target = await TestDatabase.CreateAsync(RuntimeFixture.ConnectionString!, "draft_target");
        var cluster = Guid.NewGuid();
        var flow = Guid.NewGuid();
        foreach (var db in new[] { source.Db, target.Db })
        {
            await DatabaseMigrator.ApplyAsync(db, Harness.RepositoryRoot(), CancellationToken.None);
            await new ClusterIdentityRepository(db).AdoptAsync(cluster, "Test", CancellationToken.None);
            await using var insert = db.CreateCommand("INSERT INTO flows(id) VALUES(@id); INSERT INTO flow_draft_backups(flow_id,revision,definition) VALUES(@id,1,@definition)");
            insert.Parameters.AddWithValue("id", flow);
            insert.Parameters.AddWithValue("definition", SqlKind.Json, db == target.Db ? (divergent ? "{\"value\":2,\"other\":true}" : "{\"other\":true,\"value\":1}") : "{\"value\":1,\"other\":true}");
            await insert.ExecuteNonQueryAsync();
        }
        await using (var status = target.Db.CreateCommand("UPDATE flow_draft_backups SET status='ready' WHERE flow_id=@id"))
        {
            status.Parameters.AddWithValue("id", flow);
            await status.ExecuteNonQueryAsync();
        }
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        using var sourceKey = new ClusterDataKeyStore(cluster, key);
        using var targetKey = new ClusterDataKeyStore(cluster, key);
        var from = new ClusterEntityReconciliation(source.Db, sourceKey, new EphemeralDataProtectionProvider(), NullLogger<ClusterEntityReconciliation>.Instance);
        var to = new ClusterEntityReconciliation(target.Db, targetKey, new EphemeralDataProtectionProvider(), NullLogger<ClusterEntityReconciliation>.Instance);
        await from.CaptureLocalChangesAsync(CancellationToken.None);
        if (divergent) await to.CaptureLocalChangesAsync(CancellationToken.None);
        var events = await new ClusterReconciliationRepository(source.Db).GetEventsAfterAsync(cluster, new Dictionary<Guid,long>(), ClusterReconciliationRepository.MaximumBatchSize, CancellationToken.None);
        await new ClusterReconciliationRepository(target.Db).ApplyIncomingAsync(events, CancellationToken.None);
        await to.ApplyPendingAsync(CancellationToken.None);
        await to.ApplyPendingAsync(CancellationToken.None);
        var diagnostics = await to.ProjectionDiagnosticsAsync(CancellationToken.None);
        if (divergent)
        {
            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("immutable_snapshot_conflict", diagnostic.Cause);
            Assert.Equal("flow_draft_backups", diagnostic.Table);
            Assert.Equal(1, diagnostic.Attempts);
        }
        else
        {
            Assert.Empty(diagnostics);
            await to.CaptureLocalChangesAsync(CancellationToken.None);
            var outgoing = await new ClusterReconciliationRepository(target.Db).GetEventsAfterAsync(cluster, new Dictionary<Guid,long>(), ClusterReconciliationRepository.MaximumBatchSize, CancellationToken.None);
            Assert.DoesNotContain(outgoing, change => change.OriginId != events[0].OriginId && change.Payload.Contains("\"table\":\"flow_draft_backups\"", StringComparison.Ordinal));
        }
        await using var read = target.Db.CreateCommand("SELECT status,definition,(SELECT count(*) FROM cluster_sync_pending_projections) FROM flow_draft_backups WHERE flow_id=@id");
        read.Parameters.AddWithValue("id", flow);
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("ready", reader.GetString(0));
        Assert.Equal(divergent ? 2 : 1, JsonNode.Parse(reader.GetString(1))!["value"]!.GetValue<int>());
        Assert.Equal(divergent ? 1L : 0L, reader.GetInt64(2));
    }
}
