using Encoding.Api.Flows;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Issue #17, P1 : résolution de l'empreinte officielle depuis le catalogue en cache du central, avec un faux Store HTTP local.</summary>
public sealed class WorkerStoreDesiredStateTests
{
    private static readonly string PluginSha = new string('b', 64);
    private static readonly string ToolSha = new string('c', 64).ToUpperInvariant(); // l'index peut être en majuscules ; le central normalise

    private sealed class Factory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    private sealed class Rig : IAsyncDisposable
    {
        public required TestDatabase Database; public required WebApplication Store; public required string Directory;
        public required WorkerStoreStateService Service; public required PluginRepositoryService Repositories; public required Guid Worker; public required Guid Other;
        public bool StoreUp = true;
        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync(); await Database.DisposeAsync();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }

    private static async Task<Rig> CreateAsync()
    {
        var database = await TestDatabase.CreateSqliteAsync("store-desired");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in ids)
        {
            await using var insert = database.Db.CreateCommand("INSERT INTO workers(id,name,roles,max_concurrent_tasks,capabilities,last_seen_at) VALUES(@id,'w','worker',1,'[]',(strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z'))");
            insert.Parameters.AddWithValue("id", id);
            await insert.ExecuteNonQueryAsync();
        }
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var store = builder.Build();
        Rig? rig = null;
        store.MapGet("/repository.json", () => rig is { StoreUp: false } ? Results.StatusCode(503) : Results.Json(new
        {
            schemaVersion = "1.0.0", name = "Faux Store", description = "d",
            categories = new[] { new { id = "utilities", name = "Utilitaires" } },
            plugins = new[]
            {
                new { id = "official.sample", name = "Sample", summary = "s", category = "utilities", author = "A", latestVersion = "1.2.0", requiredTools = Array.Empty<string>(),
                    versions = new[] { new { version = "1.2.0", downloadUrl = "http://127.0.0.1/p.zip", sha256 = PluginSha, releaseDate = (string?)null, changelog = (string?)null } } }
            },
            tools = new[]
            {
                new { id = "ffmpeg", name = "FFmpeg", summary = "s", latestVersion = "7.1.0",
                    versions = new[] { new { version = "7.1.0", platform = "linux-x64", downloadUrl = "http://127.0.0.1/t.zip", sha256 = ToolSha, releaseDate = (string?)null, changelog = (string?)null } } }
            }
        }));
        await store.StartAsync();
        var directory = Path.Combine(Path.GetTempPath(), "encoding-store-desired-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PluginStore:Directory"] = directory,
            ["ToolStore:Directory"] = Path.Combine(directory, "tools"),
            ["PluginStore:OfficialRepositoryUrl"] = store.Urls.Single() + "/repository.json"
        }).Build();
        var tools = new ToolStoreService(configuration, NullLogger<ToolStoreService>.Instance);
        var plugins = new PluginStoreService(configuration, StoreCatalogFixture.Create(), tools, NullLogger<PluginStoreService>.Instance);
        var repositories = new PluginRepositoryService(configuration, new Factory(), plugins, tools, NullLogger<PluginRepositoryService>.Instance);
        var service = new WorkerStoreStateService(new WorkerStoreStateRepository(database.Db), repositories);
        rig = new Rig { Database = database, Store = store, Directory = directory, Service = service, Repositories = repositories, Worker = ids[0], Other = ids[1] };
        return rig;
    }

    private static StoreDesiredRequestItem I(string kind, string id, string version, string? platform) => new(kind, id, version, platform);
    private static SetStoreStateRequest Request(params StoreDesiredRequestItem[] items) => new(items);

    [Fact]
    public async Task ResolvesOfficialShaFromCatalogAndKeepsOtherWorkersUntouched()
    {
        await using var rig = await CreateAsync();
        var result = await rig.Service.SetDesiredAsync(rig.Worker, Request(I("plugin", "official.sample", "1.2.0", null), new("tool", "ffmpeg", "7.1.0", "linux-x64")), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(1, result.Revision);
        Assert.Equal(PluginSha, result.Items.Single(item => item.Kind == "plugin").Sha256);
        Assert.Equal(ToolSha.ToLowerInvariant(), result.Items.Single(item => item.Kind == "tool").Sha256);
        Assert.Equal(2, (await rig.Service.SetDesiredAsync(rig.Worker, Request(), CancellationToken.None))!.Revision); // vider l'état est permis
        Assert.Equal(0, (await rig.Service.GetAsync(rig.Other, CancellationToken.None))!.Desired.Revision);
    }

    [Theory]
    [InlineData("plugin", "official.sample", "latest", null)]   // version flottante
    [InlineData("plugin", "official.sample", "9.9.9", null)]    // version absente du catalogue
    [InlineData("plugin", "official.unknown", "1.2.0", null)]   // paquet absent
    [InlineData("tool", "ffmpeg", "7.1.0", null)]               // plateforme manquante
    [InlineData("tool", "ffmpeg", "7.1.0", "windows-x64")]      // plateforme absente du catalogue
    [InlineData("plugin", "official.sample", "1.2.0", "linux-x64")] // un plugin n'a pas de plateforme
    [InlineData("other", "official.sample", "1.2.0", null)]
    public async Task RejectsItemsNotInTheEnabledCatalogWithoutChangingState(string kind, string id, string version, string? platform)
    {
        await using var rig = await CreateAsync();
        await Assert.ThrowsAsync<StoreCatalogException>(() => rig.Service.SetDesiredAsync(rig.Worker, Request(I(kind, id, version, platform)), CancellationToken.None));
        Assert.Equal(0, (await rig.Service.GetAsync(rig.Worker, CancellationToken.None))!.Desired.Revision);
    }

    [Fact]
    public async Task UnknownWorkerAndMissingItemsAreRefused()
    {
        await using var rig = await CreateAsync();
        Assert.Null(await rig.Service.SetDesiredAsync(Guid.NewGuid(), Request(), CancellationToken.None));
        await Assert.ThrowsAsync<StoreCatalogException>(() => rig.Service.SetDesiredAsync(rig.Worker, new SetStoreStateRequest(null), CancellationToken.None));
    }

    [Fact]
    public async Task UnavailableStoreKeepsPersistedStateAndServesFromTheCachedCatalog()
    {
        await using var rig = await CreateAsync();
        // Sans cache et Store indisponible : refus clair, aucun état créé.
        rig.StoreUp = false;
        await Assert.ThrowsAsync<StoreCatalogException>(() => rig.Service.SetDesiredAsync(rig.Worker, Request(I("plugin", "official.sample", "1.2.0", null)), CancellationToken.None));
        Assert.Equal(0, (await rig.Service.GetAsync(rig.Worker, CancellationToken.None))!.Desired.Revision);

        rig.StoreUp = true;
        Assert.NotNull(await rig.Repositories.SyncAsync("official", CancellationToken.None));
        Assert.Equal(1, (await rig.Service.SetDesiredAsync(rig.Worker, Request(I("plugin", "official.sample", "1.2.0", null)), CancellationToken.None))!.Revision);

        // Store tombé après synchronisation : l'état persisté reste lisible et le cache permet encore de résoudre.
        rig.StoreUp = false;
        await rig.Repositories.SyncAsync("official", CancellationToken.None); // l'échec est consigné sans vider le catalogue
        var state = await rig.Service.GetAsync(rig.Worker, CancellationToken.None);
        Assert.Equal("official.sample", Assert.Single(state!.Desired.Items).Id);
        Assert.Equal(2, (await rig.Service.SetDesiredAsync(rig.Worker, Request(I("tool", "ffmpeg", "7.1.0", "linux-x64")), CancellationToken.None))!.Revision);
    }
}
