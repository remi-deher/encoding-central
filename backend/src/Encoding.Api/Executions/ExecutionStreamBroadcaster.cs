using System.Collections.Concurrent;
using System.Text.Json;

namespace Encoding.Api.Executions;

/// <summary>
/// Diffuseur léger en mémoire pour notifier les abonnés SSE (<c>text/event-stream</c>) dès qu’une exécution évolue.
/// </summary>
public sealed class ExecutionStreamBroadcaster
{
    public static readonly JsonSerializerOptions SseJsonOptions = new(JsonSerializerDefaults.Web);
    private long _version;
    private int _subscriberSequence;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<long>> _waiters = new();

    public long CurrentVersion => Volatile.Read(ref _version);

    public void Publish(Guid? executionId = null)
    {
        var next = Interlocked.Increment(ref _version);
        foreach (var (key, tcs) in _waiters)
        {
            if (_waiters.TryRemove(key, out _))
            {
                tcs.TrySetResult(next);
            }
        }
    }

    public async Task<long> WaitForChangeAsync(long knownVersion, TimeSpan heartbeat, CancellationToken ct)
    {
        var current = Volatile.Read(ref _version);
        if (current > knownVersion)
            return current;

        var id = Interlocked.Increment(ref _subscriberSequence);
        var tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[id] = tcs;

        // Re-check after registration to avoid race condition.
        current = Volatile.Read(ref _version);
        if (current > knownVersion && _waiters.TryRemove(id, out _))
        {
            return current;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(heartbeat);
            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Volatile.Read(ref _version);
        }
        finally
        {
            _waiters.TryRemove(id, out _);
        }
    }

    public static void PrepareResponse(HttpResponse response)
    {
        response.Headers.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache, no-store";
        response.Headers["X-Accel-Buffering"] = "no";
    }

    public static async Task WriteEventAsync<T>(
        HttpResponse response,
        string eventType,
        T payload,
        JsonSerializerOptions options,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, options);
        await response.WriteAsync($"event: {eventType}\ndata: {json}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}
