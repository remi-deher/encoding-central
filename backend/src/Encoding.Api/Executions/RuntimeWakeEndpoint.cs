namespace Encoding.Api.Executions;

/// <summary>
/// Flux SSE qui réveille un worker quand du travail peut l'attendre (tâche, scan, remplacement, parcours de dossier, transfert).
/// Le flux ne porte aucune donnée et n'attribue rien : le worker réclame ensuite par les POST transactionnels habituels,
/// si bien que baux, fences et idempotence restent inchangés. Un flux coupé ne perd rien : le worker reprend son délai de secours.
/// </summary>
public static class RuntimeWakeEndpoint
{
    public static void MapRuntimeWakeEndpoint(this RouteGroupBuilder group)
    {
        group.MapGet("/wake", async (HttpContext http, CancellationToken ct) =>
        {
            var worker = Guid.Parse((string)http.Request.RouteValues["workerId"]!);
            var token = http.Request.Headers["X-Node-Token"].ToString();
            var coordinator = http.RequestServices.GetRequiredService<RuntimeExecutionCoordinator>();
            var streams = http.RequestServices.GetRequiredService<RuntimeWakeStreams>();
            ExecutionStreamBroadcaster.PrepareResponse(http.Response);
            await streams.RunAsync(worker, cancellation => coordinator.AuthenticateAsync(worker, token, cancellation),
                (kind, cancellation) => Write(http, kind, cancellation), ct);
        }).WithName("StreamRuntimeWake");
    }

    private static async Task Write(HttpContext http, string eventType, CancellationToken ct)
    {
        await http.Response.WriteAsync($"event: {eventType}\ndata: {{}}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
