using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>ADR 0046 (T1) : l'état Store désiré est global, c'est l'ensemble des paquets installés sur le central ; sa révision augmente à chaque changement.</summary>
public sealed class CentralStoreStateTests
{
    private static readonly string ShaA = new('a', 64);
    private static readonly string ShaB = new('b', 64);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    internal sealed class Rig : IAsyncDisposable
    {
        public required TestDatabase Database; public required string Directory;
        public required PluginStoreService Plugins; public required ToolStoreService Tools; public required CentralStoreStateService Central;
        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }

        /// <summary>Écrit les index d'installation comme le feraient les installations réelles (installed.json de chaque Store).</summary>
        public void WriteIndexes(IEnumerable<InstalledPlugin> plugins, IEnumerable<InstalledToolPackage> tools)
        {
            System.IO.Directory.CreateDirectory(Plugins.Directory); System.IO.Directory.CreateDirectory(Tools.Directory);
            File.WriteAllText(Path.Combine(Plugins.Directory, "installed.json"), JsonSerializer.Serialize(plugins.ToList(), Web));
            File.WriteAllText(Path.Combine(Tools.Directory, "installed.json"), JsonSerializer.Serialize(tools.ToList(), Web));
        }
    }

    internal static async Task<Rig> CreateAsync()
    {
        var database = await TestDatabase.CreateSqliteAsync("central-store-state");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        var directory = Path.Combine(Path.GetTempPath(), "encoding-central-store-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["PluginStore:Directory"] = directory, ["ToolStore:Directory"] = Path.Combine(directory, "tools") }).Build();
        var tools = new ToolStoreService(configuration, NullLogger<ToolStoreService>.Instance);
        var plugins = new PluginStoreService(configuration, StoreCatalogFixture.Create(), tools, NullLogger<PluginStoreService>.Instance);
        var central = new CentralStoreStateService(new CentralStoreStateRepository(database.Db), plugins, tools, new StoreStateBroadcaster(), NullLogger<CentralStoreStateService>.Instance);
        return new Rig { Database = database, Directory = directory, Plugins = plugins, Tools = tools, Central = central };
    }

    internal static InstalledPlugin Plugin(string id, string version, string sha) => new(id, version, id, sha, "repo:official", DateTimeOffset.UtcNow);
    internal static InstalledToolPackage Tool(string id, string version, string platform, string sha) =>
        new(id, version, platform, sha, [], [], "repo:official", DateTimeOffset.UtcNow);

    [Fact]
    public async Task FreshCentralHasRevisionZeroAndNoPackage()
    {
        await using var rig = await CreateAsync();
        var state = await rig.Central.CurrentAsync(CancellationToken.None);
        Assert.Equal(0, state.Revision);
        Assert.Empty(state.Items);
    }

    [Fact]
    public async Task EveryInstallUpdateAndRemovalAddsOneToTheGlobalRevision()
    {
        await using var rig = await CreateAsync();
        rig.WriteIndexes([Plugin("base", "1.0.0", ShaA)], []);
        Assert.Equal(1, (await rig.Central.CurrentAsync(CancellationToken.None)).Revision); // installation d'un plugin

        rig.WriteIndexes([Plugin("base", "1.0.0", ShaA)], [Tool("ffmpeg", "7.1.0", "linux-x64", ShaB)]);
        Assert.Equal(2, (await rig.Central.CurrentAsync(CancellationToken.None)).Revision); // installation d'un outil

        rig.WriteIndexes([Plugin("base", "1.1.0", ShaB)], [Tool("ffmpeg", "7.1.0", "linux-x64", ShaB)]);
        Assert.Equal(3, (await rig.Central.CurrentAsync(CancellationToken.None)).Revision); // mise à jour d'un plugin

        rig.WriteIndexes([Plugin("base", "1.1.0", ShaB)], []);
        Assert.Equal(4, (await rig.Central.CurrentAsync(CancellationToken.None)).Revision); // retrait d'un outil

        // Relire sans changement, ou réécrire le même ensemble, ne bouge pas la révision.
        Assert.Equal(4, (await rig.Central.CurrentAsync(CancellationToken.None)).Revision);
        rig.WriteIndexes([Plugin("base", "1.1.0", ShaB)], []);
        Assert.Equal(4, (await rig.Central.CurrentAsync(CancellationToken.None)).Revision);
    }

    [Fact]
    public async Task StateListsAllInstalledPackagesWithTheirSha256AndPlatformSorted()
    {
        await using var rig = await CreateAsync();
        rig.WriteIndexes([Plugin("nodes", "1.8.2", ShaB.ToUpperInvariant()), Plugin("base", "1.10.3", ShaA)],
            [Tool("ffmpeg", "6.1.1", "linux-x64", ShaA), Tool("ffmpeg", "6.1.1", "linux-arm64", ShaB)]);
        var items = (await rig.Central.CurrentAsync(CancellationToken.None)).Items;
        Assert.Equal(["plugin base", "plugin nodes", "tool ffmpeg linux-arm64", "tool ffmpeg linux-x64"],
            items.Select(item => $"{item.Kind} {item.Id}{(item.Platform is null ? "" : " " + item.Platform)}"));
        Assert.Equal(ShaB, items.Single(item => item.Id == "nodes").Sha256); // empreinte normalisée en minuscules
        Assert.All(items, item => Assert.Matches("^[0-9a-f]{64}$", item.Sha256));
    }

    [Fact]
    public async Task RemovingAPackageThroughTheStoreServiceRaisesTheRevisionOnTheNextRead()
    {
        await using var rig = await CreateAsync();
        rig.WriteIndexes([Plugin("base", "1.0.0", ShaA), Plugin("nodes", "1.0.0", ShaB)], []);
        Assert.Equal(1, (await rig.Central.CurrentAsync(CancellationToken.None)).Revision);
        Assert.True(await rig.Plugins.RemoveAsync("nodes")); // chemin réel : Save() signale le changement
        var state = await rig.Central.CurrentAsync(CancellationToken.None);
        Assert.Equal(2, state.Revision); // exactement +1 même si le signal asynchrone a déjà synchronisé
        Assert.Equal("base", Assert.Single(state.Items).Id);
    }

    [Fact]
    public async Task RevisionIsRestoredFromTheDatabaseAndNeverGoesBack()
    {
        await using var rig = await CreateAsync();
        rig.WriteIndexes([Plugin("base", "1.0.0", ShaA)], []);
        await rig.Central.CurrentAsync(CancellationToken.None);
        rig.WriteIndexes([], []);
        Assert.Equal(2, (await rig.Central.CurrentAsync(CancellationToken.None)).Revision); // retirer tout est un changement, pas un retour à 0
        // Un second service sur la même base (redémarrage du central) retrouve la même révision sans la modifier.
        var restarted = new CentralStoreStateService(new CentralStoreStateRepository(rig.Database.Db), rig.Plugins, rig.Tools, new StoreStateBroadcaster(), NullLogger<CentralStoreStateService>.Instance);
        Assert.Equal(2, (await restarted.CurrentAsync(CancellationToken.None)).Revision);
    }

    [Fact]
    public void DigestIsEmptyWithoutPackageAndStableForTheSameSet()
    {
        Assert.Equal("", CentralStoreStateService.Digest([]));
        var one = new StoreDesiredItem("plugin", "base", "1.0.0", null, ShaA);
        var two = new StoreDesiredItem("tool", "ffmpeg", "7.1.0", "linux-x64", ShaB);
        Assert.Equal(CentralStoreStateService.Digest([one, two]), CentralStoreStateService.Digest([one, two]));
        Assert.NotEqual(CentralStoreStateService.Digest([one, two]), CentralStoreStateService.Digest([one, two with { Version = "7.1.1" }]));
        Assert.NotEqual(CentralStoreStateService.Digest([one]), CentralStoreStateService.Digest([one, two]));
    }
}
