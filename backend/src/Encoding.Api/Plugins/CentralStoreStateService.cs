using System.Security.Cryptography;

namespace Encoding.Api.Plugins;

/// <summary>
/// Un index de paquets (plugins ou outils) existe mais ne peut pas être lu : l'état installé est INCONNU, pas vide. Le message ne contient
/// ni chemin ni contenu ; l'exception d'origine reste dans <see cref="Exception.InnerException"/> pour le journal.
/// </summary>
public sealed class StoreIndexUnreadableException(string index, Exception inner) : Exception($"L'index des {index} installés est illisible : l'état Store du central est inconnu.", inner);

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
    // Un appel plus ancien (index lu avant une installation) ne doit jamais s'exécuter après un plus récent : il ré-incrémenterait la révision
    // et ramènerait l'empreinte à un ensemble périmé. Lecture de l'index et alignement de la révision forment donc une seule section critique.
    private readonly SemaphoreSlim gate = new(1, 1);

    public CentralStoreStateService(CentralStoreStateRepository repository, PluginStoreService plugins, ToolStoreService tools,
        StoreStateBroadcaster broadcaster, ILogger<CentralStoreStateService> logger)
    {
        this.repository = repository; this.plugins = plugins; this.tools = tools; this.broadcaster = broadcaster; this.logger = logger;
        plugins.Changed += OnInstalledSetChanged;
        tools.Changed += OnInstalledSetChanged;
    }

    /// <summary>
    /// État complet courant : révision globale et paquets installés avec leur empreinte. Lève <see cref="StoreIndexUnreadableException"/> si un index est
    /// illisible : la révision n'avance pas et rien n'est diffusé (un ensemble vide supprimerait les paquets de tous les workers).
    /// </summary>
    public async Task<StoreDesiredState> CurrentAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            IReadOnlyList<StoreDesiredItem> items;
            try { items = InstalledItems(); }
            catch (StoreIndexUnreadableException exception)
            {
                logger.LogWarning(exception.InnerException, "État Store du central inconnu : {Reason}", exception.Message);
                throw;
            }
            var revision = await repository.SyncAsync(Digest(items), ct);
            if (revision.Changed) broadcaster.Notify();
            return new StoreDesiredState(revision.Revision, items, revision.UpdatedAt);
        }
        finally { gate.Release(); }
    }

    /// <summary>Paquets installés sur le central, triés (type, identifiant, plateforme) : plugins puis outils.</summary>
    public IReadOnlyList<StoreDesiredItem> InstalledItems() =>
        plugins.InstalledStrict().Select(item => new StoreDesiredItem("plugin", item.Id, item.Version, null, item.Sha256.ToLowerInvariant()))
            .Concat(tools.InstalledStrict().Select(item => new StoreDesiredItem("tool", item.Id, item.Version, item.Platform, item.ArchiveSha256.ToLowerInvariant())))
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
            catch (StoreIndexUnreadableException) { } // déjà journalisé par CurrentAsync ; l'état reste inconnu
            catch (Exception exception) { logger.LogWarning(exception, "Révision de l'état Store non mise à jour après un changement de paquets."); }
        });
    }
}
