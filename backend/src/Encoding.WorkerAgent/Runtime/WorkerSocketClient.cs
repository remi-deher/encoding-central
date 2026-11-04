using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Encoding.Contracts.Executions;
using Encoding.WorkerAgent.Services;

namespace Encoding.WorkerAgent.Runtime;

/// <summary>Persistent worker → central best-effort telemetry connection.</summary>
public sealed class WorkerSocketClient(WorkerAgentOptions options, WorkerIdentityStore identities, WorkerWakeSignal wake,
    WorkerTelemetrySink telemetry, TimeProvider time, ILogger<WorkerSocketClient> logger) : BackgroundService
{
    private readonly Channel<object> outbound = Channel.CreateBounded<object>(new BoundedChannelOptions(1024)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    private readonly WakeReconnectPolicy reconnect = new(time);
    private int connected;

    public bool Connected => Volatile.Read(ref connected) == 1;
    public void Enqueue(object frame)
    {
        if (Connected) outbound.Writer.TryWrite(frame);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        telemetry.Attach(Enqueue);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var identity = await identities.GetAsync(stoppingToken);
                if (identity.IsEnrolled)
                    foreach (var url in identity.GetCandidateServerUrls(options))
                    {
                        try { await ConnectAsync(identity, url, stoppingToken); }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                        catch (Exception exception) { logger.LogDebug("Socket de télémétrie indisponible ({Type}).", exception.GetType().Name); }
                        finally { SetConnected(false); }
                    }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogDebug("Connexion de télémétrie indisponible ({Type}).", exception.GetType().Name); }
            try { await Task.Delay(reconnect.NextDelay(), time, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task ConnectAsync(WorkerIdentityState identity, string baseUrl, CancellationToken ct)
    {
        var baseUri = new Uri(baseUrl);
        var builder = new UriBuilder(baseUri) { Scheme = baseUri.Scheme == "https" ? "wss" : "ws" };
        builder.Port = baseUri.IsDefaultPort ? -1 : baseUri.Port;
        builder.Path = baseUri.AbsolutePath.TrimEnd('/') + $"/api/v1/runtime/workers/{identity.Id:D}/socket";
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-Node-Token", identity.WorkerToken!);
        await socket.ConnectAsync(builder.Uri, ct);
        SetConnected(false);
        outbound.Writer.TryWrite(new WorkerSocketHello(WorkerSocketFrameTypes.Hello,
            typeof(WorkerSocketClient).Assembly.GetName().Version?.ToString()));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var writer = WriteLoopAsync(socket, linked.Token);
        var reader = ReadLoopAsync(socket, linked.Token);
        var first = await Task.WhenAny(reader, writer);
        try { await first; }
        finally
        {
            linked.Cancel();
            try { await Task.WhenAll(reader, writer); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        }
    }

    private async Task WriteLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        await foreach (var frame in outbound.Reader.ReadAllAsync(ct))
            await SendAsync(socket, frame, ct);
    }

    private async Task ReadLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var bytes = new byte[4096];
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(bytes, 0, result.Count);
            } while (!result.EndOfMessage);
            var envelope = JsonSerializer.Deserialize<WorkerSocketEnvelope>(message.ToArray(), AgentJson.Options);
            if (envelope?.Type == WorkerSocketFrameTypes.Ready) { SetConnected(true); reconnect.MarkConnected(); }
            else if (envelope?.Type == WorkerSocketFrameTypes.Wake) wake.Pulse();
            else if (envelope?.Type == WorkerSocketFrameTypes.Ping)
                Enqueue(new CentralSocketSignal(WorkerSocketFrameTypes.Pong));
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, object frame, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, frame.GetType(), AgentJson.Options);
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, ct);
    }

    private void SetConnected(bool value)
    {
        Interlocked.Exchange(ref connected, value ? 1 : 0);
        telemetry.Connected = value;
        wake.SetConnected(value);
        if (!value) while (outbound.Reader.TryRead(out _)) { }
    }
}
