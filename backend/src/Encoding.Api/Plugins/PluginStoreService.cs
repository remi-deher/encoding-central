using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Contracts.Plugins;
using Encoding.Contracts.Tools;
using Encoding.Core.Plugins;

namespace Encoding.Api.Plugins;

public sealed record InstalledPlugin(string Id, string Version, string Name, string Sha256, string Source, DateTimeOffset InstalledAt);

/// <summary>
/// Paquets de plugins installés sur CE serveur (0063) : archive vérifiée, enregistrée dans le catalogue sans redémarrage et
/// proposée aux workers, qui la téléchargent et la contrôlent par son empreinte. L'état est un dossier local du serveur
/// (<c>PluginStore:Directory</c>) : il n'est pas répliqué entre serveurs d'un même central, chaque serveur reçoit les paquets
/// qui lui sont importés ou installés (résidu connu, voir 0063).
/// </summary>
public sealed class PluginStoreService(IConfiguration configuration, FlowComponentRegistry registry, ToolStoreService tools, ILogger<PluginStoreService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim gate = new(1, 1);
    public string Directory { get; } = configuration["PluginStore:Directory"]
        ?? Path.Combine(configuration["Worker:DataDirectory"] is { Length: > 0 } dataDirectory ? dataDirectory : Path.Combine(AppContext.BaseDirectory, "Data"), "plugin-store");

    private string IndexPath => Path.Combine(Directory, "installed.json");
    private string PackageDirectory(string id, string version) => Path.Combine(Directory, "packages", id, version);
    public string PackagePath(string id, string version) => Path.Combine(PackageDirectory(id, version), "package.zip");

    /// <summary>Index installé ; vide si le fichier n'existe pas, mais <see cref="StoreIndexUnreadableException"/> s'il est illisible (état inconnu, pas « aucun paquet »).</summary>
    public IReadOnlyList<InstalledPlugin> InstalledStrict()
    {
        try { return File.Exists(IndexPath) ? JsonSerializer.Deserialize<List<InstalledPlugin>>(File.ReadAllText(IndexPath), Json) ?? [] : []; }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            throw new StoreIndexUnreadableException("plugins", exception);
        }
    }

    /// <summary>
    /// Variante tolérante : un index illisible est journalisé et vu comme vide. Réservée aux lectures qui n'écrivent rien et ne décrivent pas l'état
    /// aux workers (rechargement du catalogue au démarrage). Toute mutation, et toute réponse qui affirme ce qui est installé, lit <see cref="InstalledStrict"/>.
    /// </summary>
    public IReadOnlyList<InstalledPlugin> Installed()
    {
        try { return InstalledStrict(); }
        catch (StoreIndexUnreadableException exception)
        {
            logger.LogError(exception.InnerException, "L'index des plugins installés est illisible : {Path}.", IndexPath);
            return [];
        }
    }

    /// <summary>Un plugin installé requiert-il ce paquet d'outil ? Lecture stricte : avec un index illisible on ne peut pas prouver que l'outil est libre.</summary>
    public bool RequiresToolPackage(string id)
    {
        foreach (var plugin in InstalledStrict())
        {
            try
            {
                var info = PluginPackage.Inspect(File.ReadAllBytes(PackagePath(plugin.Id, plugin.Version)));
                if ((info.Manifest.RequiredToolPackages ?? []).Any(dependency => dependency.ToolPackageId == id)) return true;
            }
            catch (Exception exception) when (exception is PluginPackageException or IOException) { }
        }
        return false;
    }

    /// <summary>Recharge dans le catalogue les paquets installés (au démarrage). Un paquet corrompu est signalé, jamais fatal.</summary>
    public void LoadInstalled()
    {
        foreach (var installed in Installed())
        {
            try
            {
                var info = PluginPackage.Inspect(File.ReadAllBytes(PackagePath(installed.Id, installed.Version)));
                if (info.Sha256 != installed.Sha256) throw new PluginPackageException("L'empreinte de l'archive ne correspond plus à celle enregistrée.");
                if ((info.Manifest.RequiredToolPackages ?? []).Any(dependency => !tools.Satisfies(dependency, ToolPackagePlatform.Current)))
                    throw new PluginPackageException("Une dépendance d'outil de ce plugin n'est plus installée à une version compatible.");
                if (!registry.TryInstall(info.Manifest, out var reason, info.Runtime.Type == "native")) throw new PluginPackageException(reason);
            }
            catch (Exception exception) when (exception is PluginPackageException or IOException)
            {
                logger.LogError(exception, "Le plugin installé {PluginId} {Version} n'a pas pu être rechargé.", installed.Id, installed.Version);
                registry.AddRejected($"{installed.Id}@{installed.Version}", exception.Message);
            }
        }
    }

    /// <summary>Installe une archive. <paramref name="source"/> : « sideload » ou « repo:&lt;id&gt; ».</summary>
    public async Task<InstalledPlugin> ImportAsync(byte[] archive, string source, string? expectedId = null, string? expectedVersion = null, string? expectedSha256 = null, bool allowOfficial = false)
    {
        var info = PluginPackage.Inspect(archive);
        var id = info.Manifest.Id;
        if (!PluginIdentity.IsQualifiedPluginId(id) && !allowOfficial)
            throw new PluginPackageException("Les identifiants de plugin non qualifiés sont réservés aux paquets officiels.");
        if (expectedSha256 is not null && !string.Equals(expectedSha256, info.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new PluginPackageException("L'empreinte SHA-256 de l'archive ne correspond pas à celle annoncée par le dépôt.");
        if (expectedId is not null && expectedId != id) throw new PluginPackageException($"L'archive contient « {id} », pas « {expectedId} ».");
        if (expectedVersion is not null && expectedVersion != info.Manifest.Version) throw new PluginPackageException($"L'archive contient la version {info.Manifest.Version}, pas {expectedVersion}.");
        if (PluginIdentity.IsOfficialNamespace(id) && !allowOfficial) throw new PluginPackageException("L'espace de noms « official » est réservé au dépôt officiel.");
        // Index des outils illisible : refus explicite (état inconnu) plutôt qu'un trompeur « dépendance absente ».
        if ((info.Manifest.RequiredToolPackages ?? []).Any()) _ = tools.InstalledStrict();
        if ((info.Manifest.RequiredToolPackages ?? []).FirstOrDefault(dependency => !tools.Satisfies(dependency, ToolPackagePlatform.Current)) is { } missing)
            throw new PluginPackageException($"Dépendance requise absente : {missing.ToolPackageId} {missing.MinimumVersion} ou plus compatible pour {ToolPackagePlatform.Current}.");
        await gate.WaitAsync();
        try
        {
            // Lecture stricte sous verrou, avant toute mutation : un index illisible n'est pas « aucun plugin ». Le réécrire avec le seul paquet importé
            // tronquerait l'état global diffusé aux workers ; on refuse sans toucher à l'index, aux archives, au catalogue ni à la révision.
            var current = InstalledStrict().ToList();
            var existing = current.SingleOrDefault(item => item.Id == id);
            if (existing is { } same && same.Version == info.Manifest.Version)
            {
                if (same.Sha256 == info.Sha256) return same;
                throw new PluginPackageException("Cette version est déjà installée avec un contenu différent : une version publiée est immuable, incrémentez la version.");
            }
            if (!registry.TryInstall(info.Manifest, out var reason, info.Runtime.Type == "native")) throw new PluginPackageException(reason);
            var directory = PackageDirectory(id, info.Manifest.Version);
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                var temporary = PackagePath(id, info.Manifest.Version) + ".tmp";
                await File.WriteAllBytesAsync(temporary, archive);
                File.Move(temporary, PackagePath(id, info.Manifest.Version), overwrite: true);
                var record = new InstalledPlugin(id, info.Manifest.Version, info.Manifest.Name, info.Sha256, source, DateTimeOffset.UtcNow);
                current.RemoveAll(item => item.Id == id);
                current.Add(record);
                Save(current);
                if (existing is not null) TryDelete(PackageDirectory(id, existing.Version));
                logger.LogInformation("Plugin {PluginId} {Version} installé depuis {Source}.", id, record.Version, source);
                return record;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Le catalogue a déjà été modifié : le ramener à l'état du disque.
                registry.Uninstall(id);
                if (existing is not null) LoadInstalled();
                TryDelete(directory);
                throw new PluginPackageException($"L'archive n'a pas pu être enregistrée : {exception.Message}");
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>Retire un plugin ; <c>false</c> s'il n'est pas installé. Lève <see cref="StoreIndexUnreadableException"/> si l'index est illisible (rien n'est modifié).</summary>
    public async Task<bool> RemoveAsync(string id)
    {
        await gate.WaitAsync();
        try
        {
            var current = InstalledStrict().ToList(); // état inconnu : ni « absent » (404) ni réécriture
            var existing = current.SingleOrDefault(item => item.Id == id);
            if (existing is null) return false;
            registry.Uninstall(id);
            current.Remove(existing);
            Save(current);
            TryDelete(Path.Combine(Directory, "packages", id));
            logger.LogInformation("Plugin {PluginId} retiré du Store.", id);
            return true;
        }
        finally { gate.Release(); }
    }

    /// <summary>Signalé après chaque écriture de l'index (installation, mise à jour, retrait) : l'état Store global du central s'aligne (ADR 0046).</summary>
    public event Action? Changed;

    private void Save(List<InstalledPlugin> current)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temporary = IndexPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(current, Json));
        File.Move(temporary, IndexPath, overwrite: true);
        Changed?.Invoke();
    }

    private static void TryDelete(string path)
    {
        try { if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Recharge les plugins installés avant que l'API accepte des requêtes.</summary>
public sealed class PluginStoreStartup(PluginStoreService store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        store.LoadInstalled();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
