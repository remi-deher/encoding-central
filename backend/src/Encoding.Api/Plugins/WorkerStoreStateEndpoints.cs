using Encoding.Api.Auth;

namespace Encoding.Api.Plugins;

/// <summary>
/// État Store des workers (ADR 0046) : l'état désiré est global (paquets installés sur le central), il n'y a plus d'écriture par worker.
/// Lecture de l'état d'un worker pour tout rôle authentifié ; rapport d'état appliqué et flux `/api/v1/store/events` ouverts aux jetons worker.
/// </summary>
public static class WorkerStoreStateEndpoints
{
    public sealed record AppliedStateRequest(long Revision, IReadOnlyList<StoreAppliedItem>? Items);
    public static void MapWorkerStoreStateEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/workers/{workerId:guid}/store-state", async (Guid workerId, WorkerStoreStateService states, CancellationToken ct) =>
            await states.GetAsync(workerId, ct) is { } state ? Results.Ok(state) : Results.NotFound())
            .WithTags("Store").WithName("GetWorkerStoreState");

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
            if (await states.IsIncompatibleAsync(workerId, ct))
            {
                // ADR 0046, point 14 : pas de flux Store pour un worker de version d'application incompatible (ADR 0039).
                http.Response.StatusCode = StatusCodes.Status409Conflict;
                await http.Response.WriteAsJsonAsync(new { code = "version_incompatible" }, ct);
                return;
            }
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
            var state = await states.CurrentAsync(ct);
            var sent = state.Revision;
            var first = StoreEventStream.Initial(string.IsNullOrEmpty(lastEventId) ? null : lastEventId, state.Revision);
            if (first != StoreEventKind.None) await Write(http, StoreEventStream.Frame(first, state), ct);
            else await Write(http, StoreEventStream.Heartbeat, ct);
            while (!ct.IsCancellationRequested)
            {
                var next = await states.Changes.WaitAsync(version, HeartbeatInterval, ct);
                if (next == version) { await Write(http, StoreEventStream.Heartbeat, ct); continue; }
                version = next;
                if (await states.GetAsync(workerId, ct) is null) return; // worker retiré
                var current = await states.CurrentAsync(ct);
                if (current.Revision > sent)
                {
                    sent = current.Revision;
                    await Write(http, StoreEventStream.Frame(StoreEventKind.DesiredState, current), ct);
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
