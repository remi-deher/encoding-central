using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class ClusterReconciliationTests
{
    private static async Task<TestDatabase> NewAsync()
    {
        var database = await TestDatabase.CreateSqliteAsync("reconciliation");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        return database;
    }

    [Fact]
    public async Task NameChangesRelayAcrossPeersAndDuplicateDeliveryIsIdempotent()
    {
        await using var first = await NewAsync();
        await using var second = await NewAsync();
        await using var third = await NewAsync();
        var clusterId = Guid.NewGuid();
        await new ClusterIdentityRepository(first.Db).AdoptAsync(clusterId, "Media", CancellationToken.None);
        await new ClusterIdentityRepository(second.Db).AdoptAsync(clusterId, "Media", CancellationToken.None);
        await new ClusterIdentityRepository(third.Db).AdoptAsync(clusterId, "Media", CancellationToken.None);
        var source = new ClusterReconciliationRepository(first.Db);
        var relay = new ClusterReconciliationRepository(second.Db);
        var destination = new ClusterReconciliationRepository(third.Db);

        await source.SetRegisterAsync("cluster.name", JsonSerializer.Serialize("Media"), CancellationToken.None);
        var firstBatch = await source.GetEventsAfterAsync(clusterId, new Dictionary<Guid, long>(), ClusterReconciliationRepository.MaximumBatchSize, CancellationToken.None);
        Assert.Single(firstBatch);

        await relay.ApplyIncomingAsync(firstBatch, CancellationToken.None);
        await relay.ApplyIncomingAsync(firstBatch, CancellationToken.None);
        Assert.Equal("Media", await ClusterNameAsync(second.Db));
        Assert.Equal(1L, await CountEventsAsync(second.Db));

        var secondBatch = await relay.GetEventsAfterAsync(clusterId, new Dictionary<Guid, long>(), ClusterReconciliationRepository.MaximumBatchSize, CancellationToken.None);
        await destination.ApplyIncomingAsync(secondBatch, CancellationToken.None);
        Assert.Equal("Media", await ClusterNameAsync(third.Db));
        var vector = await destination.GetVectorAsync(CancellationToken.None);
        Assert.Equal(1L, vector[firstBatch[0].OriginId]);
    }

    [Fact]
    public async Task ConcurrentRegisterWritesUseTimestampThenStableOriginTieBreak()
    {
        await using var database = await NewAsync();
        var clusterId = Guid.NewGuid();
        await new ClusterIdentityRepository(database.Db).AdoptAsync(clusterId, "Media", CancellationToken.None);
        var repository = new ClusterReconciliationRepository(database.Db);
        var instant = DateTimeOffset.Parse("2026-10-17T12:00:00Z");
        var lowerOrigin = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var higherOrigin = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var newer = Change(clusterId, higherOrigin, 1, "latest", instant);
        var older = Change(clusterId, lowerOrigin, 1, "earlier", instant.AddSeconds(-1));
        var tieWinner = Change(clusterId, higherOrigin, 2, "tie winner", instant);
        var tieLoser = Change(clusterId, lowerOrigin, 2, "tie loser", instant);

        await repository.ApplyIncomingAsync([newer, older, tieWinner, tieLoser], CancellationToken.None);

        Assert.Equal("tie winner", await ClusterNameAsync(database.Db));
        Assert.Equal(4L, await CountEventsAsync(database.Db));
    }

    [Fact]
    public async Task JoiningAnotherClusterIsolatesThePreviousJournalAndRegisters()
    {
        await using var database = await NewAsync();
        var repository = new ClusterReconciliationRepository(database.Db);
        var identities = new ClusterIdentityRepository(database.Db);
        var oldCluster = Guid.NewGuid();
        var newCluster = Guid.NewGuid();
        await identities.AdoptAsync(oldCluster, "Old cluster", CancellationToken.None);
        await repository.SetRegisterAsync("cluster.name", JsonSerializer.Serialize("Old cluster"), CancellationToken.None);
        var oldEvents = await repository.GetEventsAfterAsync(oldCluster, new Dictionary<Guid, long>(), ClusterReconciliationRepository.MaximumBatchSize, CancellationToken.None);
        var oldOrigin = oldEvents.Single().OriginId;

        await identities.AdoptAsync(newCluster, "New cluster", CancellationToken.None);
        Assert.Empty(await repository.GetEventsAfterAsync(newCluster, new Dictionary<Guid, long>(), ClusterReconciliationRepository.MaximumBatchSize, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ApplyIncomingAsync(oldEvents, CancellationToken.None));

        var remoteOrigin = Guid.NewGuid();
        var remoteName = Change(newCluster, remoteOrigin, 1, "New cluster", DateTimeOffset.UtcNow.AddMinutes(-5));
        await repository.ApplyIncomingAsync([remoteName], CancellationToken.None);

        Assert.Equal("New cluster", await ClusterNameAsync(database.Db));
        var newEvents = await repository.GetEventsAfterAsync(newCluster, new Dictionary<Guid, long>(), ClusterReconciliationRepository.MaximumBatchSize, CancellationToken.None);
        Assert.Single(newEvents);
        Assert.NotEqual(oldOrigin, newEvents[0].OriginId);
    }

    [Fact]
    public void ExchangeSignatureAuthenticatesBodyAndRejectsExpiredMessages()
    {
        var directory = Path.Combine(Path.GetTempPath(), "encoding-signature-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PluginSettings:KeyDirectory"] = directory
        }).Build();
        var protection = new EphemeralDataProtectionProvider();
        using var identity = new ClusterPeerIdentityStore(configuration, protection);
        var body = "{\"clusterId\":\"test\"}"u8.ToArray();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var signature = ClusterReconciliationSignature.Create(identity, timestamp, body);

        Assert.True(ClusterReconciliationSignature.Verify(identity.PublicKey, identity.PeerId, timestamp, signature, body));
        Assert.False(ClusterReconciliationSignature.Verify(identity.PublicKey, identity.PeerId, timestamp, signature, "{}"u8));
        Assert.False(ClusterReconciliationSignature.Verify(identity.PublicKey, identity.PeerId, "1", signature, body));
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task PeerCursorAdvancesAndNeverMovesBackwards()
    {
        await using var database = await NewAsync();
        var repository = new ClusterReconciliationRepository(database.Db);
        var peer = "http://server-b:8080";
        var origin = Guid.NewGuid();

        await repository.SavePeerVectorAsync(peer, new Dictionary<Guid, long> { [origin] = 4 }, CancellationToken.None);
        await repository.SavePeerVectorAsync(peer, new Dictionary<Guid, long> { [origin] = 2 }, CancellationToken.None);

        var vector = await repository.GetPeerVectorAsync(peer + "/", CancellationToken.None);
        Assert.Equal(4L, vector[origin]);
    }

    [Fact]
    public async Task MeshReplicatesClusterDataAndExecutionHistoryButNotWorkerLeases()
    {
        await using var first = await NewAsync();
        await using var second = await NewAsync();
        var clusterId = Guid.NewGuid();
        await new ClusterIdentityRepository(first.Db).AdoptAsync(clusterId, "Media", CancellationToken.None);
        await new ClusterIdentityRepository(second.Db).AdoptAsync(clusterId, "Media", CancellationToken.None);

        var nodeId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        var scanId = Guid.NewGuid();
        var mediaId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var destinationScanId = Guid.NewGuid();
        var destinationMediaId = Guid.NewGuid();
        var pendingNodeId = Guid.NewGuid();
        var passwordHash = "password-hash-for-test";
        var tokenHash = System.Security.Cryptography.SHA256.HashData("opaque-worker-token"u8);
        var pairingHash = System.Security.Cryptography.SHA256.HashData("opaque-pairing-code"u8);
        var pendingTokenHash = System.Security.Cryptography.SHA256.HashData("opaque-pending-worker-token"u8);
        var sourceProtection = new EphemeralDataProtectionProvider();
        var targetProtection = new EphemeralDataProtectionProvider();
        var pluginValues = "{\"endpoint\":\"https://service.local\",\"apiKey\":\"super-secret-value\"}"u8.ToArray();
        await using (var connection = await second.Db.OpenConnectionAsync(CancellationToken.None))
        {
            // Simulate this node discovering the same path independently and assigning its own media ID.
            await using (var library = Sql.Command("INSERT INTO libraries(id,name) VALUES(@id,'Local copy')", connection))
            {
                library.Parameters.AddWithValue("id", libraryId);
                await library.ExecuteNonQueryAsync();
            }
            await using (var scan = Sql.Command("INSERT INTO library_scans(id,library_id,media_type,state,completed_at,detail) VALUES(@id,@library,'video','completed',@completed,'local scan')", connection))
            {
                scan.Parameters.AddWithValue("id", destinationScanId);
                scan.Parameters.AddWithValue("library", libraryId);
                scan.Parameters.AddWithValue("completed", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
                await scan.ExecuteNonQueryAsync();
            }
            await using (var media = Sql.Command("INSERT INTO library_media(id,library_id,relative_path,byte_length,modified_at,scan_id) VALUES(@id,@library,'movie.mkv',1234,@modified,@scan)", connection))
            {
                media.Parameters.AddWithValue("id", destinationMediaId);
                media.Parameters.AddWithValue("library", libraryId);
                media.Parameters.AddWithValue("modified", DateTimeOffset.UtcNow.ToString("O"));
                media.Parameters.AddWithValue("scan", destinationScanId);
                await media.ExecuteNonQueryAsync();
            }
        }
        await using (var connection = await first.Db.OpenConnectionAsync(CancellationToken.None))
        {
            await using (var node = Sql.Command("INSERT INTO cluster_nodes(id,name,roles,max_concurrent_tasks,capabilities,last_seen_at,node_token_hash) VALUES(@id,'worker-a',@roles,2,'[]',@seen,@token)", connection))
            {
                node.Parameters.AddWithValue("id", nodeId);
                node.Parameters.AddWithValue("roles", "[\"worker\"]");
                node.Parameters.AddWithValue("seen", DateTimeOffset.UtcNow.ToString("O"));
                node.Parameters.AddWithValue("token", SqlKind.Bytes, tokenHash);
                await node.ExecuteNonQueryAsync();
            }
            await using (var pending = Sql.Command("INSERT INTO node_enrollment_requests(node_id,pairing_key_hash,node_token_hash,name,roles,max_concurrent_tasks,capabilities,expires_at) VALUES(@id,@pairing,@token,'pending-worker','[\"worker\"]',1,'[]',@expires)", connection))
            {
                pending.Parameters.AddWithValue("id", pendingNodeId);
                pending.Parameters.AddWithValue("pairing", SqlKind.Bytes, pairingHash);
                pending.Parameters.AddWithValue("token", SqlKind.Bytes, pendingTokenHash);
                pending.Parameters.AddWithValue("expires", DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"));
                await pending.ExecuteNonQueryAsync();
            }
            await using (var library = Sql.Command("INSERT INTO libraries(id,name) VALUES(@id,'Library A')", connection))
            {
                library.Parameters.AddWithValue("id", libraryId);
                await library.ExecuteNonQueryAsync();
            }
            await using (var settings = Sql.Command("INSERT INTO library_processing_settings(library_id,watch_enabled,scan_interval_seconds,revision) VALUES(@id,true,120,3)", connection))
            {
                settings.Parameters.AddWithValue("id", libraryId);
                await settings.ExecuteNonQueryAsync();
            }
            await using (var scan = Sql.Command("INSERT INTO library_scans(id,library_id,media_type,state,completed_at,detail) VALUES(@id,@library,'video','completed',@completed,'1 fichier détecté.')", connection))
            {
                scan.Parameters.AddWithValue("id", scanId);
                scan.Parameters.AddWithValue("library", libraryId);
                scan.Parameters.AddWithValue("completed", DateTimeOffset.UtcNow.ToString("O"));
                await scan.ExecuteNonQueryAsync();
            }
            await using (var media = Sql.Command("INSERT INTO library_media(id,library_id,relative_path,byte_length,modified_at,scan_id) VALUES(@id,@library,'movie.mkv',1234,@modified,@scan)", connection))
            {
                media.Parameters.AddWithValue("id", mediaId);
                media.Parameters.AddWithValue("library", libraryId);
                media.Parameters.AddWithValue("modified", DateTimeOffset.UtcNow.ToString("O"));
                media.Parameters.AddWithValue("scan", scanId);
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
            await using (var backup = Sql.Command("INSERT INTO flow_backup_jobs(flow_id,version) VALUES(@id,1)", connection))
            {
                backup.Parameters.AddWithValue("id", flowId);
                await backup.ExecuteNonQueryAsync();
            }
            await using (var draft = Sql.Command("INSERT INTO flow_drafts(flow_id,revision,definition) VALUES(@id,2,@definition)", connection))
            {
                draft.Parameters.AddWithValue("id", flowId);
                draft.Parameters.AddWithValue("definition", SqlKind.Json, "{\"id\":\"flow\",\"draft\":true,\"nodes\":[]}");
                await draft.ExecuteNonQueryAsync();
            }
            await using (var draftBackup = Sql.Command("INSERT INTO flow_draft_backups(flow_id,revision,definition) VALUES(@id,2,@definition)", connection))
            {
                draftBackup.Parameters.AddWithValue("id", flowId);
                draftBackup.Parameters.AddWithValue("definition", SqlKind.Json, "{\"id\":\"flow\",\"draft\":true,\"nodes\":[]}");
                await draftBackup.ExecuteNonQueryAsync();
            }
            await using (var assignment = Sql.Command("INSERT INTO library_flow_assignments(library_id,flow_id,flow_version) VALUES(@library,@flow,1)", connection))
            {
                assignment.Parameters.AddWithValue("library", libraryId);
                assignment.Parameters.AddWithValue("flow", flowId);
                await assignment.ExecuteNonQueryAsync();
            }
            await using (var user = Sql.Command("INSERT INTO users(id,username,password_hash,role) VALUES(@id,'admin',@password,'admin')", connection))
            {
                user.Parameters.AddWithValue("id", Guid.NewGuid());
                user.Parameters.AddWithValue("password", passwordHash);
                await user.ExecuteNonQueryAsync();
            }
            var pluginDataProtector = sourceProtection.CreateProtector("Encoding.PluginConfiguration.v1");
            await using (var pluginConfig = Sql.Command("INSERT INTO plugin_configurations(plugin_id,revision,encrypted_values) VALUES('plugin.test',1,@values)", connection))
            {
                pluginConfig.Parameters.AddWithValue("values", SqlKind.Bytes, pluginDataProtector.Protect(pluginValues));
                await pluginConfig.ExecuteNonQueryAsync();
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
                receipt.Parameters.AddWithValue("file", mediaId);
                receipt.Parameters.AddWithValue("flow", flowId);
                receipt.Parameters.AddWithValue("modified", await ScalarAsync(first.Db, "SELECT modified_at FROM library_media WHERE id=@id", mediaId));
                receipt.Parameters.AddWithValue("execution", executionId);
                await receipt.ExecuteNonQueryAsync();
            }
            await using (var work = Sql.Command("INSERT INTO execution_work(execution_id,library_id,media_path,media_key,worker_node_id,lease_epoch,lease_until) VALUES(@execution,@library,'movie.mkv','movie',@node,4,@lease)", connection))
            {
                work.Parameters.AddWithValue("execution", executionId);
                work.Parameters.AddWithValue("library", libraryId);
                work.Parameters.AddWithValue("node", nodeId);
                work.Parameters.AddWithValue("lease", DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"));
                await work.ExecuteNonQueryAsync();
            }
        }

        var clusterDataKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        using var sourceDataKey = new ClusterDataKeyStore(clusterId, clusterDataKey);
        using var targetDataKey = new ClusterDataKeyStore(clusterId, clusterDataKey);
        var entitySyncFirst = new ClusterEntityReconciliation(first.Db, sourceDataKey, sourceProtection, NullLogger<ClusterEntityReconciliation>.Instance);
        var entitySyncSecond = new ClusterEntityReconciliation(second.Db, targetDataKey, targetProtection, NullLogger<ClusterEntityReconciliation>.Instance);
        var source = new ClusterReconciliationRepository(first.Db);
        var target = new ClusterReconciliationRepository(second.Db);

        Assert.True(await entitySyncFirst.CaptureLocalChangesAsync(CancellationToken.None) > 0);
        var events = await source.GetEventsAfterAsync(clusterId, new Dictionary<Guid, long>(), ClusterReconciliationRepository.MaximumBatchSize, CancellationToken.None);
        Assert.DoesNotContain("super-secret-value", string.Join("\n", events.Select(change => change.Payload)), StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(pairingHash).ToLowerInvariant(), string.Join("\n", events.Select(change => change.Payload)), StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(pendingTokenHash).ToLowerInvariant(), string.Join("\n", events.Select(change => change.Payload)), StringComparison.Ordinal);
        await target.ApplyIncomingAsync(events, CancellationToken.None);
        await entitySyncSecond.ApplyPendingAsync(CancellationToken.None);
        await target.ApplyIncomingAsync(events, CancellationToken.None);
        await entitySyncSecond.ApplyPendingAsync(CancellationToken.None);

        Assert.Equal("Library A", await ScalarAsync(second.Db, "SELECT name FROM libraries WHERE id=@id", libraryId));
        Assert.Equal(120L, Convert.ToInt64(await ScalarAsync(second.Db, "SELECT scan_interval_seconds FROM library_processing_settings WHERE library_id=@id", libraryId)));
        Assert.Equal("{\"id\":\"flow\",\"nodes\":[]}", await ScalarAsync(second.Db, "SELECT definition FROM flow_versions WHERE flow_id=@id AND version=1", flowId));
        Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(second.Db, "SELECT count(*) FROM flow_backup_jobs WHERE flow_id=@id AND version=1 AND status='pending'", flowId)));
        Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(second.Db, "SELECT count(*) FROM flow_draft_backups WHERE flow_id=@id AND revision=2 AND status='pending'", flowId)));
        Assert.Equal("{\"id\":\"flow\",\"draft\":true,\"nodes\":[]}", await ScalarAsync(second.Db, "SELECT definition FROM flow_drafts WHERE flow_id=@id", flowId));
        Assert.Equal("running", await ScalarAsync(second.Db, "SELECT state FROM executions WHERE id=@id", executionId));
        Assert.Equal("movie.mkv", await ScalarAsync(second.Db, "SELECT relative_path FROM library_media WHERE library_id=@id", libraryId));
        Assert.Equal(destinationMediaId, Guid.Parse(Convert.ToString(await ScalarAsync(second.Db, "SELECT file_id FROM library_processing_receipts WHERE library_id=@id", libraryId))!));
        Assert.Equal(passwordHash, await ScalarAsync(second.Db, "SELECT password_hash FROM users WHERE username='admin'"));
        var destinationPluginCipher = Assert.IsType<byte[]>(await ScalarAsync(second.Db, "SELECT encrypted_values FROM plugin_configurations WHERE plugin_id='plugin.test'"));
        var destinationPluginProtector = targetProtection.CreateProtector("Encoding.PluginConfiguration.v1");
        Assert.Equal(pluginValues, destinationPluginProtector.Unprotect(destinationPluginCipher));
        Assert.Equal(tokenHash, Assert.IsType<byte[]>(await ScalarAsync(second.Db, "SELECT node_token_hash FROM cluster_nodes WHERE id=@id", nodeId)));
        Assert.Equal(pairingHash, Assert.IsType<byte[]>(await ScalarAsync(second.Db, "SELECT pairing_key_hash FROM node_enrollment_requests WHERE node_id=@id", pendingNodeId)));
        Assert.Equal(pendingTokenHash, Assert.IsType<byte[]>(await ScalarAsync(second.Db, "SELECT node_token_hash FROM node_enrollment_requests WHERE node_id=@id", pendingNodeId)));
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(second.Db, "SELECT count(*) FROM execution_work")));
        Assert.Equal((long)events.Count, await CountEventsAsync(second.Db));
        Assert.Equal(0, await entitySyncFirst.CaptureLocalChangesAsync(CancellationToken.None));

        var targetVector = await target.GetVectorAsync(CancellationToken.None);
        await using (var delete = first.Db.CreateCommand("DELETE FROM library_processing_settings WHERE library_id=@id"))
        {
            delete.Parameters.AddWithValue("id", libraryId);
            await delete.ExecuteNonQueryAsync();
        }
        Assert.Equal(1, await entitySyncFirst.CaptureLocalChangesAsync(CancellationToken.None));
        var tombstones = await source.GetEventsAfterAsync(clusterId, targetVector, ClusterReconciliationRepository.MaximumBatchSize, CancellationToken.None);
        Assert.Single(tombstones);
        await target.ApplyIncomingAsync(tombstones, CancellationToken.None);
        await entitySyncSecond.ApplyPendingAsync(CancellationToken.None);
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(second.Db, "SELECT count(*) FROM library_processing_settings WHERE library_id=@id", libraryId)));

        var pendingRequests = await new Encoding.Api.Nodes.NodeRepository(second.Db).ListPendingPairingRequestsAsync(CancellationToken.None);
        Assert.Contains(pendingRequests, request => request.Id == pendingNodeId);
        var beforeApproval = await target.GetVectorAsync(CancellationToken.None);
        Assert.NotNull(await new Encoding.Api.Nodes.NodeRepository(second.Db).ApprovePairingRequestAsync("opaque-pairing-code", CancellationToken.None));
        Assert.True(await entitySyncSecond.CaptureLocalChangesAsync(CancellationToken.None) >= 3);
        var approvalChanges = await target.GetEventsAfterAsync(clusterId, beforeApproval, ClusterReconciliationRepository.MaximumBatchSize, CancellationToken.None);
        await source.ApplyIncomingAsync(approvalChanges, CancellationToken.None);
        await entitySyncFirst.ApplyPendingAsync(CancellationToken.None);
        Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(first.Db, "SELECT count(*) FROM node_enrollment_requests WHERE node_id=@id", pendingNodeId)));
        Assert.Equal("pending-worker", await ScalarAsync(first.Db, "SELECT name FROM cluster_nodes WHERE id=@id", pendingNodeId));
    }

    private static async Task<object?> ScalarAsync(System.Data.Common.DbDataSource database, string sql, object? id = null)
    {
        await using var command = database.CreateCommand(sql);
        if (id is not null) command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync();
    }

    private static ClusterChangeEvent Change(Guid clusterId, Guid origin, long sequence, string name, DateTimeOffset occurredAt) => new(
        Guid.NewGuid(), clusterId, origin, sequence, "register.set",
        JsonSerializer.Serialize(new { key = "cluster.name", value = JsonSerializer.Serialize(name) }), occurredAt);

    private static async Task<string> ClusterNameAsync(System.Data.Common.DbDataSource database)
    {
        await using var command = database.CreateCommand("SELECT name FROM cluster_identity WHERE singleton = true");
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountEventsAsync(System.Data.Common.DbDataSource database)
    {
        await using var command = database.CreateCommand("SELECT count(*) FROM cluster_change_log");
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
