using System.Data.Common;
using System.Net.WebSockets;
using System.Text.Json;
using Encoding.Contracts.Executions;

namespace Encoding.Api.Executions;

public enum WorkerFrameResult { Applied, Ignored, NotLeased, TooLarge, Invalid }

/// <summary>
/// Valide et applique les trames d'une connexion de worker. Une trame de step/progress/log n'est retenue que si l'exécution
/// est bien louée à ce worker avec cette époque ; la table des baux est relue au plus toutes les <see cref="LeaseRefresh"/>.
/// </summary>
public sealed class WorkerTelemetryIngest(Guid workerId, IExecutionTelemetry hub, IWorkerLeaseLookup leases, TimeProvider? timeProvider = null)
{
    public const int MaxFrameBytes = 256 * 1024;
    public const int MaxLineBytes = 8 * 1024;
    public const int MaxLinesPerFrame = 512;
    public const int MaxNameLength = 256;
    public static readonly TimeSpan LeaseRefresh = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MissRefresh = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private IReadOnlyDictionary<Guid, long> cache = new Dictionary<Guid, long>();
    private long refreshedAt;
    private bool refreshed;

    public async Task<WorkerFrameResult> HandleAsync(string text, CancellationToken ct)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxFrameBytes) return WorkerFrameResult.TooLarge;
        string? type;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String) return WorkerFrameResult.Invalid;
            type = typeElement.GetString();
        }
        catch (JsonException) { return WorkerFrameResult.Invalid; }
        try
        {
            return type switch
            {
                WorkerSocketFrameTypes.Step => await HandleStepAsync(JsonSerializer.Deserialize<WorkerStepFrame>(text, Json), ct),
                WorkerSocketFrameTypes.Progress => await HandleProgressAsync(JsonSerializer.Deserialize<WorkerProgressFrame>(text, Json), ct),
                WorkerSocketFrameTypes.Log => await HandleLogAsync(JsonSerializer.Deserialize<WorkerLogFrame>(text, Json), ct),
                _ => WorkerFrameResult.Ignored,
            };
        }
        catch (JsonException) { return WorkerFrameResult.Invalid; }
    }

    private async Task<WorkerFrameResult> HandleStepAsync(WorkerStepFrame? frame, CancellationToken ct)
    {
        if (frame is null || !ValidName(frame.NodeId) || frame.State is not (WorkerStepStates.Started or WorkerStepStates.Completed or WorkerStepStates.Failed))
            return WorkerFrameResult.Invalid;
        if (!await IsLeasedAsync(frame.ExecutionId, frame.Epoch, ct)) return WorkerFrameResult.NotLeased;
        hub.Apply(workerId, frame);
        return WorkerFrameResult.Applied;
    }

    private async Task<WorkerFrameResult> HandleProgressAsync(WorkerProgressFrame? frame, CancellationToken ct)
    {
        if (frame is null || !ValidName(frame.NodeId) || frame.ElapsedMs < 0
            || frame.Percent is { } percent && (double.IsNaN(percent) || percent < 0 || percent > 100)) return WorkerFrameResult.Invalid;
        if (!await IsLeasedAsync(frame.ExecutionId, frame.Epoch, ct)) return WorkerFrameResult.NotLeased;
        hub.Apply(workerId, frame);
        return WorkerFrameResult.Applied;
    }

    private async Task<WorkerFrameResult> HandleLogAsync(WorkerLogFrame? frame, CancellationToken ct)
    {
        if (frame is null || !ValidName(frame.NodeId) || frame.Stream is not { Length: > 0 and <= 32 } || frame.Lines is null
            || frame.Lines.Count > MaxLinesPerFrame) return WorkerFrameResult.Invalid;
        if (frame.Lines.Any(line => line is null || System.Text.Encoding.UTF8.GetByteCount(line) > MaxLineBytes)) return WorkerFrameResult.TooLarge;
        if (!await IsLeasedAsync(frame.ExecutionId, frame.Epoch, ct)) return WorkerFrameResult.NotLeased;
        hub.Apply(workerId, frame);
        return WorkerFrameResult.Applied;
    }

    private static bool ValidName(string? value) => value is { Length: > 0 and <= MaxNameLength };

    private async Task<bool> IsLeasedAsync(Guid executionId, long epoch, CancellationToken ct)
    {
        if (executionId == Guid.Empty || epoch < 1) return false;
        if (!refreshed || time.GetElapsedTime(refreshedAt) >= LeaseRefresh)
        {
            if (!await RefreshAsync(ct)) return false;
        }
        if (cache.TryGetValue(executionId, out var current) && current == epoch) return true;
        // Raté : bail tout neuf (première trame d'une exécution) ou époque changée. Relecture immédiate, au plus 1 fois par seconde.
        if (time.GetElapsedTime(refreshedAt) < MissRefresh || !await RefreshAsync(ct)) return false;
        return cache.TryGetValue(executionId, out current) && current == epoch;
    }

    private async Task<bool> RefreshAsync(CancellationToken ct)
    {
        try { cache = await leases.ActiveAsync(workerId, ct); }
        catch (DbException) { return false; }
        refreshedAt = time.GetTimestamp();
        refreshed = true;
        return true;
    }
}

/// <summary>
/// WebSocket de télémétrie des workers (ADR 0045), en plus du flux SSE <c>/wake</c> qui reste le repli. Authentifié par
/// <c>X-Node-Token</c> comme les autres routes du groupe runtime. Best-effort : rien ici n'écrit en base.
/// </summary>
public static class WorkerSocketEndpoint
{
    public static void MapWorkerSocketEndpoint(this RouteGroupBuilder group)
    {
        group.MapGet("/socket", async Task<IResult> (HttpContext http, CancellationToken ct) =>
        {
            var worker = Guid.Parse((string)http.Request.RouteValues["workerId"]!);
            var token = http.Request.Headers["X-Node-Token"].ToString();
            var coordinator = http.RequestServices.GetRequiredService<RuntimeExecutionCoordinator>();
            // Le jeton a déjà été vérifié par le filtre du groupe runtime (RuntimeEndpoints) avant d'arriver ici.
            if (!http.WebSockets.IsWebSocketRequest) return Results.BadRequest();
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            var ingest = new WorkerTelemetryIngest(worker, http.RequestServices.GetRequiredService<IExecutionTelemetry>(),
                http.RequestServices.GetRequiredService<IWorkerLeaseLookup>());
            await new WorkerSocketSession(socket, ingest, http.RequestServices.GetRequiredService<RuntimeWakeStreams>(),
                cancellation => coordinator.AuthenticateAsync(worker, token, cancellation)).RunAsync(ct);
            return Results.Empty;
        }).WithName("WorkerTelemetrySocket");
    }
}

/// <summary>Une connexion : un seul écrivain (réveils et pings), une boucle de lecture, fermeture propre à la fin.</summary>
public sealed class WorkerSocketSession(WebSocket socket, WorkerTelemetryIngest ingest, RuntimeWakeStreams wakes,
    Func<CancellationToken, Task<bool>> authenticate)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task RunAsync(CancellationToken requestAborted)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        var ct = linked.Token;
        try
        {
            await SendAsync(WorkerSocketFrameTypes.Ready, ct);
            var reading = ReadAsync(ct);
            var pushing = PushAsync(ct);
            var first = await Task.WhenAny(reading, pushing);
            var status = await first;
            await linked.CancelAsync();
            await Task.WhenAll(reading.ContinueWith(_ => { }, TaskScheduler.Default), pushing.ContinueWith(_ => { }, TaskScheduler.Default));
            await CloseAsync(status);
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException) { }
    }

    private async Task<WebSocketCloseStatus> ReadAsync(CancellationToken ct)
    {
        var buffer = new byte[WorkerTelemetryIngest.MaxFrameBytes];
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var length = 0;
                ValueWebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer.AsMemory(length), ct);
                    if (received.MessageType == WebSocketMessageType.Close) return WebSocketCloseStatus.NormalClosure;
                    length += received.Count;
                    if (!received.EndOfMessage && length >= buffer.Length) return WebSocketCloseStatus.MessageTooBig;
                } while (!received.EndOfMessage);
                if (received.MessageType != WebSocketMessageType.Text) return WebSocketCloseStatus.InvalidMessageType;
                var result = await ingest.HandleAsync(System.Text.Encoding.UTF8.GetString(buffer, 0, length), ct);
                if (result == WorkerFrameResult.Invalid) return WebSocketCloseStatus.InvalidPayloadData;
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException) { }
        return WebSocketCloseStatus.NormalClosure;
    }

    private async Task<WebSocketCloseStatus> PushAsync(CancellationToken ct)
    {
        var cursor = wakes.CreateCursor();
        var checkedAt = wakes.Timestamp();
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var woke = await cursor.WaitAsync(RuntimeWakeStreams.Heartbeat, ct);
                // Le jeton est revérifié au moins toutes les 15 s, comme sur le flux SSE.
                if (wakes.Elapsed(checkedAt) >= RuntimeWakeStreams.Heartbeat)
                {
                    if (!await authenticate(ct)) return WebSocketCloseStatus.PolicyViolation;
                    checkedAt = wakes.Timestamp();
                }
                await SendAsync(woke ? WorkerSocketFrameTypes.Wake : WorkerSocketFrameTypes.Ping, ct);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException) { }
        return WebSocketCloseStatus.NormalClosure;
    }

    private Task SendAsync(string type, CancellationToken ct) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new CentralSocketSignal(type), Json), WebSocketMessageType.Text, true, ct);

    private async Task CloseAsync(WebSocketCloseStatus status)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await socket.CloseOutputAsync(status, null, timeout.Token); }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException) { }
    }
}
