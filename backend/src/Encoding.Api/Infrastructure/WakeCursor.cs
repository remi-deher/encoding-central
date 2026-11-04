using Encoding.Api.Executions;

namespace Encoding.Api.Infrastructure;

/// <summary>
/// Curseur sur les deux diffuseurs du central : attend « du travail est peut-être prêt » (exécution, bibliothèque ou worker modifié).
/// Partagé par le flux de réveil des workers et par le service d'attentes de flow, pour que les deux réagissent aux mêmes événements.
/// </summary>
public sealed class WakeCursor(ExecutionStreamBroadcaster executions, InterfaceLiveBroadcaster live)
{
    private long executionVersion = executions.CurrentVersion;
    private long liveVersion = live.CurrentVersion;

    /// <summary>Rend true si un changement utile est arrivé, false si le délai est écoulé sans rien (ping).</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var executionWait = executions.WaitForChangeAsync(executionVersion, timeout, stop.Token);
        var liveWait = live.WaitAsync(liveVersion, timeout, stop.Token);
        await Task.WhenAny(executionWait, liveWait);
        // Libère l'attente restante pour ne pas laisser un abonné fantôme dans le diffuseur.
        stop.Cancel();
        ct.ThrowIfCancellationRequested();

        var woke = false;
        var nextExecution = await Settled(executionWait, executionVersion);
        if (nextExecution > executionVersion) { executionVersion = nextExecution; woke = true; }
        var signal = await Settled(liveWait, liveVersion);
        if (signal.Version > liveVersion)
        {
            liveVersion = signal.Version;
            woke |= signal.Topics.Any(topic => topic is "executions" or "libraries" or "workers");
        }
        return woke;
    }

    private static async Task<long> Settled(Task<long> wait, long fallback)
    {
        try { return await wait; }
        catch (OperationCanceledException) { return fallback; }
    }

    private static async Task<LiveSignal> Settled(Task<LiveSignal> wait, long fallback)
    {
        try { return await wait; }
        catch (OperationCanceledException) { return new LiveSignal(fallback, []); }
    }
}
