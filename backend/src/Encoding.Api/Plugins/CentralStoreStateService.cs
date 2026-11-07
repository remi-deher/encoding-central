using System.Security.Cryptography;

namespace Encoding.Api.Plugins;

/// <summary>
/// État Store global du central (ADR 0046) : l'état désiré de tous les workers est l'ensemble des paquets installés sur le central
/// (plugins et paquets d'outils de chaque plateforme, avec leur SHA-256), jamais une liste par worker. La révision monotone augmente
/// quand cet ensemble change ; elle est alignée sur l'empreinte de l'ensemble à chaque lecture, donc même un index modifié hors des
/// chemins d'installation (restauration, copie de fichiers) est rattrapé. Les services d'installation ne font que signaler un changement.
/// </summary>
public sealed class CentralStoreStateService
{
    private readonly CentralStoreStateRepository repository;
    private readonly PluginStoreService plugins;
    private readonly ToolStoreService tools;
    private readonly StoreStateBroadcaster broadcaster;
    private readonly ILogger<CentralStoreStateService> logger;

    public CentralStoreStateService(CentralStoreStateRepository repository, PluginStoreService plugins, ToolStoreService tools,
        StoreStateBroadcaster broadcaster, ILogger<CentralStoreStateService> logger)
    {
        this.repository = repository; this.plugins = plugins; this.tools = tools; this.broadcaster = broadcaster; this.logger = logger;
        plugins.Changed += OnInstalledSetChanged;
        tools.Changed += OnInstalledSetChanged;
    }

    /// <summary>État complet courant : révision globale et paquets installés avec leur empreinte.</summary>
    public async Task<StoreDesiredState> CurrentAsync(CancellationToken ct)
    {
        var items = InstalledItems();
        var revision = await repository.SyncAsync(Digest(items), ct);
        if (revision.Changed) broadcaster.Notify();
        return new StoreDesiredState(revision.Revision, items, revision.UpdatedAt);
    }

    /// <summary>Paquets installés sur le central, triés (type, identifiant, plateforme) : plugins puis outils.</summary>
    public IReadOnlyList<StoreDesiredItem> InstalledItems() =>
        plugins.Installed().Select(item => new StoreDesiredItem("plugin", item.Id, item.Version, null, item.Sha256.ToLowerInvariant()))
            .Concat(tools.Installed().Select(item => new StoreDesiredItem("tool", item.Id, item.Version, item.Platform, item.ArchiveSha256.ToLowerInvariant())))
            .OrderBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Platform, StringComparer.Ordinal).ToList();

    /// <summary>Empreinte de l'ensemble ; vide pour aucun paquet (révision 0 d'un central neuf).</summary>
    public static string Digest(IReadOnlyList<StoreDesiredItem> items) => items.Count == 0 ? "" : Convert.ToHexStringLower(SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(string.Join('\n', items.Select(item => $"{item.Kind}|{item.Id}|{item.Version}|{item.Platform}|{item.Sha256}")))));

    private void OnInstalledSetChanged()
    {
        // Appelé sous le verrou d'installation : ne bloque pas l'appelant. Un échec est rattrapé à la prochaine lecture (empreinte).
        _ = Task.Run(async () =>
        {
            try { await CurrentAsync(CancellationToken.None); }
            catch (Exception exception) { logger.LogWarning(exception, "Révision de l'état Store non mise à jour après un changement de paquets."); }
        });
    }
}
