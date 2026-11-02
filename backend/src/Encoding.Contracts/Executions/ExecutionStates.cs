namespace Encoding.Contracts.Executions;

/// <summary>
/// Named execution lifecycle states persisted in SQL and exchanged on the API.
/// Values are the wire/storage strings; keep them stable across nodes.
/// </summary>
public static class ExecutionStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Cancelling = "cancelling";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Deferred = "deferred";
    public const string Quarantined = "quarantined";

    public static readonly IReadOnlyList<string> All =
    [
        Queued, Running, Cancelling, Succeeded, Failed, Cancelled, Deferred, Quarantined
    ];

    public static readonly IReadOnlyList<string> Active = [Queued, Running, Cancelling];
    public static readonly IReadOnlyList<string> Terminal = [Succeeded, Failed, Cancelled];
    public static readonly IReadOnlyList<string> Finished = [Succeeded, Failed, Cancelled, Deferred];
    public static readonly IReadOnlyList<string> ProgressRecordable = [Running, Succeeded, Failed, Cancelled, Deferred];

    public static bool IsKnown(string? state) =>
        state is Queued or Running or Cancelling or Succeeded or Failed or Cancelled or Deferred or Quarantined;

    public static bool IsActive(string? state) => state is Queued or Running or Cancelling;

    public static bool IsTerminal(string? state) => state is Succeeded or Failed or Cancelled;

    public static bool IsFinished(string? state) => state is Succeeded or Failed or Cancelled or Deferred;

    public static bool TryParse(string? value, out string state)
    {
        if (IsKnown(value))
        {
            state = value!;
            return true;
        }

        state = "";
        return false;
    }
}

/// <summary>
/// Terminal / block outcome strings written into checkpoints and finish payloads
/// (lower-cased <see cref="Flows.FlowTerminalResult"/> plus the conventional error output id).
/// </summary>
public static class ExecutionOutcomes
{
    public const string Unchanged = "unchanged";
    public const string Success = "success";
    public const string Failed = "failed";
    public const string Deferred = "deferred";
    public const string ErrorOutput = "error";

    public static readonly IReadOnlyList<string> Terminals = [Unchanged, Success, Failed, Deferred];

    public static bool IsKnownTerminal(string? value) =>
        value is Unchanged or Success or Failed or Deferred;

    public static bool IsFailure(string? terminal, string? lastOutput) =>
        terminal == Failed || terminal is null && lastOutput == ErrorOutput;
}

/// <summary>Version numbers for cluster task ownership messages (<c>ClusterTaskEvent</c> / peer envelopes).</summary>
public static class ClusterTaskMessageVersions
{
    /// <summary>Current <see cref="ClusterTaskEvent.Version"/> and peer request/reply envelope version (<c>tasks/v2</c>).</summary>
    public const int Current = 2;

    public static bool IsSupported(int version) => version == Current;

    public static bool IsUnsupportedSuccessor(int version) => version == Current + 1;
}
