using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure.Data.Sqlite;
using Encoding.Api.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Architecture 0043 : le serveur démarre uniquement sur SQLite local.</summary>
public sealed class DatabaseProviderTests
{
    private static IConfiguration Config(string? provider, string? connection)
    {
        var values = new Dictionary<string, string?>();
        if (provider is not null) values["Database:Provider"] = provider;
        if (connection is not null) values["ConnectionStrings:Cluster"] = connection;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void RemovedConsensusModeFailsBeforeOpeningTheDatabase()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["PeerCluster:Enabled"] = "true" }).Build();
        Assert.Contains("retiré", Assert.Throws<InvalidOperationException>(() => DatabaseProvider.CreateRuntime(configuration)).Message);
    }

    [Fact]
    public void SqliteIsTheDefaultWhenNoConnectionStringIsConfigured()
    {
        using var source = DatabaseProvider.CreateRuntime(Config(null, null));
        Assert.IsType<SqliteDataSource>(source);
        Assert.Contains("cluster.db", source.ConnectionString, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("other", "Data Source=x.db")]
    [InlineData("sqlite", "Host=db;Database=encoding")]
    [InlineData(null, "Server=db;Database=encoding")]
    public void RuntimeRejectsNonSqliteConfiguration(string? provider, string? connection) =>
        Assert.Throws<InvalidOperationException>(() => DatabaseProvider.CreateRuntime(Config(provider, connection)));

    [Fact]
    public async Task FreshSqliteInstallationAppliesTheCurrentSchemaAndIsRestartSafe()
    {
        await using var database = await TestDatabase.CreateSqliteAsync("fresh-install");
        var contentRoot = Harness.RepositoryRoot();

        await DatabaseMigrator.ApplyAsync(database.Db, contentRoot, CancellationToken.None);
        var firstStatus = await DatabaseMigrator.StatusAsync(database.Db, contentRoot, CancellationToken.None);
        Assert.Empty(firstStatus.Pending);
        Assert.Empty(firstStatus.Unknown);
        Assert.Equal(MigrationCatalog.Load(MigrationCatalog.ResolveDirectory(contentRoot)).Last().Version, firstStatus.Latest);
        await using (var identity = database.Db.CreateCommand("SELECT count(*) FROM cluster_identity"))
            Assert.Equal(1L, await identity.ExecuteScalarAsync());

        await DatabaseMigrator.ApplyAsync(database.Db, contentRoot, CancellationToken.None);
        var restartStatus = await DatabaseMigrator.StatusAsync(database.Db, contentRoot, CancellationToken.None);
        Assert.Equal(firstStatus.Applied, restartStatus.Applied);
        Assert.Equal(firstStatus.Pending, restartStatus.Pending);
        Assert.Equal(firstStatus.Unknown, restartStatus.Unknown);
        Assert.Equal(firstStatus.Latest, restartStatus.Latest);
    }

    [Fact]
    public async Task SqliteCreatesItsDirectoryOnFirstUse()
    {
        var directory = Path.Combine(Path.GetTempPath(), "encoding-sqlite-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var source = new SqliteDataSource($"Data Source={Path.Combine(directory, "sub", "cluster.db")};Pooling=False");
            await using var command = source.CreateCommand("SELECT 1");
            Assert.Equal(1L, await command.ExecuteScalarAsync());
            Assert.True(File.Exists(Path.Combine(directory, "sub", "cluster.db")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
