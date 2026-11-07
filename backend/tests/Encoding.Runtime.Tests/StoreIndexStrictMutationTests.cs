using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Encoding.Api.Flows;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Plugins;
using Encoding.Api.Resources;
using Encoding.Contracts.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>
/// ADR 0046 (correction du verdict « À CORRIGER » de la PR #37) : un index installed.json illisible est un état INCONNU. Import et retrait,
/// pour le Store des plugins comme pour celui des outils, sont refusés sans modifier l'index, les archives, le catalogue ni la révision,
/// et sans rien diffuser aux workers ; un index ABSENT reste un Store vide où l'on peut installer.
/// </summary>
public sealed class StoreIndexStrictMutationTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class Rig : IAsyncDisposable
    {
        public required TestDatabase Database; public required string Directory; public required IConfiguration Configuration;
        public required FlowComponentRegistry Registry; public required PluginStoreService Plugins; public required ToolStoreService Tools;
        public required CentralStoreStateRepository Repository; public required StoreStateBroadcaster Broadcaster; public required CentralStoreStateService Central;
        public int PluginWrites; public int ToolWrites;

        public string PluginIndex => Path.Combine(Plugins.Directory, "installed.json");
        public string ToolIndex => Path.Combine(Tools.Directory, "installed.json");

        /// <summary>JSON tronqué, comme après une écriture interrompue ou une corruption disque. Renvoie le contenu exact laissé sur disque.</summary>
        public byte[] CorruptPluginIndex() => Corrupt(PluginIndex, "[{ \"id\": \"acme.tools\", ");
        public byte[] CorruptToolIndex() => Corrupt(ToolIndex, "not json at all");

        private static byte[] Corrupt(string path, string content)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return File.ReadAllBytes(path);
        }

        /// <summary>Instantané de tous les fichiers sous un dossier (« chemin relatif=empreinte », trié) : rien ne doit apparaître, disparaître ni changer.</summary>
        public static IReadOnlyList<string> Snapshot(string root) => !System.IO.Directory.Exists(root)
            ? []
            : System.IO.Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path) + "=" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))))
                .Order(StringComparer.Ordinal).ToList();

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }

    private static async Task<Rig> CreateAsync()
    {
        var database = await TestDatabase.CreateSqliteAsync("store-index-strict");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        var directory = Path.Combine(Path.GetTempPath(), "encoding-store-strict-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PluginStore:Directory"] = Path.Combine(directory, "plugins"),
            ["ToolStore:Directory"] = Path.Combine(directory, "tools"),
            ["Resources:Directory"] = Path.Combine(directory, "resources")
        }).Build();
        var registry = StoreCatalogFixture.Create();
        var tools = new ToolStoreService(configuration, NullLogger<ToolStoreService>.Instance);
        var plugins = new PluginStoreService(configuration, registry, tools, NullLogger<PluginStoreService>.Instance);
        var repository = new CentralStoreStateRepository(database.Db);
        var broadcaster = new StoreStateBroadcaster();
        var central = new CentralStoreStateService(repository, plugins, tools, broadcaster, NullLogger<CentralStoreStateService>.Instance);
        var rig = new Rig
        {
            Database = database, Directory = directory, Configuration = configuration, Registry = registry, Plugins = plugins, Tools = tools,
            Repository = repository, Broadcaster = broadcaster, Central = central
        };
        plugins.Changed += () => Interlocked.Increment(ref rig.PluginWrites);
        tools.Changed += () => Interlocked.Increment(ref rig.ToolWrites);
        return rig;
    }

    /// <summary>Archive de plugin valide (même gabarit que les tests du Store : manifeste d'exemple, runtime python3).</summary>
    private static byte[] PluginArchive(string id, string version, Action<JsonObject>? tweak = null)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Harness.RepositoryRoot(), "backend", "tests", "Fixtures", "StoreCatalog", "example.json")))!.AsObject();
        manifest["id"] = id;
        manifest["version"] = version;
        foreach (var component in manifest["components"]!.AsArray())
            component!["id"] = id.Replace('.', '-') + "-" + component["id"]!.GetValue<string>()["example-".Length..];
        manifest["runtime"] = new JsonObject { ["type"] = "python3", ["entrypoint"] = "main.py" };
        tweak?.Invoke(manifest);
        return Zip(new() { ["plugin.json"] = System.Text.Encoding.UTF8.GetBytes(manifest.ToJsonString()), ["main.py"] = System.Text.Encoding.UTF8.GetBytes("print('hello')\n") });
    }

    /// <summary>Archive de paquet d'outil valide pour la plateforme courante : un exécutable dont le SHA-256 est déclaré dans tool.json.</summary>
    private static byte[] ToolArchive(string id, string version, string toolId)
    {
        var binary = System.Text.Encoding.UTF8.GetBytes($"#!/bin/sh\necho {id} {version}\n");
        var manifest = new ToolPackageManifest(1, id, version, id, ToolPackagePlatform.Current,
            [new ToolPackageExecutable(toolId, "bin/" + toolId, Convert.ToHexStringLower(SHA256.HashData(binary)))]);
        return Zip(new() { ["tool.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, Web), ["bin/" + toolId] = binary });
    }

    private static byte[] Zip(Dictionary<string, byte[]> entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var (name, content) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(content);
            }
        return memory.ToArray();
    }

    /// <summary>Révision et diffusion figées, aucune écriture d'index signalée.</summary>
    private static async Task AssertNothingPublishedAsync(Rig rig, long revision, long broadcasts, int pluginWrites, int toolWrites)
    {
        await Task.Delay(100); // laisse le temps à un éventuel signal asynchrone (OnInstalledSetChanged) de s'exécuter
        Assert.Equal(revision, (await rig.Repository.GetAsync(CancellationToken.None)).Revision);
        Assert.Equal(broadcasts, rig.Broadcaster.CurrentVersion);
        Assert.Equal(pluginWrites, Volatile.Read(ref rig.PluginWrites));
        Assert.Equal(toolWrites, Volatile.Read(ref rig.ToolWrites));
    }

    [Fact]
    public async Task PluginImportWithAnUnreadableIndexIsRefusedAndChangesNothing()
    {
        await using var rig = await CreateAsync();
        await rig.Plugins.ImportAsync(PluginArchive("acme.tools", "1.0.0"), "sideload");
        Assert.Equal(1, (await rig.Central.CurrentAsync(CancellationToken.None)).Revision);

        var corrupted = rig.CorruptPluginIndex();
        var files = Rig.Snapshot(rig.Plugins.Directory);
        var (revision, broadcasts, pluginWrites, toolWrites) = (1L, rig.Broadcaster.CurrentVersion, Volatile.Read(ref rig.PluginWrites), Volatile.Read(ref rig.ToolWrites));

        // Avant la correction : l'index illisible était lu comme vide et réécrit en [acme.other], effaçant acme.tools de l'état diffusé.
        await Assert.ThrowsAsync<StoreIndexUnreadableException>(() => rig.Plugins.ImportAsync(PluginArchive("acme.other", "1.0.0"), "sideload"));

        Assert.Equal(corrupted, File.ReadAllBytes(rig.PluginIndex)); // l'index illisible est laissé tel quel (diagnostic, restauration)
        Assert.Equal(files, Rig.Snapshot(rig.Plugins.Directory));    // aucune archive écrite ni supprimée
        Assert.DoesNotContain(rig.Registry.Plugins, plugin => plugin.Id == "acme.other"); // catalogue intact
        Assert.Contains(rig.Registry.Plugins, plugin => plugin.Id == "acme.tools");
        await AssertNothingPublishedAsync(rig, revision, broadcasts, pluginWrites, toolWrites);
        await Assert.ThrowsAsync<StoreIndexUnreadableException>(() => rig.Central.CurrentAsync(CancellationToken.None)); // toujours inconnu, pas tronqué
    }

    [Fact]
    public async Task PluginRemovalWithAnUnreadableIndexIsRefusedAndKeepsTheData()
    {
        await using var rig = await CreateAsync();
        await rig.Plugins.ImportAsync(PluginArchive("acme.tools", "1.0.0"), "sideload");
        Assert.Equal(1, (await rig.Central.CurrentAsync(CancellationToken.None)).Revision);

        var corrupted = rig.CorruptPluginIndex();
        var files = Rig.Snapshot(rig.Plugins.Directory);
        var (broadcasts, pluginWrites, toolWrites) = (rig.Broadcaster.CurrentVersion, Volatile.Read(ref rig.PluginWrites), Volatile.Read(ref rig.ToolWrites));

        // Ni « absent » (false → 404) ni retrait : l'état est inconnu.
        await Assert.ThrowsAsync<StoreIndexUnreadableException>(() => rig.Plugins.RemoveAsync("acme.tools"));
        await Assert.ThrowsAsync<StoreIndexUnreadableException>(() => rig.Plugins.RemoveAsync("acme.never-installed"));

        Assert.Equal(corrupted, File.ReadAllBytes(rig.PluginIndex));
        Assert.Equal(files, Rig.Snapshot(rig.Plugins.Directory));
        Assert.True(File.Exists(rig.Plugins.PackagePath("acme.tools", "1.0.0"))); // l'archive installée est conservée
        Assert.Contains(rig.Registry.Plugins, plugin => plugin.Id == "acme.tools");
        await AssertNothingPublishedAsync(rig, 1, broadcasts, pluginWrites, toolWrites);
    }

    [Fact]
    public async Task ToolImportWithAnUnreadableIndexIsRefusedAndChangesNothing()
    {
        await using var rig = await CreateAsync();
        await rig.Tools.ImportAsync(ToolArchive("acme-ffmpeg", "1.0.0", "ffmpeg"), "sideload");
        Assert.Equal(1, (await rig.Central.CurrentAsync(CancellationToken.None)).Revision);

        var corrupted = rig.CorruptToolIndex();
        var files = Rig.Snapshot(rig.Tools.Directory);
        var (broadcasts, pluginWrites, toolWrites) = (rig.Broadcaster.CurrentVersion, Volatile.Read(ref rig.PluginWrites), Volatile.Read(ref rig.ToolWrites));

        await Assert.ThrowsAsync<StoreIndexUnreadableException>(() => rig.Tools.ImportAsync(ToolArchive("acme-mkvtoolnix", "1.0.0", "mkvmerge"), "sideload"));

        Assert.Equal(corrupted, File.ReadAllBytes(rig.ToolIndex));
        Assert.Equal(files, Rig.Snapshot(rig.Tools.Directory));
        Assert.False(File.Exists(rig.Tools.PackagePath("acme-mkvtoolnix", "1.0.0", ToolPackagePlatform.Current)));
        Assert.Throws<StoreIndexUnreadableException>(() => rig.Tools.AvailableStrict());
        await AssertNothingPublishedAsync(rig, 1, broadcasts, pluginWrites, toolWrites);
    }

    [Fact]
    public async Task ToolRemovalWithAnUnreadableIndexIsRefusedAndKeepsTheData()
    {
        await using var rig = await CreateAsync();
        await rig.Tools.ImportAsync(ToolArchive("acme-ffmpeg", "1.0.0", "ffmpeg"), "sideload");
        Assert.Equal(1, (await rig.Central.CurrentAsync(CancellationToken.None)).Revision);

        var corrupted = rig.CorruptToolIndex();
        var files = Rig.Snapshot(rig.Tools.Directory);
        var (broadcasts, pluginWrites, toolWrites) = (rig.Broadcaster.CurrentVersion, Volatile.Read(ref rig.PluginWrites), Volatile.Read(ref rig.ToolWrites));

        await Assert.ThrowsAsync<StoreIndexUnreadableException>(() => rig.Tools.RemoveAsync("acme-ffmpeg", ToolPackagePlatform.Current, CancellationToken.None));
        await Assert.ThrowsAsync<StoreIndexUnreadableException>(() => rig.Tools.RemoveAsync("acme-unknown", ToolPackagePlatform.Current, CancellationToken.None));

        Assert.Equal(corrupted, File.ReadAllBytes(rig.ToolIndex));
        Assert.Equal(files, Rig.Snapshot(rig.Tools.Directory));
        Assert.True(File.Exists(rig.Tools.PackagePath("acme-ffmpeg", "1.0.0", ToolPackagePlatform.Current)));
        await AssertNothingPublishedAsync(rig, 1, broadcasts, pluginWrites, toolWrites);
    }

    [Fact]
    public async Task PluginImportNeedingAToolIsRefusedExplicitlyWhenTheToolIndexIsUnreadable()
    {
        await using var rig = await CreateAsync();
        await rig.Plugins.ImportAsync(PluginArchive("acme.tools", "1.0.0"), "sideload");
        Assert.Equal(1, (await rig.Central.CurrentAsync(CancellationToken.None)).Revision);
        var pluginIndex = File.ReadAllBytes(rig.PluginIndex);
        rig.CorruptToolIndex();
        var (broadcasts, pluginWrites, toolWrites) = (rig.Broadcaster.CurrentVersion, Volatile.Read(ref rig.PluginWrites), Volatile.Read(ref rig.ToolWrites));

        var archive = PluginArchive("acme.encoder", "1.0.0", manifest => manifest["requiredToolPackages"] = new JsonArray(
            new JsonObject { ["toolPackageId"] = "acme-ffmpeg", ["minimumVersion"] = "1.0.0" }));
        // État inconnu, pas « dépendance absente » : l'opérateur doit savoir que c'est l'index qu'il faut réparer.
        await Assert.ThrowsAsync<StoreIndexUnreadableException>(() => rig.Plugins.ImportAsync(archive, "sideload"));

        Assert.Equal(pluginIndex, File.ReadAllBytes(rig.PluginIndex));
        Assert.DoesNotContain(rig.Registry.Plugins, plugin => plugin.Id == "acme.encoder");
        await AssertNothingPublishedAsync(rig, 1, broadcasts, pluginWrites, toolWrites);
    }

    [Fact]
    public async Task ToolDependencyCheckIsRefusedWhenThePluginIndexIsUnreadable()
    {
        await using var rig = await CreateAsync();
        await rig.Tools.ImportAsync(ToolArchive("acme-ffmpeg", "1.0.0", "ffmpeg"), "sideload");
        rig.CorruptPluginIndex();
        // Un index de plugins illisible ne prouve pas que l'outil est libre : la route de retrait refuse (503) au lieu de le supprimer.
        Assert.Throws<StoreIndexUnreadableException>(() => rig.Plugins.RequiresToolPackage("acme-ffmpeg"));
    }

    [Fact]
    public async Task MissingIndexesAreAnEmptyStoreWhereImportAndRemovalWork()
    {
        await using var rig = await CreateAsync();
        Assert.False(File.Exists(rig.PluginIndex)); Assert.False(File.Exists(rig.ToolIndex));

        // Absent : « rien d'installé », sans erreur ; un retrait répond « absent » (false → 404), pas « état inconnu ».
        Assert.False(await rig.Plugins.RemoveAsync("acme.tools"));
        Assert.False(await rig.Tools.RemoveAsync("acme-ffmpeg", ToolPackagePlatform.Current, CancellationToken.None));
        Assert.False(rig.Plugins.RequiresToolPackage("acme-ffmpeg"));
        Assert.Empty(rig.Tools.AvailableStrict());
        Assert.Equal(0, (await rig.Central.CurrentAsync(CancellationToken.None)).Revision);

        // Et l'on peut installer : le premier import crée l'index.
        await rig.Tools.ImportAsync(ToolArchive("acme-ffmpeg", "1.0.0", "ffmpeg"), "sideload");
        await rig.Plugins.ImportAsync(PluginArchive("acme.tools", "1.0.0"), "sideload");
        var state = await rig.Central.CurrentAsync(CancellationToken.None);
        Assert.Equal(["plugin acme.tools", "tool acme-ffmpeg"], state.Items.Select(item => $"{item.Kind} {item.Id}"));
        Assert.InRange(state.Revision, 1, 2);
    }

    [Fact]
    public async Task StoreRoutesAnswer503WithAnUnreadableIndexAndNeverClaimAnEmptyStore()
    {
        await using var rig = await CreateAsync();
        await rig.Tools.ImportAsync(ToolArchive("acme-ffmpeg", "1.0.0", "ffmpeg"), "sideload");
        await rig.Plugins.ImportAsync(PluginArchive("acme.tools", "1.0.0"), "sideload");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var blobs = new ResourceBlobStore(rig.Configuration);
        builder.Services.AddSingleton(rig.Plugins);
        builder.Services.AddSingleton(rig.Tools);
        builder.Services.AddSingleton(rig.Registry);
        builder.Services.AddSingleton<IHttpClientFactory>(new HttpClientFactoryStub());
        builder.Services.AddSingleton(new PluginRepositoryService(rig.Configuration, new HttpClientFactoryStub(), rig.Plugins, rig.Tools, NullLogger<PluginRepositoryService>.Instance));
        builder.Services.AddSingleton(blobs);
        builder.Services.AddSingleton(new ResourceRepository(rig.Database.Db, blobs));
        builder.Services.AddSingleton(new FlowRepository(rig.Database.Db));
        builder.Services.AddSingleton(new FlowDefinitionValidator(rig.Registry));
        await using var app = builder.Build();
        app.MapPluginStoreEndpoints();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(30) };

        // Index lisibles : les routes de distribution répondent normalement.
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/store/installed")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.DeleteAsync("/api/v1/store/packages/acme.never-installed")).StatusCode);

        var pluginIndex = rig.CorruptPluginIndex();
        var toolIndex = rig.CorruptToolIndex();
        var files = Rig.Snapshot(rig.Directory);

        async Task Unavailable(HttpResponseMessage response, string label)
        {
            using (response)
            {
                Assert.True(response.StatusCode == HttpStatusCode.ServiceUnavailable, $"{label} : {(int)response.StatusCode}");
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("store_index_unreadable", body.GetProperty("code").GetString());
                Assert.DoesNotContain(rig.Directory, body.GetRawText()); // aucun chemin dans la réponse
            }
        }

        await Unavailable(await http.PostAsync("/api/v1/store/packages/import", new ByteArrayContent(PluginArchive("acme.other", "1.0.0"))), "import plugin");
        await Unavailable(await http.PostAsync("/api/v1/store/tools/import", new ByteArrayContent(ToolArchive("acme-mkvtoolnix", "1.0.0", "mkvmerge"))), "import outil");
        await Unavailable(await http.DeleteAsync("/api/v1/store/packages/acme.tools"), "retrait plugin");
        await Unavailable(await http.DeleteAsync("/api/v1/store/packages/acme.never-installed"), "retrait plugin inconnu");
        await Unavailable(await http.DeleteAsync($"/api/v1/store/tools/acme-ffmpeg/{ToolPackagePlatform.Current}"), "retrait outil");
        await Unavailable(await http.GetAsync("/api/v1/store/installed"), "liste des plugins installés");
        await Unavailable(await http.GetAsync("/api/v1/store/tools/installed"), "liste des outils installés");
        await Unavailable(await http.GetAsync("/api/v1/store/packages"), "catalogue");
        await Unavailable(await http.GetAsync("/api/v1/store/packages/acme.tools/1.0.0/download"), "téléchargement plugin");

        Assert.Equal(pluginIndex, File.ReadAllBytes(rig.PluginIndex));
        Assert.Equal(toolIndex, File.ReadAllBytes(rig.ToolIndex));
        Assert.Equal(files, Rig.Snapshot(rig.Directory));
        await app.StopAsync();
    }

    private sealed class HttpClientFactoryStub : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }
}
