namespace Encoding.Api.Plugins;

/// <summary>Point d'entrée applicatif de l'état Store par worker ; les règles d'écriture (P1, P3) s'y ajouteront.</summary>
public sealed class WorkerStoreStateService(WorkerStoreStateRepository repository)
{
    public Task<WorkerStoreState?> GetAsync(Guid workerId, CancellationToken ct) => repository.GetAsync(workerId, ct);
}
