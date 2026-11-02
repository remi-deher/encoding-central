using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Nodes;
using Encoding.Contracts.Nodes;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class HeartbeatWriteTests
{
    [Fact]
    public async Task StableHeartbeatsWritePresenceWithoutRewritingMetadataOrSession()
    {
        await using var database = await TestDatabase.CreateSqliteAsync("heartbeat-writes");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        var nodes = new NodeRepository(database.Db);
        var id = Guid.NewGuid();
        const string token = "test-heartbeat-token";
        Assert.NotNull(await nodes.EnrollAsync(new(id, "worker", [NodeRole.Worker], 1, [], token), true, CancellationToken.None));
        var heartbeat = new NodeHeartbeatRequest("worker", [NodeRole.Worker], 1, [], ProtocolVersion: ClusterProtocol.Current, SessionNonce: "first");
        Assert.NotNull(await nodes.RecordHeartbeatAsync(id, token, heartbeat, CancellationToken.None));
        await ExecuteAsync(database, """
            CREATE TABLE heartbeat_write_counts(kind TEXT NOT NULL);
            CREATE TRIGGER count_heartbeat_metadata AFTER UPDATE OF name,roles,max_concurrent_tasks,capabilities,inventory ON cluster_nodes
            BEGIN INSERT INTO heartbeat_write_counts VALUES('metadata'); END;
            CREATE TRIGGER count_heartbeat_session AFTER UPDATE OF protocol_version,session_nonce ON cluster_nodes
            BEGIN INSERT INTO heartbeat_write_counts VALUES('session'); END;
            """);
        for (var i = 0; i < 10; i++)
            Assert.NotNull(await nodes.RecordHeartbeatAsync(id, token, heartbeat, CancellationToken.None));
        Assert.Equal(0L, await ScalarAsync(database, "SELECT count(*) FROM heartbeat_write_counts"));
        var changed = await nodes.RecordHeartbeatAsync(id, token, heartbeat with { Name = "renamed", MaxConcurrentTasks = 2 }, CancellationToken.None);
        Assert.Equal("renamed", changed?.Name);
        Assert.Equal(2, changed?.MaxConcurrentTasks);
        Assert.Equal(1L, await ScalarAsync(database, "SELECT count(*) FROM heartbeat_write_counts WHERE kind='metadata'"));
        Assert.Null(await nodes.RecordHeartbeatAsync(id, "wrong-token", heartbeat, CancellationToken.None));
        Assert.Equal(1L, await ScalarAsync(database, "SELECT count(*) FROM heartbeat_write_counts"));
    }

    [Fact]
    public async Task SessionChangesStillDetectDuplicateProcessesAndProtocolIncompatibility()
    {
        await using var database = await TestDatabase.CreateSqliteAsync("heartbeat-session");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        var nodes = new NodeRepository(database.Db);
        var id = Guid.NewGuid();
        const string token = "test-session-token";
        Assert.NotNull(await nodes.EnrollAsync(new(id, "worker", [NodeRole.Worker], 1, [], token), true, CancellationToken.None));
        var request = new NodeHeartbeatRequest("worker", [NodeRole.Worker], 1, [], ProtocolVersion: ClusterProtocol.Current, SessionNonce: "one");
        await nodes.RecordHeartbeatAsync(id, token, request, CancellationToken.None);
        await nodes.RecordHeartbeatAsync(id, token, request with { SessionNonce = "two" }, CancellationToken.None);
        var duplicate = await nodes.RecordHeartbeatAsync(id, token, request, CancellationToken.None);
        Assert.Equal(NodeLifecycle.Quarantined, duplicate?.Lifecycle);
        Assert.True(await nodes.ClearQuarantineAsync(id, CancellationToken.None));
        var incompatible = await nodes.RecordHeartbeatAsync(id, token, request with { ProtocolVersion = ClusterProtocol.Current + 1 }, CancellationToken.None);
        Assert.Equal(NodeLifecycle.Incompatible, incompatible?.Lifecycle);
        var recovered = await nodes.RecordHeartbeatAsync(id, token, request, CancellationToken.None);
        Assert.Equal(NodeLifecycle.Online, recovered?.Lifecycle);
    }

    [Fact]
    public async Task AcknowledgingRequestedRolesStillClearsTheRequest()
    {
        await using var database = await TestDatabase.CreateSqliteAsync("heartbeat-roles");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        var nodes = new NodeRepository(database.Db);
        var id = Guid.NewGuid();
        Assert.NotNull(await nodes.EnrollAsync(new(id, "worker", [NodeRole.Worker], 1, [], "roles-token"), true, CancellationToken.None));
        await using (var desired = database.Db.CreateCommand("UPDATE cluster_nodes SET desired_roles='[\"worker\"]' WHERE id=@id"))
        {
            desired.Parameters.AddWithValue("id", id);
            await desired.ExecuteNonQueryAsync();
        }
        var node = await nodes.RecordHeartbeatAsync(id, "roles-token", new("worker", [NodeRole.Worker], 1, []), CancellationToken.None);
        Assert.NotNull(node);
        Assert.Null(node.DesiredRoles);
    }

    private static async Task ExecuteAsync(TestDatabase database, string sql)
    {
        await using var command = database.Db.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(TestDatabase database, string sql)
    {
        await using var command = database.Db.CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }
}
