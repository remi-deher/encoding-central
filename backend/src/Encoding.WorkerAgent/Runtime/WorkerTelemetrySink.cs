using System.Collections.Concurrent;
using Encoding.Contracts.Executions;
using Encoding.WorkerAgent.Services;

namespace Encoding.WorkerAgent.Runtime;

/// <summary>Best-effort, bounded telemetry queue. Durable command logs remain in RuntimeCommandCapture.</summary>
public sealed class WorkerTelemetrySink(TimeProvider time) : BackgroundService
{
    private sealed class CommandBuffer(Guid executionId, long epoch, string nodeId, Guid commandId)
    {
        public readonly object Gate = new();
        public readonly Guid ExecutionId = executionId;
        public readonly long Epoch = epoch;
        public readonly string NodeId = nodeId;
        public readonly Guid CommandId = commandId;
        public readonly Dictionary<string, List<string>> Lines = new(StringComparer.Ordinal);
        public int PendingChars;
        public DateTimeOffset LastProgress = DateTimeOffset.MinValue;
    }

    /// <summary>2 000 caractères font au plus 8 Ko en UTF-8 (4 octets par caractère au pire).</summary>
    public const int MaxLineChars = 2_000;
    public const int MaxBatchLines = 64;
    /// <summary>Marge sous les 256 Ko du central pour l'échappement JSON et l'enveloppe.</summary>
    public const int MaxBatchChars = 32_000;

    private readonly ConcurrentDictionary<Guid, CommandBuffer> commands = new();
    private readonly ConcurrentDictionary<Guid, long> sequences = new();
    private Action<object>? enqueue;

    public void Attach(Action<object> writer) => Volatile.Write(ref enqueue, writer);
    private int connected;
    public bool Connected
    {
        get => Volatile.Read(ref connected) == 1;
        set
        {
            if (Interlocked.Exchange(ref connected, value ? 1 : 0) == (value ? 1 : 0) || value) return;
            foreach (var buffer in commands.Values)
                lock (buffer.Gate) buffer.Lines.Clear();
        }
    }

    public void Step(Guid executionId, long epoch, string nodeId, string state)
    {
        if (Connected) Volatile.Read(ref enqueue)?.Invoke(new WorkerStepFrame(WorkerSocketFrameTypes.Step,
            executionId, epoch, nodeId, state, time.GetUtcNow()));
    }

    public void Log(Guid executionId, long epoch, string nodeId, Guid commandId, string stream, string line)
    {
        if (!Connected || string.IsNullOrEmpty(line)) return;
        var buffer = commands.GetOrAdd(commandId, _ => new(executionId, epoch, nodeId, commandId));
        lock (buffer.Gate)
        {
            if (!buffer.Lines.TryGetValue(stream, out var lines)) buffer.Lines[stream] = lines = [];
            // Le central refuse une ligne de plus de 8 Ko et une trame de plus de 256 Ko (UTF-8) : on coupe et on vide avant.
            if (line.Length > MaxLineChars) line = string.Concat(line.AsSpan(0, MaxLineChars), "…");
            lines.Add(line);
            buffer.PendingChars += line.Length;
            if (buffer.Lines.Values.Sum(batch => batch.Count) >= MaxBatchLines || buffer.PendingChars >= MaxBatchChars)
                foreach (var pendingStream in buffer.Lines.Keys.ToArray()) Flush(buffer, pendingStream);
        }
    }

    public void Progress(Guid executionId, long epoch, string nodeId, Guid commandId, double? percent, long? bytes, long elapsedMs)
    {
        if (!Connected) return;
        var buffer = commands.GetOrAdd(commandId, _ => new(executionId, epoch, nodeId, commandId));
        lock (buffer.Gate)
        {
            var now = time.GetUtcNow();
            if (now - buffer.LastProgress < TimeSpan.FromMilliseconds(500)) return;
            buffer.LastProgress = now;
            Volatile.Read(ref enqueue)?.Invoke(new WorkerProgressFrame(WorkerSocketFrameTypes.Progress,
                executionId, epoch, nodeId, commandId, percent, bytes, elapsedMs, now));
        }
    }

    private void Flush(CommandBuffer buffer, string stream)
    {
        if (!buffer.Lines.Remove(stream, out var lines)) return;
        buffer.PendingChars = buffer.Lines.Values.Sum(batch => batch.Sum(line => line.Length));
        if (!Connected || lines.Count == 0) return;
        Volatile.Read(ref enqueue)?.Invoke(new WorkerLogFrame(WorkerSocketFrameTypes.Log, buffer.ExecutionId, buffer.Epoch,
            buffer.NodeId, buffer.CommandId, stream, sequences.AddOrUpdate(buffer.CommandId, 1, static (_, current) => current + 1),
            lines.ToArray(), time.GetUtcNow()));
    }

    public void CompleteCommand(Guid commandId)
    {
        if (!commands.TryRemove(commandId, out var buffer)) return;
        lock (buffer.Gate)
            foreach (var stream in buffer.Lines.Keys.ToArray()) Flush(buffer, stream);
        sequences.TryRemove(commandId, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var buffer in commands.Values)
            {
                lock (buffer.Gate)
                    foreach (var stream in buffer.Lines.Keys.ToArray()) Flush(buffer, stream);
            }
        }
    }
}
