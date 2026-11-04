using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Contracts.Executions;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class ExecutionTelemetryEndpointTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly Guid Worker = Guid.NewGuid();

    private sealed class Recorder
    {
        private readonly object gate = new();
        private readonly List<(string Type, string Json)> items = [];

        public Task Write(string type, object? payload, CancellationToken ct)
        {
            lock (gate) items.Add((type, JsonSerializer.Serialize(payload, Web)));
            return Task.CompletedTask;
        }

        public List<(string Type, string Json)> Items { get { lock (gate) return [.. items]; } }

        public async Task<List<(string Type, string Json)>> UntilAsync(Func<List<(string Type, string Json)>, bool> done)
        {
            for (var i = 0; i < 400; i++)
            {
                var snapshot = Items;
                if (done(snapshot)) return snapshot;
                await Task.Delay(10);
            }
            throw new TimeoutException(string.Join(",", Items.Select(item => item.Type)));
        }
    }

    private static WorkerStepFrame Step(Guid execution, string node, string state = WorkerStepStates.Started) =>
        new(WorkerSocketFrameTypes.Step, execution, 1, node, state, DateTimeOffset.UnixEpoch);

    private static WorkerProgressFrame Progress(Guid execution) =>
        new(WorkerSocketFrameTypes.Progress, execution, 1, "node", Guid.Empty, 42, 10, 5, DateTimeOffset.UnixEpoch);

    private static WorkerLogFrame Log(Guid execution, long seq) =>
        new(WorkerSocketFrameTypes.Log, execution, 1, "node", Guid.Empty, "stderr", seq, ["a", "b"], DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task ExecutionStreamSendsNullSnapshotThenEventsThenEndAndCloses()
    {
        using var hub = new ExecutionTelemetryHub();
        var execution = Guid.NewGuid();
        var recorder = new Recorder();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = TelemetryStreams.ExecutionAsync(hub, execution, TimeSpan.FromMinutes(1), recorder.Write, cts.Token);

        await recorder.UntilAsync(items => items.Count >= 1);
        hub.Apply(Worker, Step(execution, "encode"));
        hub.Apply(Worker, Progress(execution));
        hub.Apply(Worker, Log(execution, 1));
        hub.End(execution);
        await run;

        var items = recorder.Items;
        Assert.Equal(new[] { "snapshot", "step", "progress", "log", "end" }, items.Select(item => item.Type).ToArray());
        Assert.Equal("null", items[0].Json);
        Assert.Contains("\"nodeId\":\"encode\"", items[1].Json);
        Assert.Contains("\"percent\":42", items[2].Json);
        Assert.StartsWith("[", items[3].Json);
        Assert.Contains("\"text\":\"a\"", items[3].Json);
        Assert.Equal("{}", items[4].Json);
    }

    [Fact]
    public async Task ExecutionStreamStartsWithTheCurrentSnapshot()
    {
        using var hub = new ExecutionTelemetryHub();
        var execution = Guid.NewGuid();
        hub.Apply(Worker, Step(execution, "probe"));
        hub.Apply(Worker, Log(execution, 1));
        var recorder = new Recorder();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = TelemetryStreams.ExecutionAsync(hub, execution, TimeSpan.FromMinutes(1), recorder.Write, cts.Token);
        var items = await recorder.UntilAsync(list => list.Count >= 1);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal("snapshot", items[0].Type);
        using var document = JsonDocument.Parse(items[0].Json);
        Assert.Equal("probe", document.RootElement.GetProperty("currentNodeId").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("logs").GetArrayLength());
    }

    [Fact]
    public async Task StreamsSendPingsWhenQuiet()
    {
        using var hub = new ExecutionTelemetryHub();
        var recorder = new Recorder();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var execution = TelemetryStreams.ExecutionAsync(hub, Guid.NewGuid(), TimeSpan.FromMilliseconds(30), recorder.Write, cts.Token);
        var worker = TelemetryStreams.WorkerAsync(hub, Worker, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(10), recorder.Write, cts.Token);
        var items = await recorder.UntilAsync(list => list.Count(item => item.Type == "ping") >= 4);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
        Assert.All(items.Where(item => item.Type == "ping"), item => Assert.Equal("{}", item.Json));
    }

    [Fact]
    public async Task WorkerStreamCarriesExecutionIdAndResendsTheRunningSet()
    {
        using var hub = new ExecutionTelemetryHub();
        var execution = Guid.NewGuid();
        var recorder = new Recorder();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = TelemetryStreams.WorkerAsync(hub, Worker, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(20), recorder.Write, cts.Token);
        var first = await recorder.UntilAsync(list => list.Count >= 1);
        Assert.Equal("snapshot", first[0].Type);
        Assert.Equal("[]", first[0].Json);

        hub.Apply(Worker, Step(execution, "encode"));
        hub.Apply(Worker, Progress(execution));
        hub.Apply(Worker, Log(execution, 1));
        var items = await recorder.UntilAsync(list => list.Count(item => item.Type == "snapshot") >= 2);

        var id = execution.ToString();
        foreach (var type in new[] { "step", "progress", "log" })
            Assert.Contains(id, items.Single(item => item.Type == type).Json);
        using (var document = JsonDocument.Parse(items.Last(item => item.Type == "snapshot").Json))
        {
            Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
            Assert.Equal(id, document.RootElement[0].GetProperty("executionId").GetString());
            Assert.Equal("encode", document.RootElement[0].GetProperty("currentNodeId").GetString());
        }

        hub.End(execution);
        items = await recorder.UntilAsync(list => list.Count(item => item.Type == "snapshot") >= 3);
        Assert.Contains(items, item => item.Type == "end" && item.Json.Contains(id));
        Assert.Equal("[]", items.Last(item => item.Type == "snapshot").Json);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task WorkerSnapshotsAreThrottledDuringABurstOfSteps()
    {
        using var hub = new ExecutionTelemetryHub();
        var recorder = new Recorder();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = TelemetryStreams.WorkerAsync(hub, Worker, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(400), recorder.Write, cts.Token);
        await recorder.UntilAsync(list => list.Count >= 1);
        for (var i = 0; i < 20; i++) hub.Apply(Worker, Step(Guid.NewGuid(), "n" + i));

        var items = await recorder.UntilAsync(list => list.Count(item => item.Type == "step") >= 20);
        Assert.True(items.Count(item => item.Type == "snapshot") <= 2, "au plus un snapshot de plus pendant la salve");
        items = await recorder.UntilAsync(list => list.Last(item => item.Type == "snapshot").Json.Split("executionId").Length == 21);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public void RoutesAreMappedWithTheSseHeadersOfTheOtherStreams()
    {
        var path = Path.Combine(FindApiDirectory(), "Executions", "ExecutionTelemetryEndpoints.cs");
        var text = File.ReadAllText(path);
        Assert.Contains("\"/api/v1/executions/{id:guid}/telemetry\"", text);
        Assert.Contains("\"/api/v1/workers/{id:guid}/telemetry\"", text);
        Assert.Equal(2, text.Split("ExecutionStreamBroadcaster.PrepareResponse(http.Response)").Length - 1);
        Assert.Contains("app.MapExecutionTelemetryEndpoints();", File.ReadAllText(Path.Combine(FindApiDirectory(), "Program.cs")));
    }

    private static string FindApiDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            foreach (var candidate in new[] { Path.Combine(directory.FullName, "backend", "src", "Encoding.Api"), Path.Combine(directory.FullName, "src", "Encoding.Api") })
                if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("Encoding.Api");
    }
}
