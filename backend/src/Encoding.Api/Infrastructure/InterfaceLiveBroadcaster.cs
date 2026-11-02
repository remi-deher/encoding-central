using System.Collections.Concurrent;

namespace Encoding.Api.Infrastructure;

/// <summary>
/// Signale aux interfaces qu'un sujet a changé. Le flux ne porte pas les données : la page recharge seulement ce sujet.
/// Les annonces rapprochées d'un même sujet n'en forment qu'une.
/// </summary>
public sealed class InterfaceLiveBroadcaster
{
    private long version;
    private int subscriberSequence;
    private readonly object gate = new();
    private readonly List<(long Version, string Topic)> changes = [];
    private readonly ConcurrentDictionary<int, TaskCompletionSource<long>> waiters = new();

    public long CurrentVersion => Volatile.Read(ref version);

    public void Publish(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return;
        long next;
        lock (gate)
        {
            next = ++version;
            changes.Add((next, topic));
            if (changes.Count > 200) changes.RemoveRange(0, changes.Count - 200);
        }
        foreach (var (key, waiter) in waiters)
        {
            if (waiters.TryRemove(key, out var pending)) pending.TrySetResult(next);
        }
    }

    public async Task<LiveSignal> WaitAsync(long knownVersion, TimeSpan heartbeat, CancellationToken ct)
    {
        var ready = Snapshot(knownVersion);
        if (ready.Topics.Count > 0) return ready;

        var id = Interlocked.Increment(ref subscriberSequence);
        var waiter = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiters[id] = waiter;
        ready = Snapshot(knownVersion);
        if (ready.Topics.Count > 0 && waiters.TryRemove(id, out _)) return ready;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(heartbeat);
            await waiter.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Snapshot(knownVersion);
        }
        finally
        {
            waiters.TryRemove(id, out _);
        }
        return Snapshot(knownVersion);
    }

    private LiveSignal Snapshot(long knownVersion)
    {
        lock (gate)
        {
            var topics = changes.Where(change => change.Version > knownVersion).Select(change => change.Topic).Distinct().ToArray();
            return new LiveSignal(version, topics);
        }
    }
}

public readonly record struct LiveSignal(long Version, IReadOnlyList<string> Topics);
