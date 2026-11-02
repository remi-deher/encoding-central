using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Encoding.Contracts.Logging;

namespace Encoding.Core.Logging;

/// <summary>
/// Diffuseur asynchrone non-bloquant pour les abonnés temps réel (SSE).
/// Chaque client dispose d'un canal borné qui supprime les plus anciens messages si le client est lent,
/// garantissant que l'application hôte et les workers ne ralentissent jamais l'émission des logs.
/// </summary>
public sealed class SystemLogStreamBroadcaster
{
    private sealed record Subscription(
        long Id,
        Channel<SystemLogEntry> Channel,
        SystemLogFilterQuery? Filter);

    private readonly ConcurrentDictionary<long, Subscription> _subscriptions = new();
    private long _subscriberIdSequence;

    public int ActiveSubscriberCount => _subscriptions.Count;

    public void Publish(SystemLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        foreach (var sub in _subscriptions.Values)
        {
            if (MatchesFilter(entry, sub.Filter))
            {
                sub.Channel.Writer.TryWrite(entry);
            }
        }
    }

    public async IAsyncEnumerable<SystemLogEntry> SubscribeAsync(
        SystemLogFilterQuery? filter,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _subscriberIdSequence);
        var options = new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        };
        var channel = Channel.CreateBounded<SystemLogEntry>(options);
        var subscription = new Subscription(id, channel, filter);

        _subscriptions[id] = subscription;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                SystemLogEntry item;
                try
                {
                    item = await channel.Reader.ReadAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ChannelClosedException)
                {
                    break;
                }

                yield return item;
            }
        }
        finally
        {
            _subscriptions.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }

    private static bool MatchesFilter(SystemLogEntry entry, SystemLogFilterQuery? query)
    {
        if (query is null)
            return true;

        if (query.MinLevel.HasValue && entry.Level < query.MinLevel.Value)
            return false;

        if (!string.IsNullOrWhiteSpace(query.Category) &&
            !entry.Category.StartsWith(query.Category.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (query.NodeId.HasValue && entry.NodeId != query.NodeId.Value)
            return false;

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            var matchMessage = entry.Message.Contains(term, StringComparison.OrdinalIgnoreCase);
            var matchException = entry.Exception != null && entry.Exception.Contains(term, StringComparison.OrdinalIgnoreCase);
            if (!matchMessage && !matchException)
                return false;
        }

        return true;
    }
}
