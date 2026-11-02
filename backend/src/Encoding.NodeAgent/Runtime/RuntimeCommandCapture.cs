using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using Encoding.Contracts.Executions;

namespace Encoding.NodeAgent.Runtime;

public sealed class RuntimeCommandCapture : IDisposable
{
    private sealed record ActiveStep(RuntimeCommandCapture Capture, string NodeId, Guid InvocationId);
    private static readonly AsyncLocal<RuntimeCommandCapture?> Current = new();
    private static readonly AsyncLocal<ActiveStep?> CurrentStep = new();
    private static readonly ConcurrentDictionary<Guid, RuntimeCommandCapture> ActiveExecutions = new();
    private static readonly Regex SecretAssignment = new(
        "(?i)(password|passwd|secret|token|api[_-]?key|authorization)(\\s*[:=]\\s*)(\\\"[^\\\"]*\\\"|'[^']*'|[^\\s,;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UrlCredential = new(
        "(?i)(https?://[^:/\\s]+:)[^@/\\s]+@", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AuthorizationHeader = new(
        "(?i)(authorization\\s*[:=]\\s*)[^\\r\\n]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveArgument = new(
        "(?i)(password|passwd|secret|token|api[_-]?key|authorization)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SafeArgument = new("^[a-zA-Z0-9_./:=,+@%-]+$", RegexOptions.Compiled);
    private readonly object sync = new();
    private readonly List<RuntimeStepCommand> records = [];
    private readonly HashSet<(string NodeId, Guid InvocationId)> completedSteps = [];
    private readonly RuntimeCommandCapture? previous;
    private readonly Guid? executionId;
    private bool disposed;

    private RuntimeCommandCapture(Guid? executionId)
    {
        this.executionId = executionId;
        previous = Current.Value;
        Current.Value = this;
        if (executionId is { } id) ActiveExecutions[id] = this;
    }

    public static RuntimeCommandCapture Begin(Guid? executionId = null) => new(executionId);

    public IDisposable BeginStep(string nodeId, Guid invocationId)
    {
        var previousStep = CurrentStep.Value;
        CurrentStep.Value = new(this, nodeId, invocationId);
        return new Scope(() => CurrentStep.Value = previousStep);
    }

    public IReadOnlyList<RuntimeCommandLog> ForStep(string nodeId, Guid invocationId)
    {
        lock (sync)
            return records.Where(item => item.NodeId == nodeId && item.InvocationId == invocationId)
                .Select(item => item.Command).ToArray();
    }

    public IReadOnlyList<RuntimeStepCommand> Pending()
    {
        lock (sync) return records.ToArray();
    }

    public IReadOnlyList<RuntimeStepCommand> ForHeartbeat()
    {
        lock (sync)
        {
            var result = new List<RuntimeStepCommand>();
            var bytes = 0;
            foreach (var record in records.AsEnumerable().Reverse().Take(64))
            {
                var size = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(record).Length;
                if (bytes + size > 450000) break;
                bytes += size;
                result.Add(record);
            }
            return result;
        }
    }

    public void MarkPersisted(string nodeId, Guid invocationId)
    {
        lock (sync)
        {
            completedSteps.Add((nodeId, invocationId));
            records.RemoveAll(item => item.NodeId == nodeId && item.InvocationId == invocationId);
        }
    }

    public static void Record(string executable, IReadOnlyList<string> arguments, string standardOutput,
        string standardError, int? exitCode, DateTimeOffset startedAt, DateTimeOffset? finishedAt, bool truncated,
        bool protocolOutput, Guid? commandId = null, decimal? progressPercent = null)
    {
        var step = CurrentStep.Value;
        if (step is null || Current.Value != step.Capture) return;
        var command = FormatCommand(executable, arguments);
        var log = new RuntimeCommandLog(commandId ?? Guid.NewGuid(), command,
            protocolOutput ? string.Empty : Redact(standardOutput), Redact(standardError), exitCode,
            startedAt, finishedAt, truncated, progressPercent);
        lock (step.Capture.sync)
        {
            step.Capture.records.RemoveAll(item => item.Command.Id == log.Id);
            step.Capture.records.Add(new(step.NodeId, step.InvocationId, log));
        }
    }

    public static RuntimeCommandLog CreateOperationLog(string operation, Guid id, DateTimeOffset startedAt,
        DateTimeOffset? finishedAt, int? exitCode, long? bytesProcessed = null) =>
        new(id, operation, string.Empty, string.Empty, exitCode, startedAt, finishedAt, false,
            Kind: "operation", BytesProcessed: bytesProcessed);

    public static void RecordOperation(string operation, Guid id, DateTimeOffset startedAt,
        DateTimeOffset? finishedAt, int? exitCode, long? bytesProcessed = null)
    {
        var step = CurrentStep.Value;
        if (step is null || Current.Value != step.Capture) return;
        var log = CreateOperationLog(operation, id, startedAt, finishedAt, exitCode, bytesProcessed);
        step.Capture.Store(step.NodeId, step.InvocationId, log);
    }

    public static bool RecordForExecution(Guid executionId, string nodeId, Guid invocationId,
        string operation, Guid id, DateTimeOffset startedAt, DateTimeOffset? finishedAt,
        int? exitCode, long? bytesProcessed = null)
    {
        if (!ActiveExecutions.TryGetValue(executionId, out var capture)) return false;
        lock (capture.sync)
        {
            if (capture.completedSteps.Contains((nodeId, invocationId))) return false;
            capture.records.RemoveAll(item => item.Command.Id == id);
            capture.records.Add(new(nodeId, invocationId,
                CreateOperationLog(operation, id, startedAt, finishedAt, exitCode, bytesProcessed)));
        }
        return true;
    }

    private void Store(string nodeId, Guid invocationId, RuntimeCommandLog log)
    {
        lock (sync)
        {
            records.RemoveAll(item => item.Command.Id == log.Id);
            records.Add(new(nodeId, invocationId, log));
        }
    }

    public static string FormatCommand(string executable, IReadOnlyList<string> arguments)
    {
        var rendered = new List<string> { Quote(Redact(executable)) };
        var redactNext = false;
        foreach (var argument in arguments)
        {
            if (redactNext)
            {
                rendered.Add("[masqué]");
                redactNext = false;
                continue;
            }
            var equals = argument.IndexOf('=');
            var key = equals > 0 ? argument[..equals] : argument;
            if (SensitiveArgument.IsMatch(key))
            {
                if (equals > 0) rendered.Add(Quote(argument[..(equals + 1)] + "[masqué]"));
                else
                {
                    rendered.Add(Quote(argument));
                    redactNext = true;
                }
                continue;
            }
            rendered.Add(Quote(Redact(argument)));
        }
        return string.Join(' ', rendered);
    }

    private static string Quote(string value) => SafeArgument.IsMatch(value)
        ? value
        : "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    public static string Redact(string value)
    {
        var result = AuthorizationHeader.Replace(value, "$1[masqué]");
        result = SecretAssignment.Replace(result, "$1$2[masqué]");
        return UrlCredential.Replace(result, "$1[masqué]@");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (executionId is { } id && ActiveExecutions.TryGetValue(id, out var active) && ReferenceEquals(active, this))
            ActiveExecutions.TryRemove(id, out _);
        if (Current.Value == this) Current.Value = previous;
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        private Action? onDispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref onDispose, null)?.Invoke();
    }
}
