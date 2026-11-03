namespace Encoding.WorkerAgent.Runtime;

/// <summary>
/// Signal de réveil partagé par les boucles de l'agent. Le central le déclenche par SSE (<see cref="WorkerWakeListener"/>) ;
/// une boucle qui termine un travail local peut aussi le déclencher pour libérer sa capacité sans attendre.
/// Sans flux, chaque boucle reprend exactement son ancien délai : le SSE réveille plus tôt, il ne remplace pas la reprise.
/// </summary>
public sealed class WorkerWakeSignal
{
    /// <summary>Filet de sécurité quand le flux est connecté : borne la perte d'un signal sans retour au sondage rapide.</summary>
    public static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(30);

    private long version;
    private int connected;
    private TaskCompletionSource<bool> next = Create();

    public bool Connected => Volatile.Read(ref connected) == 1;

    public void SetConnected(bool value)
    {
        if (Interlocked.Exchange(ref connected, value ? 1 : 0) != (value ? 1 : 0)) Pulse();
    }

    public void Pulse()
    {
        Interlocked.Increment(ref version);
        Interlocked.Exchange(ref next, Create()).TrySetResult(true);
    }

    /// <summary>Une boucle crée son propre attendeur : chacune voit chaque signal une fois.</summary>
    public Waiter CreateWaiter() => new(this);

    private static TaskCompletionSource<bool> Create() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public sealed class Waiter(WorkerWakeSignal signal)
    {
        private long seen = Volatile.Read(ref signal.version);

        /// <summary>
        /// Rend la main dès qu'un signal est arrivé depuis le dernier passage, sinon après <paramref name="pollMilliseconds"/>
        /// (ou le filet de sécurité si le flux est connecté, s'il est plus long), avec un léger décalage anti-rafale.
        /// </summary>
        public async Task WaitAsync(int pollMilliseconds, CancellationToken ct)
        {
            var pending = signal.next.Task;
            var current = Volatile.Read(ref signal.version);
            if (current != seen) { seen = current; return; }
            var poll = TimeSpan.FromMilliseconds(pollMilliseconds);
            var delay = signal.Connected && poll < SafetyNet ? SafetyNet : poll;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timer = Task.Delay(delay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 501)), timeout.Token);
            try { await Task.WhenAny(pending, timer); }
            finally { timeout.Cancel(); }
            ct.ThrowIfCancellationRequested();
            seen = Volatile.Read(ref signal.version);
        }
    }
}
