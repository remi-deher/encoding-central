using System.Diagnostics;
using System.Text.Json;
using Encoding.Contracts.Executions;

namespace Encoding.Api.Executions;

/// <summary>
/// Flux SSE de télémétrie pour l'interface (ADR 0045), alimentés par <see cref="IExecutionTelemetry"/>.
/// Lecture seule, derrière l'authentification habituelle de l'API. Les charges sont en camelCase.
/// </summary>
public static class ExecutionTelemetryEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapExecutionTelemetryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/executions/{id:guid}/telemetry", async (Guid id, HttpContext http, IExecutionTelemetry telemetry, CancellationToken ct) =>
        {
            ExecutionStreamBroadcaster.PrepareResponse(http.Response);
            await TelemetryStreams.RunAsync(token => TelemetryStreams.ExecutionAsync(telemetry, id, TelemetryStreams.Heartbeat, Writer(http.Response), token), ct);
        }).WithTags("Executions").WithName("StreamExecutionTelemetry");

        app.MapGet("/api/v1/workers/{id:guid}/telemetry", async (Guid id, HttpContext http, IExecutionTelemetry telemetry, CancellationToken ct) =>
        {
            ExecutionStreamBroadcaster.PrepareResponse(http.Response);
            await TelemetryStreams.RunAsync(token => TelemetryStreams.WorkerAsync(telemetry, id, TelemetryStreams.Heartbeat,
                TelemetryStreams.SnapshotInterval, Writer(http.Response), token), ct);
        }).WithTags("Workers").WithName("StreamWorkerTelemetry");
    }

    private static TelemetryStreams.Write Writer(HttpResponse response) =>
        (type, payload, ct) => ExecutionStreamBroadcaster.WriteEventAsync<object?>(response, type, payload, Json, ct);
}

/// <summary>Boucles des deux flux, séparées de HTTP pour être testables avec un écrivain factice.</summary>
public static class TelemetryStreams
{
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan SnapshotInterval = TimeSpan.FromMilliseconds(500);

    public delegate Task Write(string type, object? payload, CancellationToken ct);

    private sealed record Empty;
    private sealed record WorkerStep(Guid ExecutionId, string NodeId, string State, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);
    private sealed record WorkerProgress(Guid ExecutionId, string NodeId, Guid CommandId, double? Percent, long? BytesProcessed, long ElapsedMs, DateTimeOffset At);
    private sealed record WorkerLogs(Guid ExecutionId, IReadOnlyList<ExecutionTelemetryLogLine> Logs);
    private sealed record WorkerEnd(Guid ExecutionId);

    /// <summary>Une fin de connexion côté client n'est pas une erreur.</summary>
    public static async Task RunAsync(Func<CancellationToken, Task> body, CancellationToken ct)
    {
        try { await body(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>snapshot (ou null), puis step / progress / log (tableau), puis end {} ; ping toutes les 15 s.</summary>
    public static async Task ExecutionAsync(IExecutionTelemetry telemetry, Guid executionId, TimeSpan heartbeat, Write write, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var events = telemetry.SubscribeExecution(executionId, stop.Token).GetAsyncEnumerator(stop.Token);
        // Le premier MoveNext inscrit l'abonné de façon synchrone : rien n'est perdu entre l'instantané et le flux.
        var next = events.MoveNextAsync().AsTask();
        try
        {
            await write("snapshot", telemetry.Snapshot(executionId), ct);
            while (true)
            {
                if (!await WaitAsync(next, heartbeat, ct)) { await write("ping", new Empty(), ct); continue; }
                if (!await next) { await write("end", new Empty(), ct); return; }
                var item = events.Current;
                next = events.MoveNextAsync().AsTask();
                if (item.Ended) { await write("end", new Empty(), ct); return; }
                if (item.Step is not null) await write("step", item.Step, ct);
                else if (item.Progress is not null) await write("progress", item.Progress, ct);
                else if (item.Logs is not null) await write("log", item.Logs, ct);
            }
        }
        finally { await CloseAsync(stop, next, events); }
    }

    /// <summary>
    /// snapshot = tableau des exécutions en cours, renvoyé quand l'ensemble ou l'étape courante change (au plus toutes les
    /// <paramref name="snapshotInterval"/>), puis step / progress / log / end portant <c>executionId</c> ; ping toutes les 15 s.
    /// </summary>
    public static async Task WorkerAsync(IExecutionTelemetry telemetry, Guid workerId, TimeSpan heartbeat, TimeSpan snapshotInterval,
        Write write, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var events = telemetry.SubscribeWorker(workerId, stop.Token).GetAsyncEnumerator(stop.Token);
        var next = events.MoveNextAsync().AsTask();
        try
        {
            var running = telemetry.Running(workerId);
            var sentKey = Key(running);
            await write("snapshot", running, ct);
            var lastWrite = Stopwatch.GetTimestamp();
            var lastSnapshot = lastWrite;
            var dirty = false;
            while (true)
            {
                var wait = heartbeat - Stopwatch.GetElapsedTime(lastWrite);
                if (dirty) wait = Min(wait, snapshotInterval - Stopwatch.GetElapsedTime(lastSnapshot));
                if (await WaitAsync(next, wait > TimeSpan.Zero ? wait : TimeSpan.Zero, ct))
                {
                    if (!await next) return;
                    var item = events.Current;
                    next = events.MoveNextAsync().AsTask();
                    dirty |= await Forward(item, write, ct);
                    lastWrite = Stopwatch.GetTimestamp();
                }
                if (dirty && Stopwatch.GetElapsedTime(lastSnapshot) >= snapshotInterval)
                {
                    dirty = false;
                    lastSnapshot = Stopwatch.GetTimestamp();
                    running = telemetry.Running(workerId);
                    var key = Key(running);
                    if (key != sentKey)
                    {
                        sentKey = key;
                        await write("snapshot", running, ct);
                        lastWrite = Stopwatch.GetTimestamp();
                    }
                }
                if (Stopwatch.GetElapsedTime(lastWrite) >= heartbeat)
                {
                    await write("ping", new Empty(), ct);
                    lastWrite = Stopwatch.GetTimestamp();
                    dirty = true; // filet de sécurité : un événement jeté par le canal borné ne doit pas laisser l'ensemble faux.
                }
            }
        }
        finally { await CloseAsync(stop, next, events); }
    }

    /// <summary>
    /// Un itérateur asynchrone ne peut pas être libéré pendant qu'une lecture est en attente : on annule d'abord la lecture,
    /// on attend qu'elle se termine, puis on libère (ce qui désinscrit l'abonné).
    /// </summary>
    private static async Task CloseAsync(CancellationTokenSource stop, Task<bool> pending, IAsyncEnumerator<ExecutionTelemetryEvent> events)
    {
        await stop.CancelAsync();
        try { await pending; }
        catch (OperationCanceledException) { }
        await events.DisposeAsync();
    }

    /// <summary>Retourne vrai si l'événement peut avoir changé l'ensemble des exécutions en cours.</summary>
    private static async Task<bool> Forward(ExecutionTelemetryEvent item, Write write, CancellationToken ct)
    {
        var id = item.ExecutionId;
        if (item.Ended) { await write("end", new WorkerEnd(id), ct); return true; }
        if (item.Step is { } step)
        {
            await write("step", new WorkerStep(id, step.NodeId, step.State, step.StartedAt, step.FinishedAt), ct);
            return true;
        }
        if (item.Progress is { } p) await write("progress", new WorkerProgress(id, p.NodeId, p.CommandId, p.Percent, p.BytesProcessed, p.ElapsedMs, p.At), ct);
        else if (item.Logs is { } logs) await write("log", new WorkerLogs(id, logs), ct);
        return false;
    }

    private static string Key(IReadOnlyList<WorkerRunningExecution> running) =>
        string.Join('|', running.Select(entry => $"{entry.ExecutionId:N}:{entry.CurrentNodeId}"));

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>Vrai si la tâche est terminée avant le délai ; faux sinon. Lève si <paramref name="ct"/> est annulé.</summary>
    private static async Task<bool> WaitAsync(Task task, TimeSpan timeout, CancellationToken ct)
    {
        if (task.IsCompleted) return true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(timeout, cts.Token);
        var first = await Task.WhenAny(task, delay);
        await cts.CancelAsync();
        ct.ThrowIfCancellationRequested();
        return ReferenceEquals(first, task);
    }
}
