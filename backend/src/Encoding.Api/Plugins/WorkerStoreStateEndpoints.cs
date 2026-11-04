using Encoding.Api.Auth;

namespace Encoding.Api.Plugins;

/// <summary>
/// État Store par worker (issue #17, P0) : lecture seule pour tout rôle authentifié. L'écriture de l'état désiré (PUT, administrateur,
/// empreinte résolue par le central depuis le catalogue officiel en cache) est en P1 ; le rapport d'état appliqué (jeton worker) arrive en P3.
/// Seul le flux `/api/v1/store/events` est ouvert aux jetons worker.
/// </summary>
public static class WorkerStoreStateEndpoints
{
    public sealed record AppliedStateRequest(long Revision, IReadOnlyList<StoreAppliedItem>? Items);
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

        app.MapPut("/api/v1/store/applied-state", async (HttpContext http, AppliedStateRequest request, WorkerStoreStateService states, CancellationToken ct) =>
        {
            var token = http.Request.Headers["X-Node-Token"].ToString();
            if (token.Length == 0 || await states.WorkerIdForTokenAsync(token, ct) is not { } workerId)
                return Results.Unauthorized();
            if (request.Items is null || request.Revision < 0)
                return Results.UnprocessableEntity(new { code = "invalid_report" });
            try
            {
                var result = await states.ReportAppliedAsync(workerId, request.Revision, request.Items, ct);
                return result.Outcome switch
                {
                    StoreReportOutcome.Accepted => Results.Ok(new { revision = request.Revision }),
                    StoreReportOutcome.StaleRevision or StoreReportOutcome.UnknownRevision => Results.Conflict(new { desiredRevision = result.DesiredRevision }),
                    _ => Results.Unauthorized()
                };
            }
            catch (StoreStateException) { return Results.UnprocessableEntity(new { code = "invalid_report" }); }
        }).AllowNodeCredentials().WithTags("Store").WithName("ReportWorkerAppliedStoreState");

        // Flux SSE worker ← central. L'identité vient du jeton X-Node-Token ; la route ne lit aucun identifiant fourni par le worker.
        app.MapGet("/api/v1/store/events", async (HttpContext http, WorkerStoreStateService states) =>
        {
            var ct = http.RequestAborted;
            var token = http.Request.Headers["X-Node-Token"].ToString();
            if (token.Length == 0 || await states.WorkerIdForTokenAsync(token, ct) is not { } workerId) { http.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
            Encoding.Api.Executions.ExecutionStreamBroadcaster.PrepareResponse(http.Response);
            await http.Response.Body.FlushAsync(ct);
            await StreamAsync(http, states, workerId, http.Request.Headers["Last-Event-ID"].ToString(), ct);
        }).AllowNodeCredentials().WithTags("Store").WithName("StreamStoreEvents");
    }

    public static TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(20);

    private static async Task StreamAsync(HttpContext http, WorkerStoreStateService states, Guid workerId, string lastEventId, CancellationToken ct)
    {
        try
        {
            var version = states.Changes.CurrentVersion; // lu avant l'état : un changement concurrent réveille la boucle
            if (await states.GetAsync(workerId, ct) is not { } state) return;
            var sent = state.Desired.Revision;
            var first = StoreEventStream.Initial(string.IsNullOrEmpty(lastEventId) ? null : lastEventId, state.Desired.Revision);
            if (first != StoreEventKind.None) await Write(http, StoreEventStream.Frame(first, state.Desired), ct);
            else await Write(http, StoreEventStream.Heartbeat, ct);
            while (!ct.IsCancellationRequested)
            {
                var next = await states.Changes.WaitAsync(version, HeartbeatInterval, ct);
                if (next == version) { await Write(http, StoreEventStream.Heartbeat, ct); continue; }
                version = next;
                if (await states.GetAsync(workerId, ct) is not { } current) return; // worker retiré
                if (current.Desired.Revision > sent)
                {
                    sent = current.Desired.Revision;
                    await Write(http, StoreEventStream.Frame(StoreEventKind.DesiredState, current.Desired), ct);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task Write(HttpContext http, string frame, CancellationToken ct)
    {
        await http.Response.WriteAsync(frame, ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
