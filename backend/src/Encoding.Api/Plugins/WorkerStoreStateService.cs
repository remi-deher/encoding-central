namespace Encoding.Api.Plugins;

/// <summary>
/// État Store vu d'un worker (ADR 0046) : l'état désiré est celui, global, du central ; l'état appliqué est le dernier rapport du worker.
/// Aucune liste par worker, aucune saisie administrateur : un worker suit ce que le central a installé.
/// </summary>
public sealed class WorkerStoreStateService(WorkerStoreStateRepository repository, CentralStoreStateService central,
    StoreStateBroadcaster broadcaster, Encoding.Api.Workers.WorkerRepository workers)
{
    /// <summary>Identité du worker d'après son jeton ; jamais d'après un identifiant fourni par lui.</summary>
    public Task<Guid?> WorkerIdForTokenAsync(string token, CancellationToken ct) => workers.WorkerIdForTokenAsync(token, ct);

    /// <summary>Vrai si le central a marqué ce worker de version d'application incompatible (ADR 0039).</summary>
    public Task<bool> IsIncompatibleAsync(Guid workerId, CancellationToken ct) => workers.IsIncompatibleAsync(workerId, ct);

    public StoreStateBroadcaster Changes => broadcaster;

    /// <summary>État global courant du central (révision et paquets installés).</summary>
    public Task<StoreDesiredState> CurrentAsync(CancellationToken ct) => central.CurrentAsync(ct);

    /// <summary>Révision globale, état appliqué et écart pour un worker ; <c>null</c> si le worker est inconnu ou retiré.</summary>
    public async Task<WorkerStoreState?> GetAsync(Guid workerId, CancellationToken ct)
    {
        if (await repository.GetAppliedAsync(workerId, ct) is not { } applied) return null;
        var desired = await central.CurrentAsync(ct);
        return new WorkerStoreState(workerId, desired, applied, ComputeGap(desired, applied));
    }

    public Task<StoreReportResult> ReportAppliedAsync(Guid workerId, long revision, IReadOnlyList<StoreAppliedItem> items, CancellationToken ct) =>
        repository.ReportAppliedAsync(workerId, revision, items, ct);

    /// <summary>
    /// Écart entre le désiré (central) et le dernier rapport. <c>missing</c> : jamais appliqué ou retiré ; <c>version_mismatch</c> : autre version ou
    /// autre empreinte ; <c>failed</c> : échec rapporté ; <c>extra</c> : appliqué mais plus installé sur le central (suppression en attente).
    /// Un worker qui n'a encore rien rapporté (révision 0) a donc tous les paquets en <c>missing</c>.
    /// </summary>
    public static StoreGap ComputeGap(StoreDesiredState desired, StoreAppliedState applied)
    {
        static (string, string, string?) Key(string kind, string id, string? platform) => (kind, id, platform);
        var reported = applied.Items.ToDictionary(item => Key(item.Kind, item.Id, item.Platform));
        var items = new List<StoreGapItem>();
        foreach (var want in desired.Items)
        {
            if (!reported.TryGetValue(Key(want.Kind, want.Id, want.Platform), out var have) || have.Status == "removed")
                items.Add(new(want.Kind, want.Id, want.Platform, "missing", want.Version, null, null));
            else if (have.Status == "failed")
                items.Add(new(want.Kind, want.Id, want.Platform, "failed", want.Version, have.Version, have.ErrorCode));
            else if (have.Version != want.Version || !string.Equals(have.Sha256, want.Sha256, StringComparison.OrdinalIgnoreCase))
                items.Add(new(want.Kind, want.Id, want.Platform, "version_mismatch", want.Version, have.Version, null));
        }
        var wanted = desired.Items.Select(item => Key(item.Kind, item.Id, item.Platform)).ToHashSet();
        foreach (var have in applied.Items.Where(item => item.Status == "applied" && !wanted.Contains(Key(item.Kind, item.Id, item.Platform))))
            items.Add(new(have.Kind, have.Id, have.Platform, "extra", null, have.Version, null));
        return new StoreGap(Math.Max(0, desired.Revision - applied.Revision), items.Count == 0, items);
    }
}
