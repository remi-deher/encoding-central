namespace Encoding.Api.Plugins;

/// <summary>
/// Signal en mémoire « l'état désiré d'au moins un worker a changé ». Il ne porte aucune donnée : les flux SSE relisent l'état persisté,
/// donc un signal manqué (redémarrage, course) est rattrapé par le prochain réveil ou par la reconnexion avec <c>Last-Event-ID</c>.
/// </summary>
public sealed class StoreStateBroadcaster
{
    private readonly object gate = new();
    private long version;
    private TaskCompletionSource next = NewSignal();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public long CurrentVersion { get { lock (gate) return version; } }

    public void Notify()
    {
        TaskCompletionSource previous;
        lock (gate) { version++; previous = next; next = NewSignal(); }
        previous.TrySetResult();
    }

    /// <summary>Attend un changement après <paramref name="seen"/> ; rend la version courante (inchangée si le délai est écoulé).</summary>
    public async Task<long> WaitAsync(long seen, TimeSpan timeout, CancellationToken ct)
    {
        Task wait;
        lock (gate)
        {
            if (version > seen) return version;
            wait = next.Task;
        }
        try { await wait.WaitAsync(timeout, ct); }
        catch (TimeoutException) { }
        return CurrentVersion;
    }
}
