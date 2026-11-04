namespace Encoding.Api.Plugins;

/// <summary>
/// État Store par worker (issue #17, P0) : lecture seule pour tout rôle authentifié. L'écriture de l'état désiré (administrateur,
/// empreinte résolue par le central) et le rapport d'état appliqué (jeton worker) arrivent avec les phases P1 et P3.
/// La route n'est pas ouverte aux jetons worker : un worker ne lit ni n'écrit l'état d'un autre.
/// </summary>
public static class WorkerStoreStateEndpoints
{
    public static void MapWorkerStoreStateEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/workers/{workerId:guid}/store-state", async (Guid workerId, WorkerStoreStateService states, CancellationToken ct) =>
            await states.GetAsync(workerId, ct) is { } state ? Results.Ok(state) : Results.NotFound())
            .WithTags("Store").WithName("GetWorkerStoreState");
    }
}
