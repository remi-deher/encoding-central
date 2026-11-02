using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Nodes;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Architecture 0043 : garanties propres à un cluster sur SQLite (ne demandent aucune base externe).</summary>
public sealed class SqliteClusterTests
{
    private static async Task<TestDatabase> NewAsync()
    {
        var database = await TestDatabase.CreateSqliteAsync("sqlcluster");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        return database;
    }

    private static async Task InsertNodeAsync(TestDatabase database, Guid id, string roles)
    {
        await using var command = database.Db.CreateCommand(
            "INSERT INTO cluster_nodes(id,name,roles,max_concurrent_tasks,capabilities,last_seen_at,node_token_hash) VALUES(@id,@name,@roles,1,@caps,clock_timestamp(),@token)");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", "node-" + id.ToString("N")[..6]);
        command.Parameters.AddWithValue("roles", SqlKind.TextArray, roles.Split(','));
        command.Parameters.AddWithValue("caps", SqlKind.Json, "[]");
        command.Parameters.AddWithValue("token", SqlKind.Bytes, id.ToByteArray());
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ASecondServerCanBeEnabledOnAnSqliteCluster()
    {
        await using var database = await NewAsync();
        var server = Guid.NewGuid(); var worker = Guid.NewGuid();
        await InsertNodeAsync(database, server, "server,worker");
        await InsertNodeAsync(database, worker, "worker");
        var repository = new NodeRepository(database.Db);
        Assert.Equal(NodeRepository.ModeRequestResult.Accepted, await repository.RequestModeAsync(worker, true, true, TimeSpan.FromMinutes(5), CancellationToken.None));
    }

    [Fact]
    public async Task LastServerCannotLeaveServerMode()
    {
        await using var database = await NewAsync();
        var server = Guid.NewGuid();
        await InsertNodeAsync(database, server, "server,worker");
        var repository = new NodeRepository(database.Db);
        Assert.Equal(NodeRepository.ModeRequestResult.LastServer, await repository.RequestModeAsync(server, false, true, TimeSpan.FromMinutes(5), CancellationToken.None));
    }

    [Fact]
    public async Task ReplicatedSqliteAllowsMultipleServerRoles()
    {
        await using var database = await NewAsync();
        var server = Guid.NewGuid(); var worker = Guid.NewGuid();
        await InsertNodeAsync(database, server, "server,worker");
        await InsertNodeAsync(database, worker, "worker");
        var repository = new NodeRepository(database.Db);
        Assert.Equal(NodeRepository.ModeRequestResult.Accepted,
            await repository.RequestModeAsync(worker, true, true, TimeSpan.FromMinutes(5), CancellationToken.None));
    }
}
