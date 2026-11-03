namespace Encoding.WorkerAgent.Runtime;

/// <summary>Arrête les opérations avant l'expiration confirmée du bail, même pendant une requête bloquée.</summary>
public sealed class WorkerLeaseDeadline : IDisposable
{
    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(2);
    private readonly object gate = new();
    private readonly TimeProvider time;
    private readonly Action expired;
    private readonly ITimer timer;
    private long started;
    private TimeSpan duration;
    private bool stopped;

    public WorkerLeaseDeadline(DateTimeOffset expiresAt, TimeProvider time, Action expired)
    {
        this.time = time;
        this.expired = expired;
        timer = time.CreateTimer(_ => CheckExpiry(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Arm(expiresAt);
    }

    public TimeSpan Remaining
    {
        get { lock (gate) return duration - time.GetElapsedTime(started); }
    }

    /// <summary>Une réponse tardive ne peut jamais réactiver un bail déjà expiré localement.</summary>
    public bool TryRenew(DateTimeOffset expiresAt)
    {
        lock (gate)
        {
            if (stopped || duration <= time.GetElapsedTime(started)) return false;
            Arm(expiresAt);
            return duration > TimeSpan.Zero;
        }
    }

    private void Arm(DateTimeOffset expiresAt)
    {
        started = time.GetTimestamp();
        duration = expiresAt - time.GetUtcNow() - Margin;
        timer.Change(duration > TimeSpan.Zero ? duration : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private void CheckExpiry()
    {
        lock (gate)
        {
            // Un callback déjà en file avant un renouvellement ne doit pas annuler le nouveau bail.
            if (stopped || duration > time.GetElapsedTime(started)) return;
            stopped = true;
            expired();
        }
    }

    public void Dispose()
    {
        lock (gate) { stopped = true; timer.Dispose(); }
    }
}
