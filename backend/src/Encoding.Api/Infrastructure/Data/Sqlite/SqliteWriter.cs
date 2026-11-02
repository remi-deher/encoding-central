using System.Diagnostics;

namespace Encoding.Api.Infrastructure.Data.Sqlite;

public sealed record SqliteWriterOptions
{
    public bool Enabled { get; init; } = true;
    public bool CriticalOnly { get; init; }
    public int Capacity { get; init; } = 256;
    public int UrgentCapacity { get; init; } = 32;
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan LongTransaction { get; init; } = TimeSpan.FromSeconds(1);
    public int BeginRetries { get; init; } = 2;

    public void Validate()
    {
        if (Capacity < 1 || UrgentCapacity < 1 || WaitTimeout <= TimeSpan.Zero
            || LongTransaction <= TimeSpan.Zero || BeginRetries is < 0 or > 10)
            throw new InvalidOperationException("Configuration Database:Writer invalide.");
    }
}

public sealed class SqliteWriteUnavailableException(string reason)
    : InvalidOperationException("Écriture SQLite temporairement indisponible : " + reason);

/// <summary>
/// Local transaction admission queue. The admitted caller owns the writer until commit/rollback;
/// no caller code is replayed, and cancellation of a waiter never cancels an admitted transaction.
/// </summary>
public sealed class SqliteWriter
{
    private static readonly AsyncLocal<bool> Urgent = new();
    private readonly object sync = new();
    private readonly LinkedList<TaskCompletionSource<IDisposable>> ordinary = new(), urgent = new();
    private bool active;
    private int urgentBurst;
    private int observedActive;
    private long admitted, rejected, expired, retries, longHolds, waitTicks, holdTicks;
    public SqliteWriterOptions Options { get; }

    public SqliteWriter(SqliteWriterOptions? options = null)
    {
        Options = options ?? new();
        Options.Validate();
    }

    public static IDisposable Prioritize()
    {
        var previous = Urgent.Value;
        Urgent.Value = true;
        return new ActionLease(() => Urgent.Value = previous);
    }

    public SqliteWriterSnapshot Snapshot()
    {
        lock (sync) return new(ordinary.Count, urgent.Count, active || observedActive > 0, admitted, rejected, expired, retries,
            longHolds, TimeSpan.FromTicks(waitTicks).TotalSeconds, TimeSpan.FromTicks(holdTicks).TotalSeconds);
    }

    internal void Retried() { lock (sync) retries++; }

    public async ValueTask<IDisposable> EnterAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        if (!Options.Enabled || Options.CriticalOnly && !Urgent.Value)
        {
            lock (sync) { admitted++; observedActive++; }
            return new ActionLease(() =>
            {
                lock (sync) { observedActive--; RecordHold(started); }
            });
        }
        LinkedListNode<TaskCompletionSource<IDisposable>> node;
        lock (sync)
        {
            if (!active) { active = true; return Admit(started); }
            var queue = Urgent.Value ? urgent : ordinary;
            if (queue.Count >= (Urgent.Value ? Options.UrgentCapacity : Options.Capacity))
            {
                rejected++;
                throw new SqliteWriteUnavailableException("file saturée");
            }
            node = queue.AddLast(new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        try
        {
            var lease = await node.Value.Task.WaitAsync(Options.WaitTimeout, ct);
            lock (sync) waitTicks += Stopwatch.GetElapsedTime(started).Ticks;
            return lease;
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            lock (sync)
            {
                if (node.List is { } list) list.Remove(node);
                else if (node.Value.Task.IsCompletedSuccessfully) node.Value.Task.GetAwaiter().GetResult().Dispose();
                if (exception is TimeoutException) expired++;
            }
            if (exception is TimeoutException) throw new SqliteWriteUnavailableException("délai d’attente dépassé");
            throw;
        }
    }

    private IDisposable Admit(long started)
    {
        admitted++;
        return new ActionLease(() => Release(started));
    }

    private void Release(long started)
    {
        lock (sync)
        {
            RecordHold(started);
            // Bound priority inversion in both directions: urgent work overtakes ordinary work,
            // but after eight urgent admissions an ordinary waiter makes progress.
            var queue = urgent.Count > 0 && (urgentBurst < 8 || ordinary.Count == 0) ? urgent : ordinary;
            if (queue.First is not { } next) { active = false; urgentBurst = 0; return; }
            queue.RemoveFirst();
            urgentBurst = queue == urgent ? urgentBurst + 1 : 0;
            next.Value.SetResult(Admit(Stopwatch.GetTimestamp()));
        }
    }

    private void RecordHold(long started)
    {
        var elapsed = Stopwatch.GetElapsedTime(started);
        holdTicks += elapsed.Ticks;
        if (elapsed < Options.LongTransaction) return;
        longHolds++;
        Trace.TraceWarning("Écrivain SQLite détenu pendant {0:F0} ms.", elapsed.TotalMilliseconds);
    }

    private sealed class ActionLease(Action release) : IDisposable
    {
        private Action? action = release;
        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }
}

public sealed record SqliteWriterSnapshot(int Pending, int UrgentPending, bool Active, long Admitted,
    long Rejected, long Expired, long Retries, long LongHolds, double WaitSeconds, double HoldSeconds);
