using Encoding.Api.Infrastructure;
using Encoding.Api.Workers;
using Encoding.Contracts.Workers;
using Encoding.WorkerAgent.Runtime;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class WorkerDirectoryBrowserTests
{
    [Fact]
    public async Task EmptyPollDoesNotWaitForSqliteWriter()
    {
        await using var database = await TestDatabase.CreateSqliteAsync("node-directory-browser-idle");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        await using var holder = await database.Db.OpenConnectionAsync();
        await using var held = await holder.BeginTransactionAsync();

        var claim = await new WorkerDirectoryBrowserRepository(database.Db)
            .ClaimAsync(Guid.NewGuid(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(claim);
    }

    [Fact]
    public async Task BrowseRequestIsClaimedByTargetWorkerAndReturnsDirectories()
    {
        await using var database = await TestDatabase.CreateSqliteAsync("node-directory-browser");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        var workerId = Guid.NewGuid();
        await using (var insert = database.Db.CreateCommand())
        {
            insert.CommandText = "INSERT INTO workers(id,name,roles,max_concurrent_tasks,last_seen_at) VALUES(@id,'worker','worker',1,@seen)";
            var id = insert.CreateParameter();
            id.ParameterName = "@id";
            id.Value = workerId;
            insert.Parameters.Add(id);
            var seen = insert.CreateParameter();
            seen.ParameterName = "@seen";
            seen.Value = DateTimeOffset.UtcNow;
            insert.Parameters.Add(seen);
            await insert.ExecuteNonQueryAsync();
        }

        var repository = new WorkerDirectoryBrowserRepository(database.Db);
        var requestId = Assert.IsType<Guid>(await repository.CreateAsync(workerId, "/media", CancellationToken.None));
        Assert.Equal("queued", (await repository.GetAsync(requestId, workerId, CancellationToken.None))?.State);
        Assert.Null(await repository.ClaimAsync(Guid.NewGuid(), CancellationToken.None));
        var claim = await repository.ClaimAsync(workerId, CancellationToken.None);
        Assert.Equal(new WorkerDirectoryBrowseClaim(requestId, "/media"), claim);

        var result = new WorkerDirectoryBrowseCompletion([new("Films", "/media/Films")], true, null);
        Assert.True(await repository.CompleteAsync(requestId, workerId, result, CancellationToken.None));
        var response = await repository.GetAsync(requestId, workerId, CancellationToken.None);
        Assert.Equal("completed", response?.State);
        Assert.Equal(result.Entries, response?.Entries);
        Assert.True(response?.HasMore);
        Assert.Null(await repository.ClaimAsync(workerId, CancellationToken.None));
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

            var result = WorkerDirectoryBrowserService.Browse(root, CancellationToken.None);

            Assert.Null(result.Error);
            var entries = Assert.IsAssignableFrom<IReadOnlyList<WorkerDirectoryBrowserEntry>>(result.Entries);
            Assert.Equal(200, entries.Count);
            Assert.True(result.HasMore);
            Assert.DoesNotContain(entries, entry => entry.Name.StartsWith(".encoding", StringComparison.Ordinal));
            Assert.DoesNotContain(entries, entry => entry.Name == "movie.mkv");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
