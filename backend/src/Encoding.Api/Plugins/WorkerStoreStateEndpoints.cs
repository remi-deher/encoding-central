namespace Encoding.Api.Plugins;

/// <summary>
/// État Store par worker (issue #17, P0) : lecture seule pour tout rôle authentifié. L'écriture de l'état désiré (PUT, administrateur,
/// empreinte résolue par le central depuis le catalogue officiel en cache) est en P1 ; le rapport d'état appliqué (jeton worker) arrive en P3.
/// La route n'est pas ouverte aux jetons worker : un worker ne lit ni n'écrit l'état d'un autre.
/// </summary>
public static class WorkerStoreStateEndpoints
{
    public static void MapWorkerStoreStateEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/workers/{workerId:guid}/store-state", async (Guid workerId, WorkerStoreStateService states, CancellationToken ct) =>
            await states.GetAsync(workerId, ct) is { } state ? Results.Ok(state) : Results.NotFound())
            .WithTags("Store").WithName("GetWorkerStoreState");

        // Écriture réservée aux administrateurs (règle commune de ManagementAccessHandler : méthode non sûre, pas de jeton worker).
        app.MapPut("/api/v1/workers/{workerId:guid}/store-state", async (Guid workerId, SetStoreStateRequest request, WorkerStoreStateService states, CancellationToken ct) =>
        {
            try { return await states.SetDesiredAsync(workerId, request, ct) is { } result ? Results.Ok(result) : Results.NotFound(); }
            catch (StoreCatalogException exception) { return Results.UnprocessableEntity(new { title = exception.Message }); }
        }).WithTags("Store").WithName("SetWorkerStoreState");
    }
}
