using Encoding.Api.Infrastructure;

namespace Encoding.Api.Executions;

/// <summary>
/// Flux SSE qui réveille un worker quand du travail peut l'attendre (tâche, scan, remplacement, parcours de dossier, transfert).
/// Le flux ne porte aucune donnée et n'attribue rien : le worker réclame ensuite par les POST transactionnels habituels,
/// si bien que baux, fences et idempotence restent inchangés. Un flux coupé ne perd rien : le worker reprend son délai de secours.
/// </summary>
public static class RuntimeWakeEndpoint
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

    public static void MapRuntimeWakeEndpoint(this RouteGroupBuilder group)
    {
        // Services résolus dans le handler : une route qui exige un service absent casserait la construction de tout le groupe.
        group.MapGet("/wake", async (HttpContext http, CancellationToken ct) =>
        {
            var cursor = new WakeCursor(
                http.RequestServices.GetRequiredService<ExecutionStreamBroadcaster>(),
                http.RequestServices.GetRequiredService<InterfaceLiveBroadcaster>());
            ExecutionStreamBroadcaster.PrepareResponse(http.Response);
            await Write(http, "ready", ct);
            try
            {
                while (!ct.IsCancellationRequested)
                    await Write(http, await cursor.WaitAsync(Heartbeat, ct) ? "wake" : "ping", ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* le worker a fermé le flux */ }
        }).WithName("StreamRuntimeWake");
    }

    private static async Task Write(HttpContext http, string eventType, CancellationToken ct)
    {
        await http.Response.WriteAsync($"event: {eventType}\ndata: {{}}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
