using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Encoding.Contracts.Executions;

namespace Encoding.Api.Executions;

/// <summary>
/// Télémétrie temps réel en mémoire (ADR 0045) : étapes, dernière progression et 500 dernières lignes de journal par exécution.
/// Singleton. Chaque abonné a un canal borné (les plus anciens événements sont jetés). Une exécution sans activité depuis
/// 10 minutes est oubliée. Rien n'est durable ni autoritaire : la vérité reste dans les POST transactionnels.
/// </summary>
public sealed class ExecutionTelemetryHub : IExecutionTelemetry, IDisposable
{
    public const int MaxLogLines = 500;
    public const int SubscriberCapacity = 256;
    public const int MaxStepsPerExecution = 500;
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SweepPeriod = TimeSpan.FromMinutes(1);

    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly Dictionary<Guid, State> states = [];
    private readonly Dictionary<Guid, (DateTimeOffset At, long Epoch)> ended = [];
    private readonly Dictionary<Guid, List<Channel<ExecutionTelemetryEvent>>> executionSubscribers = [];
    private readonly Dictionary<Guid, List<Channel<ExecutionTelemetryEvent>>> workerSubscribers = [];
    private readonly ITimer sweeper;

    public ExecutionTelemetryHub(TimeProvider? timeProvider = null)
    {
        time = timeProvider ?? TimeProvider.System;
        sweeper = time.CreateTimer(_ => EvictIdle(), null, SweepPeriod, SweepPeriod);
    }

    public void Dispose() => sweeper.Dispose();

    public void Apply(Guid workerId, WorkerStepFrame frame)
    {
        var step = Publish(workerId, frame.ExecutionId, frame.Epoch, state => state.ApplyStep(frame));
        if (step is not null) Emit(workerId, new ExecutionTelemetryEvent(frame.ExecutionId, workerId, Step: step));
    }

    public void Apply(Guid workerId, WorkerProgressFrame frame)
    {
        var progress = new ExecutionTelemetryProgress(frame.NodeId, frame.CommandId, frame.Percent, frame.BytesProcessed, frame.ElapsedMs, frame.At);
        if (Publish(workerId, frame.ExecutionId, frame.Epoch, state => { state.Progress = progress; return progress; }) is null) return;
        Emit(workerId, new ExecutionTelemetryEvent(frame.ExecutionId, workerId, Progress: progress));
    }

    public void Apply(Guid workerId, WorkerLogFrame frame)
    {
        // Le seq du worker numérote la trame ; chaque ligne reçoit un seq propre, croissant et unique par commande.
        var lines = frame.Lines.Select((text, index) => new ExecutionTelemetryLogLine(frame.NodeId, frame.CommandId, frame.Stream,
            frame.Seq * WorkerTelemetryIngest.MaxLinesPerFrame + index, text, frame.At)).ToArray();
        if (lines.Length == 0) return;
        if (Publish(workerId, frame.ExecutionId, frame.Epoch, state => { state.AddLogs(lines); return lines; }) is null) return;
        Emit(workerId, new ExecutionTelemetryEvent(frame.ExecutionId, workerId, Logs: lines));
    }

    public void End(Guid executionId)
    {
        List<Channel<ExecutionTelemetryEvent>> own = [];
        List<Channel<ExecutionTelemetryEvent>> others = [];
        var worker = Guid.Empty;
        lock (gate)
        {
            // Pierre tombale : une trame en retard (cache de baux de quelques secondes) ne doit pas recréer l'exécution.
            if (states.Remove(executionId, out var state))
            {
                worker = state.WorkerId;
                ended[executionId] = (time.GetUtcNow(), state.Epoch);
            }
            if (executionSubscribers.Remove(executionId, out var byExecution)) own = byExecution;
            if (worker != Guid.Empty && workerSubscribers.TryGetValue(worker, out var byWorker)) others = [.. byWorker];
        }
        // Toujours émis, même sans état : un abonné peut attendre une exécution qui n'a rien reçu.
        var endedEvent = new ExecutionTelemetryEvent(executionId, worker, Ended: true);
        foreach (var channel in own) { channel.Writer.TryWrite(endedEvent); channel.Writer.TryComplete(); }
        foreach (var channel in others) channel.Writer.TryWrite(endedEvent);
    }

    public ExecutionTelemetrySnapshot? Snapshot(Guid executionId)
    {
        lock (gate) return states.TryGetValue(executionId, out var state) ? state.ToSnapshot() : null;
    }

    public IReadOnlyList<WorkerRunningExecution> Running(Guid workerId)
    {
        lock (gate)
            return states.Values.Where(state => state.WorkerId == workerId)
                .OrderBy(state => state.StartedAt)
                .Select(state => new WorkerRunningExecution(state.ExecutionId, state.StartedAt, state.CurrentNodeId, state.Progress))
                .ToArray();
    }

    public IAsyncEnumerable<ExecutionTelemetryEvent> SubscribeExecution(Guid executionId, CancellationToken ct) =>
        Subscribe(executionSubscribers, executionId, ct);

    public IAsyncEnumerable<ExecutionTelemetryEvent> SubscribeWorker(Guid workerId, CancellationToken ct) =>
        Subscribe(workerSubscribers, workerId, ct);

    /// <summary>Oublie les exécutions muettes depuis <see cref="IdleTimeout"/> ; chaque abonné reçoit <c>Ended</c>.</summary>
    public int EvictIdle()
    {
        Guid[] expired;
        var limit = time.GetUtcNow() - IdleTimeout;
        lock (gate)
        {
            expired = states.Values.Where(state => state.LastActivity <= limit).Select(state => state.ExecutionId).ToArray();
            foreach (var id in ended.Where(entry => entry.Value.At <= limit).Select(entry => entry.Key).ToArray()) ended.Remove(id);
        }
        foreach (var id in expired) End(id);
        return expired.Length;
    }

    /// <summary>Applique une trame ; null si l'exécution vient de se terminer (trame tardive, ignorée).</summary>
    private T? Publish<T>(Guid workerId, Guid executionId, long epoch, Func<State, T?> mutate) where T : class
    {
        lock (gate)
        {
            // Une nouvelle tentative (relance, reprise) porte une époque plus grande et repart de zéro.
            if (ended.TryGetValue(executionId, out var tombstone))
            {
                if (epoch <= tombstone.Epoch) return null;
                ended.Remove(executionId);
            }
            if (!states.TryGetValue(executionId, out var state) || state.WorkerId != workerId)
            {
                state = new State(executionId, workerId, time.GetUtcNow());
                states[executionId] = state;
            }
            state.LastActivity = time.GetUtcNow();
            state.Epoch = Math.Max(state.Epoch, epoch);
            return mutate(state);
        }
    }

    private void Emit(Guid workerId, ExecutionTelemetryEvent value)
    {
        Channel<ExecutionTelemetryEvent>[] targets;
        lock (gate)
        {
            targets = [.. executionSubscribers.GetValueOrDefault(value.ExecutionId) ?? [], .. workerSubscribers.GetValueOrDefault(workerId) ?? []];
        }
        foreach (var channel in targets) channel.Writer.TryWrite(value);
    }

    private async IAsyncEnumerable<ExecutionTelemetryEvent> Subscribe(
        Dictionary<Guid, List<Channel<ExecutionTelemetryEvent>>> registry, Guid key, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<ExecutionTelemetryEvent>(new BoundedChannelOptions(SubscriberCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        lock (gate)
        {
            if (!registry.TryGetValue(key, out var list)) registry[key] = list = [];
            list.Add(channel);
        }
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(ct)) yield return item;
        }
        finally
        {
            lock (gate)
            {
                if (registry.TryGetValue(key, out var list) && list.Remove(channel) && list.Count == 0) registry.Remove(key);
            }
        }
    }

    private sealed class State(Guid executionId, Guid workerId, DateTimeOffset now)
    {
        private readonly List<ExecutionTelemetryStep> steps = [];
        private readonly Queue<ExecutionTelemetryLogLine> logs = new();

        public Guid ExecutionId { get; } = executionId;
        public Guid WorkerId { get; } = workerId;
        public DateTimeOffset StartedAt { get; } = now;
        public DateTimeOffset LastActivity { get; set; } = now;
        public ExecutionTelemetryProgress? Progress { get; set; }
        public long Epoch { get; set; }
        public string? CurrentNodeId => steps.LastOrDefault(step => step.FinishedAt is null)?.NodeId;

        public ExecutionTelemetryStep? ApplyStep(WorkerStepFrame frame)
        {
            var index = steps.FindIndex(step => step.NodeId == frame.NodeId);
            var finished = frame.State is WorkerStepStates.Completed or WorkerStepStates.Failed;
            if (index < 0)
            {
                if (steps.Count >= MaxStepsPerExecution) return null;
                steps.Add(new ExecutionTelemetryStep(frame.NodeId, frame.State, frame.At, finished ? frame.At : null));
                return steps[^1];
            }
            var current = steps[index];
            steps[index] = current with { State = frame.State, FinishedAt = finished ? frame.At : null };
            return steps[index];
        }

        public void AddLogs(IEnumerable<ExecutionTelemetryLogLine> lines)
        {
            foreach (var line in lines)
            {
                logs.Enqueue(line);
                while (logs.Count > MaxLogLines) logs.Dequeue();
            }
        }

        public ExecutionTelemetrySnapshot ToSnapshot() =>
            new(ExecutionId, WorkerId, StartedAt, CurrentNodeId, [.. steps], Progress, [.. logs]);
    }
}
