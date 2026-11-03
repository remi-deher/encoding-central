using Encoding.WorkerAgent.Services;

namespace Encoding.WorkerAgent.Runtime;

/// <summary>
/// Tient ouvert le flux SSE <c>runtime/workers/{id}/wake</c> du central et réveille les boucles de l'agent à chaque signal.
/// Le flux ne porte aucune donnée : les boucles réclament ensuite par leurs POST transactionnels, donc baux et fences ne changent pas.
/// Tant que le flux est coupé, <see cref="WorkerWakeSignal.Connected"/> est faux et chaque boucle reprend son ancien délai.
/// </summary>
public sealed class WorkerWakeListener(
    WorkerAgentOptions options,
    WorkerIdentityStore identities,
    WorkerWakeSignal signal,
    ILogger<WorkerWakeListener> logger) : BackgroundService
{
    /// <summary>Le central envoie un ping toutes les 15 s : au-delà, le flux est considéré comme mort.</summary>
    private static readonly TimeSpan Silence = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var identity = await identities.GetAsync(ct);
                if (identity.IsEnrolled)
                {
                    foreach (var serverUrl in identity.GetCandidateServerUrls(options))
                    {
                        try { await ListenAsync(identity, serverUrl, ct); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception exception) { logger.LogDebug("Flux de réveil indisponible ({Type}).", exception.GetType().Name); }
                        finally { signal.SetConnected(false); }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogDebug("Écoute du central momentanément indisponible ({Type}).", exception.GetType().Name); }
            try { await Task.Delay(Retry, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private async Task ListenAsync(WorkerIdentityState identity, string serverUrl, CancellationToken ct)
    {
        using var client = new RuntimeClient(identity, serverUrl);
        using var response = await client.OpenWakeStreamAsync(ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        string? eventType = null;
        while (!ct.IsCancellationRequested)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(Silence);
            var line = await reader.ReadLineAsync(idle.Token);
            if (line is null) return;
            if (line.StartsWith("event:", StringComparison.Ordinal)) { eventType = line[6..].Trim(); continue; }
            if (line.Length != 0) continue;
            // Une ligne vide termine l'événement : « ready » confirme la connexion, « wake » annonce du travail, « ping » n'annonce rien.
            if (eventType == "ready") signal.SetConnected(true);
            else if (eventType == "wake") signal.Pulse();
            eventType = null;
        }
    }
}
