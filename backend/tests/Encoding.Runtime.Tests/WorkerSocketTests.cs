using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Contracts.Executions;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class WorkerSocketTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly Guid Worker = Guid.NewGuid();

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public override long GetTimestamp() => now.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class FakeLeases(Dictionary<Guid, long> leases) : IWorkerLeaseLookup
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyDictionary<Guid, long>> ActiveAsync(Guid workerId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyDictionary<Guid, long>>(new Dictionary<Guid, long>(leases));
        }
    }

    private static WorkerLogFrame Log(Guid execution, long seq, string line = "ligne", long epoch = 1) =>
        new(WorkerSocketFrameTypes.Log, execution, epoch, "node", Guid.Empty, "stderr", seq, [line], DateTimeOffset.UnixEpoch);

    private static WorkerStepFrame Step(Guid execution, long epoch, string state = WorkerStepStates.Started) =>
        new(WorkerSocketFrameTypes.Step, execution, epoch, "node", state, DateTimeOffset.UnixEpoch);

    [Fact]
    public void RingBufferKeepsTheLast500Lines()
    {
        using var hub = new ExecutionTelemetryHub(new ManualTime());
        var execution = Guid.NewGuid();
        for (var i = 1; i <= 600; i++) hub.Apply(Worker, Log(execution, i, "l" + i));
        var logs = hub.Snapshot(execution)!.Logs;
        Assert.Equal(500, logs.Count);
        Assert.Equal(101 * WorkerTelemetryIngest.MaxLinesPerFrame, logs[0].Seq);
        Assert.Equal("l600", logs[^1].Text);
    }

    [Fact]
    public async Task SlowSubscriberDropsOldestEventsAndStillSeesEnd()
    {
        using var hub = new ExecutionTelemetryHub(new ManualTime());
        var execution = Guid.NewGuid();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var events = hub.SubscribeExecution(execution, cts.Token).GetAsyncEnumerator(cts.Token);
        var first = events.MoveNextAsync().AsTask();
        hub.Apply(Worker, Log(execution, 1));
        Assert.True(await first);
        for (var i = 2; i <= 301; i++) hub.Apply(Worker, Log(execution, i));
        hub.End(execution);

        var received = new List<ExecutionTelemetryEvent>();
        while (await events.MoveNextAsync()) received.Add(events.Current);
        Assert.Equal(ExecutionTelemetryHub.SubscriberCapacity, received.Count);
        Assert.True(received[^1].Ended);
        Assert.Equal(47 * WorkerTelemetryIngest.MaxLinesPerFrame, received[0].Logs![0].Seq);
        Assert.Null(hub.Snapshot(execution));
    }

    [Fact]
    public async Task EndNotifiesWorkerSubscribersAndForgetsTheExecution()
    {
        using var hub = new ExecutionTelemetryHub(new ManualTime());
        var execution = Guid.NewGuid();
        hub.Apply(Worker, Step(execution, 1));
        Assert.Single(hub.Running(Worker));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var events = hub.SubscribeWorker(Worker, cts.Token).GetAsyncEnumerator(cts.Token);
        var next = events.MoveNextAsync().AsTask();
        hub.End(execution);
        Assert.True(await next);
        Assert.True(events.Current.Ended);
        Assert.Equal(execution, events.Current.ExecutionId);
        Assert.Empty(hub.Running(Worker));
    }

    [Fact]
    public void EachLineOfABatchGetsItsOwnIncreasingSeq()
    {
        using var hub = new ExecutionTelemetryHub(new ManualTime());
        var execution = Guid.NewGuid();
        hub.Apply(Worker, new WorkerLogFrame(WorkerSocketFrameTypes.Log, execution, 1, "node", Guid.Empty, "stderr", 1, ["a", "b", "c"], DateTimeOffset.UnixEpoch));
        hub.Apply(Worker, Log(execution, 2));
        var seqs = hub.Snapshot(execution)!.Logs.Select(line => line.Seq).ToArray();
        Assert.Equal(seqs.Order().Distinct(), seqs);
        Assert.Equal(4, seqs.Length);
    }

    [Fact]
    public void LateFramesAfterEndAreIgnoredButANewEpochStartsAgain()
    {
        using var hub = new ExecutionTelemetryHub(new ManualTime());
        var execution = Guid.NewGuid();
        hub.Apply(Worker, Step(execution, 2));
        hub.End(execution);
        hub.Apply(Worker, Log(execution, 5, epoch: 2));
        hub.Apply(Worker, Step(execution, 2, WorkerStepStates.Completed));
        Assert.Null(hub.Snapshot(execution));
        Assert.Empty(hub.Running(Worker));
        hub.Apply(Worker, Step(execution, 3));
        Assert.NotNull(hub.Snapshot(execution));
    }

    [Fact]
    public void IdleExecutionsAreEvictedAfterTenMinutesWithoutActivity()
    {
        var time = new ManualTime();
        using var hub = new ExecutionTelemetryHub(time);
        var quiet = Guid.NewGuid();
        var busy = Guid.NewGuid();
        hub.Apply(Worker, Log(quiet, 1));
        hub.Apply(Worker, Log(busy, 1));
        time.Advance(TimeSpan.FromMinutes(9));
        hub.Apply(Worker, Log(busy, 2));
        Assert.Equal(0, hub.EvictIdle());
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, hub.EvictIdle());
        Assert.Null(hub.Snapshot(quiet));
        Assert.NotNull(hub.Snapshot(busy));
    }

    [Fact]
    public async Task OnlyTheLeasedEpochIsAccepted()
    {
        using var hub = new ExecutionTelemetryHub(new ManualTime());
        var execution = Guid.NewGuid();
        var ingest = new WorkerTelemetryIngest(Worker, hub, new FakeLeases(new() { [execution] = 3 }), new ManualTime());

        Assert.Equal(WorkerFrameResult.NotLeased, await Handle(ingest, Step(execution, 2)));
        Assert.Equal(WorkerFrameResult.NotLeased, await Handle(ingest, Log(Guid.NewGuid(), 1, epoch: 3)));
        Assert.Null(hub.Snapshot(execution));
        Assert.Equal(WorkerFrameResult.Applied, await Handle(ingest, Step(execution, 3)));
        Assert.Equal(WorkerFrameResult.Applied, await Handle(ingest, Log(execution, 1, epoch: 3)));
        Assert.Single(hub.Snapshot(execution)!.Steps);
    }

    [Fact]
    public async Task LeaseTableIsNotReadOnEveryFrame()
    {
        var time = new ManualTime();
        using var hub = new ExecutionTelemetryHub(time);
        var execution = Guid.NewGuid();
        var leases = new FakeLeases(new() { [execution] = 1 });
        var ingest = new WorkerTelemetryIngest(Worker, hub, leases, time);
        for (var i = 1; i <= 50; i++) await Handle(ingest, Log(execution, i));
        Assert.Equal(1, leases.Calls);
        time.Advance(WorkerTelemetryIngest.LeaseRefresh + TimeSpan.FromSeconds(1));
        await Handle(ingest, Log(execution, 51));
        Assert.Equal(2, leases.Calls);
    }

    [Fact]
    public async Task UnknownExecutionRefreshesTheLeasesAtMostOncePerSecond()
    {
        var time = new ManualTime();
        using var hub = new ExecutionTelemetryHub(time);
        var fresh = Guid.NewGuid();
        var table = new Dictionary<Guid, long>();
        var leases = new FakeLeases(table);
        var ingest = new WorkerTelemetryIngest(Worker, hub, leases, time);

        Assert.Equal(WorkerFrameResult.NotLeased, await Handle(ingest, Log(fresh, 1)));
        Assert.Equal(1, leases.Calls);
        table[fresh] = 1; // le bail vient d'être pris : on n'attend pas les 3 s
        Assert.Equal(WorkerFrameResult.NotLeased, await Handle(ingest, Log(fresh, 2)));
        Assert.Equal(1, leases.Calls); // limité à 1 relecture par seconde
        time.Advance(WorkerTelemetryIngest.MissRefresh + TimeSpan.FromMilliseconds(1));
        Assert.Equal(WorkerFrameResult.Applied, await Handle(ingest, Log(fresh, 3)));
        Assert.Equal(2, leases.Calls);
    }

    [Fact]
    public async Task OversizedFramesAndLinesAreRejected()
    {
        using var hub = new ExecutionTelemetryHub(new ManualTime());
        var execution = Guid.NewGuid();
        var ingest = new WorkerTelemetryIngest(Worker, hub, new FakeLeases(new() { [execution] = 1 }), new ManualTime());

        var longLine = new string('x', WorkerTelemetryIngest.MaxLineBytes + 1);
        Assert.Equal(WorkerFrameResult.TooLarge, await Handle(ingest, Log(execution, 1, longLine)));
        Assert.Equal(WorkerFrameResult.Applied, await Handle(ingest, Log(execution, 2, new string('x', WorkerTelemetryIngest.MaxLineBytes))));
        Assert.Equal(WorkerFrameResult.TooLarge, await ingest.HandleAsync(new string(' ', WorkerTelemetryIngest.MaxFrameBytes + 1), default));
        Assert.Equal(WorkerFrameResult.Invalid, await ingest.HandleAsync("{not json", default));
        Assert.Equal(WorkerFrameResult.Invalid, await ingest.HandleAsync("[]", default));
        Assert.Equal(WorkerFrameResult.Ignored, await ingest.HandleAsync("{\"type\":\"hello\",\"agentVersion\":\"1\"}", default));
        Assert.Single(hub.Snapshot(execution)!.Logs);
    }

    [Fact]
    public void SocketRouteSitsBehindTheNodeTokenFilterOfTheRuntimeGroup()
    {
        // L'authentification (X-Node-Token) est le filtre du groupe runtime : la route doit y être déclarée, après le filtre.
        var path = FindSource("Executions", "RuntimeEndpoints.cs");
        var text = File.ReadAllText(path);
        var filter = text.IndexOf("group.AddEndpointFilter", StringComparison.Ordinal);
        var socket = text.IndexOf("group.MapWorkerSocketEndpoint();", StringComparison.Ordinal);
        Assert.True(filter >= 0 && socket > filter, "MapWorkerSocketEndpoint doit être appelé sur le groupe runtime authentifié.");
        Assert.Contains("AuthenticateAsync", text[filter..socket]);
    }

    private static Task<WorkerFrameResult> Handle<T>(WorkerTelemetryIngest ingest, T frame) =>
        ingest.HandleAsync(JsonSerializer.Serialize(frame, Web), default);

    private static string FindSource(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var relative = Path.Combine(parts);
            var candidate = Path.Combine(directory.FullName, "backend", "src", "Encoding.Api", relative);
            if (File.Exists(candidate)) return candidate;
            candidate = Path.Combine(directory.FullName, "src", "Encoding.Api", relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }
}
