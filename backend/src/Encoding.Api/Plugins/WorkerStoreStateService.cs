namespace Encoding.Api.Plugins;

/// <summary>Demande d'activation : version exacte seulement. L'empreinte n'est jamais fournie par le client.</summary>
public sealed record StoreDesiredRequestItem(string? Kind, string? Id, string? Version, string? Platform);
public sealed record SetStoreStateRequest(IReadOnlyList<StoreDesiredRequestItem>? Items);
public sealed record SetStoreStateResponse(Guid WorkerId, long Revision, IReadOnlyList<StoreDesiredItem> Items);

/// <summary>Erreur de validation ou de catalogue (HTTP 422) ; le message ne contient aucun secret.</summary>
public sealed class StoreCatalogException(string message) : Exception(message);

/// <summary>
/// État Store par worker. L'état désiré est résolu contre le catalogue du dépôt officiel mis en cache par le central :
/// le central retient l'empreinte SHA-256 officielle, jamais une valeur fournie par le client, et n'a besoin d'aucun accès réseau
/// si le catalogue est déjà synchronisé (une panne du Store ne supprime ni ne modifie l'état déjà persisté).
/// </summary>
public sealed class WorkerStoreStateService(WorkerStoreStateRepository repository, PluginRepositoryService repositories)
{
    public Task<WorkerStoreState?> GetAsync(Guid workerId, CancellationToken ct) => repository.GetAsync(workerId, ct);

    /// <summary>Remplace l'état désiré. <c>null</c> si le worker est inconnu ; <see cref="StoreCatalogException"/> si un élément est invalide ou absent du catalogue.</summary>
    public async Task<SetStoreStateResponse?> SetDesiredAsync(Guid workerId, SetStoreStateRequest request, CancellationToken ct)
    {
        if (request.Items is null) throw new StoreCatalogException("La liste « items » est obligatoire.");
        if (await repository.GetAsync(workerId, ct) is null) return null;
        var official = await OfficialCatalogAsync(ct);
        var resolved = request.Items.Select(item => Resolve(official, item)).ToList();
        try
        {
            var state = await repository.ReplaceDesiredAsync(workerId, resolved, ct);
            return state is null ? null : new SetStoreStateResponse(workerId, state.Revision, state.Items);
        }
        catch (StoreStateException exception) { throw new StoreCatalogException(exception.Message); }
    }

    private async Task<PluginRepository> OfficialCatalogAsync(CancellationToken ct)
    {
        var official = repositories.List().FirstOrDefault(item => item.IsOfficial)
            ?? throw new StoreCatalogException("Le dépôt officiel n'est pas configuré.");
        // Catalogue jamais synchronisé : une seule tentative ; sinon on s'appuie sur le cache persisté.
        if (official.LastSyncAt is null && official.Packages.Count == 0 && (official.Tools?.Count ?? 0) == 0)
            official = await repositories.SyncAsync(official.Id, ct) ?? official;
        if (official.Packages.Count == 0 && (official.Tools?.Count ?? 0) == 0)
            throw new StoreCatalogException("Le catalogue officiel n'est pas disponible dans le cache du central ; synchronisez le dépôt officiel.");
        return official;
    }

    private static StoreDesiredItem Resolve(PluginRepository catalog, StoreDesiredRequestItem item)
    {
        var kind = item.Kind ?? "";
        var id = item.Id ?? "";
        var version = item.Version ?? "";
        if (version.Length == 0 || !System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+$"))
            throw new StoreCatalogException($"Une version exacte X.Y.Z est exigée pour {id} : pas de version flottante.");
        string? sha = kind switch
        {
            "plugin" when item.Platform is null => catalog.Packages.FirstOrDefault(p => p.Id == id)?.Versions.FirstOrDefault(v => v.Version == version)?.Sha256,
            "plugin" => throw new StoreCatalogException("Un plugin n'a pas de plateforme."),
            "tool" when string.IsNullOrEmpty(item.Platform) => throw new StoreCatalogException($"La plateforme est requise pour l'outil {id}."),
            "tool" => catalog.Tools?.FirstOrDefault(t => t.Id == id)?.Versions.FirstOrDefault(v => v.Version == version && v.Platform == item.Platform)?.Sha256,
            _ => throw new StoreCatalogException("Le type doit être « plugin » ou « tool ».")
        };
        if (sha is null) throw new StoreCatalogException($"{kind} {id} {version}{(item.Platform is null ? "" : " (" + item.Platform + ")")} est absent du catalogue officiel activé.");
        return new StoreDesiredItem(kind, id, version, item.Platform, sha.ToLowerInvariant());
    }
}
