using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class ClusterReconciliationSqliteTests
{
    [DatabaseFact]
    public async Task ProcessingReceiptsMapToTheDestinationMediaIdAndFlowBackupsAreMaterialized()
    {
        var connectionString = RuntimeFixture.ConnectionString!;
        await using var source = await TestDatabase.CreateAsync(connectionString, "mesh_receipt_source");
        await using var target = await TestDatabase.CreateAsync(connectionString, "mesh_receipt_target");
        var migrations = Harness.RepositoryRoot();
        await DatabaseMigrator.ApplyAsync(source.Db, migrations, CancellationToken.None);
        await DatabaseMigrator.ApplyAsync(target.Db, migrations, CancellationToken.None);

        var clusterId = Guid.NewGuid();
        await new ClusterIdentityRepository(source.Db).AdoptAsync(clusterId, "Media", CancellationToken.None);
        await new ClusterIdentityRepository(target.Db).AdoptAsync(clusterId, "Media", CancellationToken.None);

        var nodeId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        var sourceScanId = Guid.NewGuid();
        var targetScanId = Guid.NewGuid();
        var sourceMediaId = Guid.NewGuid();
        var targetMediaId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var pendingNodeId = Guid.NewGuid();
        var pairingHash = System.Security.Cryptography.SHA256.HashData("pairing-code"u8);
        var pendingTokenHash = System.Security.Cryptography.SHA256.HashData("pending-node-token"u8);
        var modifiedAt = DateTimeOffset.UtcNow.AddMinutes(-2);

        await using (var connection = await source.Db.OpenConnectionAsync(CancellationToken.None))
        {
            await using (var node = Sql.Command("INSERT INTO cluster_nodes(id,name,roles,max_concurrent_tasks,capabilities,last_seen_at) VALUES(@id,'worker-a',@roles,1,'[]',@seen)", connection))
            {
                node.Parameters.AddWithValue("id", nodeId);
                node.Parameters.AddWithValue("roles", "[\"worker\"]");
                node.Parameters.AddWithValue("seen", DateTimeOffset.UtcNow);
                await node.ExecuteNonQueryAsync();
            }
            await using (var pending = Sql.Command("INSERT INTO node_enrollment_requests(node_id,pairing_key_hash,node_token_hash,name,roles,max_concurrent_tasks,capabilities,expires_at) VALUES(@id,@pairing,@token,'pending-worker',@roles,1,'[]',@expires)", connection))
            {
                pending.Parameters.AddWithValue("id", pendingNodeId);
                pending.Parameters.AddWithValue("pairing", SqlKind.Bytes, pairingHash);
                pending.Parameters.AddWithValue("token", SqlKind.Bytes, pendingTokenHash);
                pending.Parameters.AddWithValue("roles", "[\"worker\"]");
                pending.Parameters.AddWithValue("expires", DateTimeOffset.UtcNow.AddMinutes(10));
                await pending.ExecuteNonQueryAsync();
            }
            await using (var library = Sql.Command("INSERT INTO libraries(id,name) VALUES(@id,'Library A')", connection))
            {
                library.Parameters.AddWithValue("id", libraryId);
                await library.ExecuteNonQueryAsync();
            }
            await using (var scan = Sql.Command("INSERT INTO library_scans(id,library_id,media_type,state,completed_at,detail) VALUES(@id,@library,'video','completed',@completed,'scan')", connection))
            {
                scan.Parameters.AddWithValue("id", sourceScanId);
                scan.Parameters.AddWithValue("library", libraryId);
                scan.Parameters.AddWithValue("completed", DateTimeOffset.UtcNow);
                await scan.ExecuteNonQueryAsync();
            }
            await using (var media = Sql.Command("INSERT INTO library_media(id,library_id,relative_path,byte_length,modified_at,scan_id) VALUES(@id,@library,'movie.mkv',1234,@modified,@scan)", connection))
            {
                media.Parameters.AddWithValue("id", sourceMediaId);
                media.Parameters.AddWithValue("library", libraryId);
                media.Parameters.AddWithValue("modified", modifiedAt);
                media.Parameters.AddWithValue("scan", sourceScanId);
                await media.ExecuteNonQueryAsync();
            }
            await using (var flow = Sql.Command("INSERT INTO flows(id,current_version) VALUES(@id,1)", connection))
            {
                flow.Parameters.AddWithValue("id", flowId);
                await flow.ExecuteNonQueryAsync();
            }
            await using (var version = Sql.Command("INSERT INTO flow_versions(flow_id,version,definition) VALUES(@id,1,@definition)", connection))
            {
                version.Parameters.AddWithValue("id", flowId);
                version.Parameters.AddWithValue("definition", SqlKind.Json, "{\"id\":\"flow\",\"nodes\":[]}");
                await version.ExecuteNonQueryAsync();
            }
            await using (var execution = Sql.Command("INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,node_id,attempt) VALUES(@id,'movie.mkv','Library A','Flow A',1,@definition,'running',@node,1)", connection))
            {
                execution.Parameters.AddWithValue("id", executionId);
                execution.Parameters.AddWithValue("definition", SqlKind.Json, "{\"nodes\":[]}");
                execution.Parameters.AddWithValue("node", nodeId);
                await execution.ExecuteNonQueryAsync();
            }
            await using (var receipt = Sql.Command("INSERT INTO library_processing_receipts(library_id,file_id,flow_id,flow_version,byte_length,modified_at,execution_id) VALUES(@library,@file,@flow,1,1234,@modified,@execution)", connection))
            {
                receipt.Parameters.AddWithValue("library", libraryId);
                receipt.Parameters.AddWithValue("file", sourceMediaId);
                receipt.Parameters.AddWithValue("flow", flowId);
                receipt.Parameters.AddWithValue("modified", modifiedAt);
                receipt.Parameters.AddWithValue("execution", executionId);
                await receipt.ExecuteNonQueryAsync();
            }
        }

        await using (var connection = await target.Db.OpenConnectionAsync(CancellationToken.None))
        {
            await using (var library = Sql.Command("INSERT INTO libraries(id,name) VALUES(@id,'Local copy')", connection))
            {
                library.Parameters.AddWithValue("id", libraryId);
                await library.ExecuteNonQueryAsync();
            }
            await using (var scan = Sql.Command("INSERT INTO library_scans(id,library_id,media_type,state,completed_at,detail) VALUES(@id,@library,'video','completed',@completed,'local scan')", connection))
            {
                scan.Parameters.AddWithValue("id", targetScanId);
                scan.Parameters.AddWithValue("library", libraryId);
                scan.Parameters.AddWithValue("completed", DateTimeOffset.UtcNow.AddMinutes(-1));
                await scan.ExecuteNonQueryAsync();
            }
            await using (var media = Sql.Command("INSERT INTO library_media(id,library_id,relative_path,byte_length,modified_at,scan_id) VALUES(@id,@library,'movie.mkv',1234,@modified,@scan)", connection))
            {
                media.Parameters.AddWithValue("id", targetMediaId);
                media.Parameters.AddWithValue("library", libraryId);
                media.Parameters.AddWithValue("modified", modifiedAt);
                media.Parameters.AddWithValue("scan", targetScanId);
                await media.ExecuteNonQueryAsync();
            }
        }

        var clusterDataKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        using var sourceDataKey = new ClusterDataKeyStore(clusterId, clusterDataKey);
        using var targetDataKey = new ClusterDataKeyStore(clusterId, clusterDataKey);
        var sourceReconciliation = new ClusterEntityReconciliation(source.Db, sourceDataKey, new EphemeralDataProtectionProvider(), NullLogger<ClusterEntityReconciliation>.Instance);
        var targetReconciliation = new ClusterEntityReconciliation(target.Db, targetDataKey, new EphemeralDataProtectionProvider(), NullLogger<ClusterEntityReconciliation>.Instance);
        var sourceJournal = new ClusterReconciliationRepository(source.Db);
        var targetJournal = new ClusterReconciliationRepository(target.Db);

        Assert.True(await sourceReconciliation.CaptureLocalChangesAsync(CancellationToken.None) > 0);
        var changes = await sourceJournal.GetEventsAfterAsync(clusterId, new Dictionary<Guid, long>(), ClusterReconciliationRepository.MaximumBatchSize, CancellationToken.None);
        var receiptChange = Assert.Single(changes, change => change.Payload.Contains("library_processing_receipts", StringComparison.Ordinal));
        Assert.DoesNotContain(Convert.ToHexString(pairingHash).ToLowerInvariant(), string.Join("\n", changes.Select(change => change.Payload)), StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(pendingTokenHash).ToLowerInvariant(), string.Join("\n", changes.Select(change => change.Payload)), StringComparison.Ordinal);
        await targetJournal.ApplyIncomingAsync(changes, CancellationToken.None);
        var projected = await targetReconciliation.ApplyPendingAsync(CancellationToken.None);
        long mediaCount;
        long pendingCount;
        await using (var diagnostics = target.Db.CreateCommand("SELECT (SELECT count(*) FROM library_media WHERE library_id=@library),(SELECT count(*) FROM cluster_sync_pending_projections)"))
        {
            diagnostics.Parameters.AddWithValue("library", libraryId);
            await using var reader = await diagnostics.ExecuteReaderAsync();
            await reader.ReadAsync();
            mediaCount = reader.GetInt64(0);
            pendingCount = reader.GetInt64(1);
        }
        Assert.True(Convert.ToInt64(await ScalarAsync(target.Db, "SELECT count(*) FROM library_processing_receipts WHERE library_id=@library", "library", libraryId)) > 0,
            $"Change={receiptChange.EventType}:{receiptChange.Payload}; projection count={projected}, media rows={mediaCount}, pending events={pendingCount}.");

        await using (var receipt = target.Db.CreateCommand("SELECT file_id FROM library_processing_receipts WHERE library_id=@library"))
        {
            receipt.Parameters.AddWithValue("library", libraryId);
            Assert.Equal(targetMediaId, Guid.Parse(Convert.ToString(await receipt.ExecuteScalarAsync())!));
        }
        await using (var jobs = target.Db.CreateCommand("SELECT count(*) FROM flow_backup_jobs WHERE flow_id=@flow AND version=1 AND status='pending'"))
        {
            jobs.Parameters.AddWithValue("flow", flowId);
            Assert.Equal(1L, Convert.ToInt64(await jobs.ExecuteScalarAsync()));
        }
        await using (var pending = target.Db.CreateCommand("SELECT pairing_key_hash,node_token_hash FROM node_enrollment_requests WHERE node_id=@id"))
        {
            pending.Parameters.AddWithValue("id", pendingNodeId);
            await using var reader = await pending.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(pairingHash, reader.GetFieldValue<byte[]>(0));
            Assert.Equal(pendingTokenHash, reader.GetFieldValue<byte[]>(1));
        }
    }

    private static async Task<object?> ScalarAsync(System.Data.Common.DbDataSource dataSource, string sql, string parameter, object value)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(parameter, value);
        return await command.ExecuteScalarAsync();
    }
}
