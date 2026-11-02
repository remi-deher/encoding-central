using Encoding.Api.Infrastructure;
using Encoding.Api.Nodes;
using Encoding.Contracts.Nodes;
using Encoding.NodeAgent.Runtime;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class NodeDirectoryBrowserTests
{
    [Fact]
    public async Task EmptyPollDoesNotWaitForSqliteWriter()
    {
        await using var database = await TestDatabase.CreateSqliteAsync("node-directory-browser-idle");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        await using var holder = await database.Db.OpenConnectionAsync();
        await using var held = await holder.BeginTransactionAsync();

        var claim = await new NodeDirectoryBrowserRepository(database.Db)
            .ClaimAsync(Guid.NewGuid(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(claim);
    }

    [Fact]
    public async Task BrowseRequestIsClaimedByTargetNodeAndReturnsDirectories()
    {
        await using var database = await TestDatabase.CreateSqliteAsync("node-directory-browser");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        var nodeId = Guid.NewGuid();
        await using (var insert = database.Db.CreateCommand())
        {
            insert.CommandText = "INSERT INTO cluster_nodes(id,name,roles,max_concurrent_tasks,last_seen_at) VALUES(@id,'worker','worker',1,@seen)";
            var id = insert.CreateParameter();
            id.ParameterName = "@id";
            id.Value = nodeId;
            insert.Parameters.Add(id);
            var seen = insert.CreateParameter();
            seen.ParameterName = "@seen";
            seen.Value = DateTimeOffset.UtcNow;
            insert.Parameters.Add(seen);
            await insert.ExecuteNonQueryAsync();
        }

        var repository = new NodeDirectoryBrowserRepository(database.Db);
        var requestId = Assert.IsType<Guid>(await repository.CreateAsync(nodeId, "/media", CancellationToken.None));
        Assert.Equal("queued", (await repository.GetAsync(requestId, nodeId, CancellationToken.None))?.State);
        Assert.Null(await repository.ClaimAsync(Guid.NewGuid(), CancellationToken.None));
        var claim = await repository.ClaimAsync(nodeId, CancellationToken.None);
        Assert.Equal(new NodeDirectoryBrowseClaim(requestId, "/media"), claim);

        var result = new NodeDirectoryBrowseCompletion([new("Films", "/media/Films")], true, null);
        Assert.True(await repository.CompleteAsync(requestId, nodeId, result, CancellationToken.None));
        var response = await repository.GetAsync(requestId, nodeId, CancellationToken.None);
        Assert.Equal("completed", response?.State);
        Assert.Equal(result.Entries, response?.Entries);
        Assert.True(response?.HasMore);
        Assert.Null(await repository.ClaimAsync(nodeId, CancellationToken.None));
    }

    [Fact]
    public void AgentListsOnlyImmediateDirectoriesAndCapsLargeResults()
    {
        var root = Path.Combine(Path.GetTempPath(), "encoding-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (var index = 0; index < 202; index++) Directory.CreateDirectory(Path.Combine(root, $"folder-{index:D3}"));
            Directory.CreateDirectory(Path.Combine(root, ".encoding-private"));
            File.WriteAllText(Path.Combine(root, "movie.mkv"), "not listed");

            var result = NodeDirectoryBrowserService.Browse(root, CancellationToken.None);

            Assert.Null(result.Error);
            var entries = Assert.IsAssignableFrom<IReadOnlyList<NodeDirectoryBrowserEntry>>(result.Entries);
            Assert.Equal(200, entries.Count);
            Assert.True(result.HasMore);
            Assert.DoesNotContain(entries, entry => entry.Name.StartsWith(".encoding", StringComparison.Ordinal));
            Assert.DoesNotContain(entries, entry => entry.Name == "movie.mkv");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
