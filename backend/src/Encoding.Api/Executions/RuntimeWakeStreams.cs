using Encoding.Api.Infrastructure;

namespace Encoding.Api.Executions;

/// <summary>Une connexion de réveil par worker ; l'autorisation est revérifiée au moins toutes les 15 secondes.</summary>
public sealed class RuntimeWakeStreams(ExecutionStreamBroadcaster executions, InterfaceLiveBroadcaster live,
    TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<Guid, CancellationTokenSource> active = [];

    /// <summary>Curseur sur les mêmes signaux que le flux SSE ; réutilisé par le WebSocket des workers.</summary>
    public WakeCursor CreateCursor() => new(executions, live);

    public long Timestamp() => time.GetTimestamp();

    public TimeSpan Elapsed(long since) => time.GetElapsedTime(since);

    public async Task RunAsync(Guid worker, Func<CancellationToken, Task<bool>> authenticate,
        Func<string, CancellationToken, Task> write, CancellationToken requestAborted)
    {
        var connection = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        lock (gate)
        {
            if (active.TryGetValue(worker, out var previous)) previous.Cancel();
            active[worker] = connection;
        }
        var ct = connection.Token;
        try
        {
            var cursor = CreateCursor();
            var checkedAt = time.GetTimestamp();
            await write("ready", ct);
            while (!ct.IsCancellationRequested)
            {
                var remaining = Heartbeat - time.GetElapsedTime(checkedAt);
                var woke = await cursor.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, ct);
                // Des réveils fréquents ne doivent pas repousser indéfiniment la revérification.
                if (time.GetElapsedTime(checkedAt) >= Heartbeat)
                {
                    if (!await authenticate(ct)) return;
                    checkedAt = time.GetTimestamp();
                }
                await write(woke ? "wake" : "ping", ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            lock (gate)
            {
                if (active.TryGetValue(worker, out var current) && ReferenceEquals(current, connection)) active.Remove(worker);
                connection.Dispose();
            }
        }
    }
}
