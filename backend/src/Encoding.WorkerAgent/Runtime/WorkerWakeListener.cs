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
    TimeProvider time,
    ILogger<WorkerWakeListener> logger, WakeReconnectPolicy? reconnectPolicy = null, WorkerSocketClient? socketClient = null) : BackgroundService
{
    /// <summary>Le central envoie un ping toutes les 15 s : au-delà, le flux est considéré comme mort.</summary>
    private static readonly TimeSpan Silence = TimeSpan.FromSeconds(45);
    private readonly WakeReconnectPolicy reconnect = reconnectPolicy ?? new(time);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (socketClient?.Connected == true)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(1), time, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                continue;
            }
            try
            {
                var identity = await identities.GetAsync(ct);
                if (identity.IsEnrolled)
                {
                    foreach (var serverUrl in identity.GetCandidateServerUrls(options))
                    {
                        if (socketClient?.Connected == true) break;
                        try { await ListenAsync(identity, serverUrl, ct); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception exception) { logger.LogDebug("Flux de réveil indisponible ({Type}).", exception.GetType().Name); }
                        finally { if (socketClient?.Connected != true) signal.SetConnected(false); }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogDebug("Écoute du central momentanément indisponible ({Type}).", exception.GetType().Name); }
            try { await Task.Delay(reconnect.NextDelay(), time, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private async Task ListenAsync(WorkerIdentityState identity, string serverUrl, CancellationToken ct)
    {
        using var client = new RuntimeClient(identity, serverUrl);
        using var response = await client.OpenWakeStreamAsync(ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        if (socketClient is null)
        {
            await ReadEventsAsync(reader, ct);
            return;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var read = ReadEventsAsync(reader, linked.Token);
        try
        {
            while (!read.IsCompleted && socketClient?.Connected != true)
                await Task.WhenAny(read, Task.Delay(TimeSpan.FromMilliseconds(250), time, ct));
            if (socketClient?.Connected == true) linked.Cancel();
            await read;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && socketClient?.Connected == true) { }
    }

    /// <summary>Lit les événements SSE jusqu'à la fin du flux ; un silence de plus de <see cref="Silence"/> coupe la lecture.</summary>
    internal async Task ReadEventsAsync(TextReader reader, CancellationToken ct)
    {
        string? eventType = null;
        while (!ct.IsCancellationRequested)
        {
            using var silence = new CancellationTokenSource(Silence, time);
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct, silence.Token);
            var line = await reader.ReadLineAsync(idle.Token);
            if (line is null) return;
            if (line.StartsWith("event:", StringComparison.Ordinal)) { eventType = line[6..].Trim(); continue; }
            if (line.StartsWith("retry:", StringComparison.Ordinal)) { ApplyServerRetry(line[6..]); continue; }
            if (line.Length != 0) continue;
            // Une ligne vide termine l'événement : « ready » confirme la connexion, « wake » annonce du travail, « ping » n'annonce rien.
            if (eventType == "ready") { signal.SetConnected(true); reconnect.MarkConnected(); }
            else if (eventType == "wake") signal.Pulse();
            eventType = null;
        }
    }

    private void ApplyServerRetry(string value)
    {
        if (long.TryParse(value.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var milliseconds)
            && milliseconds <= TimeSpan.MaxValue.TotalMilliseconds)
            reconnect.SetServerRetry(TimeSpan.FromMilliseconds(milliseconds));
    }
}
