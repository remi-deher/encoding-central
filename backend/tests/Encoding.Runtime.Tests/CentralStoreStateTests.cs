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
        public required CentralStoreStateRepository Repository; public required StoreStateBroadcaster Broadcaster;
        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }

        /// <summary>Rend un index illisible (JSON tronqué), comme après une écriture interrompue ou une corruption disque.</summary>
        public void CorruptPluginIndex() => File.WriteAllText(Path.Combine(Plugins.Directory, "installed.json"), "[{ \"id\": \"base\", ");
        public void CorruptToolIndex() => File.WriteAllText(Path.Combine(Tools.Directory, "installed.json"), "not json at all");

        // Comme les services d'installation (fichier temporaire puis déplacement) : un lecteur concurrent ne voit jamais un index à moitié écrit.
        private static void AtomicWrite(string path, string content)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }

        /// <summary>Écrit les index d'installation comme le feraient les installations réelles (installed.json de chaque Store).</summary>
        public void WriteIndexes(IEnumerable<InstalledPlugin> plugins, IEnumerable<InstalledToolPackage> tools)
        {
            System.IO.Directory.CreateDirectory(Plugins.Directory); System.IO.Directory.CreateDirectory(Tools.Directory);
            AtomicWrite(Path.Combine(Plugins.Directory, "installed.json"), JsonSerializer.Serialize(plugins.ToList(), Web));
            AtomicWrite(Path.Combine(Tools.Directory, "installed.json"), JsonSerializer.Serialize(tools.ToList(), Web));
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
        var repository = new CentralStoreStateRepository(database.Db);
        var broadcaster = new StoreStateBroadcaster();
        var central = new CentralStoreStateService(repository, plugins, tools, broadcaster, NullLogger<CentralStoreStateService>.Instance);
        return new Rig { Database = database, Directory = directory, Plugins = plugins, Tools = tools, Central = central, Repository = repository, Broadcaster = broadcaster };
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
    public async Task UnreadablePluginIndexIsAnUnknownStateNotAnEmptyOne()
    {
        await using var rig = await CreateAsync();
        rig.WriteIndexes([Plugin("base", "1.0.0", ShaA), Plugin("nodes", "1.0.0", ShaB)], []);
        Assert.Equal(1, (await rig.Central.CurrentAsync(CancellationToken.None)).Revision);
        var broadcasts = rig.Broadcaster.CurrentVersion;

        rig.CorruptPluginIndex();
        await Assert.ThrowsAsync<StoreIndexUnreadableException>(() => rig.Central.CurrentAsync(CancellationToken.None));
        Assert.Equal(1, (await rig.Repository.GetAsync(CancellationToken.None)).Revision); // la révision n'avance pas : ce n'est pas « aucun paquet »
        Assert.Equal(broadcasts, rig.Broadcaster.CurrentVersion); // rien n'est diffusé aux workers (ils supprimeraient leurs plugins)

        // Index de nouveau lisible avec le même ensemble : aucune révision n'a été consommée par l'incident.
        rig.WriteIndexes([Plugin("base", "1.0.0", ShaA), Plugin("nodes", "1.0.0", ShaB)], []);
        var state = await rig.Central.CurrentAsync(CancellationToken.None);
        Assert.Equal(1, state.Revision); Assert.Equal(2, state.Items.Count);
    }

    [Fact]
    public async Task UnreadableToolIndexIsAnUnknownStateToo()
    {
        await using var rig = await CreateAsync();
        rig.WriteIndexes([Plugin("base", "1.0.0", ShaA)], [Tool("ffmpeg", "7.1.0", "linux-x64", ShaB)]);
        Assert.Equal(1, (await rig.Central.CurrentAsync(CancellationToken.None)).Revision);
        rig.CorruptToolIndex();
        await Assert.ThrowsAsync<StoreIndexUnreadableException>(() => rig.Central.CurrentAsync(CancellationToken.None));
        Assert.Equal(1, (await rig.Repository.GetAsync(CancellationToken.None)).Revision);
    }

    [Fact]
    public async Task MissingIndexMeansNoPackageButUnreadableDoesNot()
    {
        await using var rig = await CreateAsync();
        Assert.Empty(rig.Plugins.InstalledStrict()); // fichier absent : ensemble vide, révision 0
        Assert.Equal(0, (await rig.Central.CurrentAsync(CancellationToken.None)).Revision);
        System.IO.Directory.CreateDirectory(rig.Plugins.Directory);
        rig.CorruptPluginIndex();
        Assert.Throws<StoreIndexUnreadableException>(() => rig.Plugins.InstalledStrict());
        Assert.Empty(rig.Plugins.Installed()); // l'accesseur historique garde son comportement (liste vide + journal)
    }

    [Fact]
    public async Task ConcurrentReadsAfterAChangeAddExactlyOneToTheRevision()
    {
        await using var rig = await CreateAsync();
        rig.WriteIndexes([Plugin("base", "1.0.0", ShaA)], []);
        Assert.Equal(1, (await rig.Central.CurrentAsync(CancellationToken.None)).Revision);
        rig.WriteIndexes([Plugin("base", "1.1.0", ShaB)], []);
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => rig.Central.CurrentAsync(CancellationToken.None))));
        Assert.All(results, state => { Assert.Equal(2, state.Revision); Assert.Equal("1.1.0", Assert.Single(state.Items).Version); });
        Assert.Equal(2, (await rig.Repository.GetAsync(CancellationToken.None)).Revision);
    }

    [Fact]
    public async Task ConcurrentReadsWhileThePackageSetKeepsChangingNeverRegressTheDigest()
    {
        await using var rig = await CreateAsync();
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 25; i++) await rig.Central.CurrentAsync(CancellationToken.None);
        })).ToArray();
        for (var version = 1; version <= 6; version++)
        {
            rig.WriteIndexes([Plugin("base", $"1.{version}.0", ShaA)], []);
            await Task.Delay(5);
        }
        await Task.WhenAll(readers);
        var final = await rig.Central.CurrentAsync(CancellationToken.None);
        // La révision finale décrit l'ensemble réellement installé, et relire n'en consomme plus : l'empreinte n'a pas été ramenée à un état périmé.
        Assert.Equal("1.6.0", Assert.Single(final.Items).Version);
        Assert.Equal(final.Revision, (await rig.Central.CurrentAsync(CancellationToken.None)).Revision);
        Assert.Equal(final.Revision, (await rig.Repository.GetAsync(CancellationToken.None)).Revision);
        Assert.InRange(final.Revision, 1, 6); // au plus un cran par version écrite
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
