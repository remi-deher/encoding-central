using System.Data.Common;
using System.Net;
using System.Text;
using Encoding.Api.Cluster;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Nodes;
using Encoding.Contracts.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class LeadershipHandoffTests : IDisposable
{
    private readonly string testDir;

    public LeadershipHandoffTests()
    {
        testDir = Path.Combine(Path.GetTempPath(), "encoding_leadership_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(testDir))
        {
            try { Directory.Delete(testDir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task CreateSnapshotAsync_CreatesConsistentSqliteSnapshot()
    {
        await using var testDb = await TestDatabase.CreateSqliteAsync("leadership_src");
        await DatabaseMigrator.ApplyAsync(testDb.Db, Harness.RepositoryRoot(), CancellationToken.None);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Node:DataDirectory"] = testDir,
                ["ConnectionStrings:Cluster"] = testDb.ConnectionString
            }).Build();

        var repo = new LeadershipRepository(testDb.Db);
        var nodeRepo = new NodeRepository(testDb.Db);
        var registry = new LocalNodeRegistry(nodeRepo);
        var service = new LeadershipHandoffService(testDb.Db, repo, nodeRepo, registry, config, NullLogger<LeadershipHandoffService>.Instance);

        var destination = Path.Combine(testDir, "snapshot.db");
        var (digest, sizeBytes) = await service.CreateSnapshotAsync(destination, CancellationToken.None);

        Assert.True(File.Exists(destination));
        Assert.True(sizeBytes > 0);
        Assert.False(string.IsNullOrWhiteSpace(digest));

        // Vérifier que la base du snapshot est lisible et contient les tables
        using var snapshotConnection = new SqliteConnection($"Data Source={destination}");
        await snapshotConnection.OpenAsync();
        using var cmd = snapshotConnection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table'";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.True(count > 0);
    }

    [Fact]
    public async Task LeadershipRepository_TracksBackupsAndPruning()
    {
        await using var testDb = await TestDatabase.CreateSqliteAsync("leadership_repo");
        await DatabaseMigrator.ApplyAsync(testDb.Db, Harness.RepositoryRoot(), CancellationToken.None);

        var repo = new LeadershipRepository(testDb.Db);

        for (var i = 1; i <= 6; i++)
        {
            var fakeFile = Path.Combine(testDir, $"backup_{i}.db");
            await File.WriteAllTextAsync(fakeFile, "fake backup content " + i);
            await repo.SaveBackupAsync($"digest_{i}", 100 * i, fakeFile, CancellationToken.None);
        }

        var latest = await repo.GetLatestBackupAsync(CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal("digest_6", latest.Digest);

        var pruned = await repo.PruneOldBackupsAsync(keepCount: 5, CancellationToken.None);
        Assert.Single(pruned);
        Assert.Contains("backup_1.db", pruned[0]);
    }

    [Fact]
    public async Task MasterRedirectMiddleware_RedirectsWhenMasterUrlIsConfigured()
    {
        await using var testDb = await TestDatabase.CreateSqliteAsync("leadership_redir");
        await DatabaseMigrator.ApplyAsync(testDb.Db, Harness.RepositoryRoot(), CancellationToken.None);

        var repo = new LeadershipRepository(testDb.Db);
        await repo.SetMasterUrlAsync("http://192.168.1.51:8090/", CancellationToken.None);

        var middleware = new MasterRedirectMiddleware(next: (innerContext) => Task.CompletedTask);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/executions";
        context.Request.QueryString = new QueryString("?state=active");

        await middleware.InvokeAsync(context, repo);

        Assert.Equal(StatusCodes.Status308PermanentRedirect, context.Response.StatusCode);
        Assert.Equal("http://192.168.1.51:8090/api/v1/executions?state=active", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task MasterRedirectMiddleware_DoesNotRedirectHealthEndpoints()
    {
        await using var testDb = await TestDatabase.CreateSqliteAsync("leadership_health");
        await DatabaseMigrator.ApplyAsync(testDb.Db, Harness.RepositoryRoot(), CancellationToken.None);

        var repo = new LeadershipRepository(testDb.Db);
        await repo.SetMasterUrlAsync("http://192.168.1.51:8090/", CancellationToken.None);

        var executedNext = false;
        var middleware = new MasterRedirectMiddleware(next: (innerContext) =>
        {
            executedNext = true;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Path = "/health/ready";

        await middleware.InvokeAsync(context, repo);

        Assert.True(executedNext);
        Assert.NotEqual(StatusCodes.Status308PermanentRedirect, context.Response.StatusCode);
    }

    [Fact]
    public void GetKeyBundle_ReturnsKeyFilesFromPluginKeyDirectory()
    {
        var keyDir = Path.Combine(testDir, "plugin-keys");
        Directory.CreateDirectory(keyDir);
        var xmlContent = "<key id=\"test\"><descriptor/></key>";
        File.WriteAllText(Path.Combine(keyDir, "key-test-123.xml"), xmlContent);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Node:DataDirectory"] = testDir,
            }).Build();

        var repo = new LeadershipRepository(null!);
        var service = new LeadershipHandoffService(null!, repo, null!, null!, config, NullLogger<LeadershipHandoffService>.Instance);

        var bundle = service.GetKeyBundle();
        Assert.Single(bundle.Keys);
        Assert.Equal("key-test-123.xml", bundle.Keys[0].FileName);
        Assert.Equal(xmlContent, bundle.Keys[0].Content);
    }
}
