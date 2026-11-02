using System.Text.Json;
using System.Text.Json.Serialization;

namespace Encoding.Contracts.Flows;

/// <summary>
/// Stable invocation identity for duplicate detection. A new lease epoch invalidates
/// reports from a previous worker; the host must verify it against shared storage.
/// </summary>
public sealed record FlowStepInvocation(
    int ContractVersion,
    Guid InvocationId,
    Guid ExecutionId,
    int Attempt,
    Guid WorkerNodeId,
    long LeaseEpoch,
    string NodeId,
    FlowComponentReference Component,
    IReadOnlyDictionary<string, JsonElement> Configuration,
    FlowStepContext Context,
    DateTimeOffset Deadline,
    IReadOnlyDictionary<string, JsonElement>? PluginConfiguration = null);

/// <summary>
/// Artifacts are opaque identifiers resolved to local files by the worker. MediaPath and LibraryId
/// identify the library file being processed so integrations can address it in an external service;
/// they are read-only context and never a location a plugin may write to.
/// </summary>
public sealed record FlowStepContext(
    string? SourceArtifactId,
    string? CurrentArtifactId,
    JsonElement? BeforeMetadata,
    JsonElement? CurrentMetadata,
    IReadOnlyDictionary<string, JsonElement> Variables,
    string? MediaPath = null,
    Guid? LibraryId = null);

/// <summary>
/// Exactly one of OutputId or TerminalResult is required. The host validates this
/// against the component descriptor. A transport failure is not an output selection.
/// </summary>
public sealed record FlowStepResult(
    int ContractVersion,
    Guid InvocationId,
    long LeaseEpoch,
    string? OutputId,
    FlowTerminalResult? TerminalResult,
    IReadOnlyDictionary<string, JsonElement> VariableChanges,
    string? CandidateArtifactId,
    string Detail);

[JsonConverter(typeof(JsonStringEnumConverter<FlowTerminalResult>))]
public enum FlowTerminalResult
{
    Unchanged,
    Success,
    Failed,
    Deferred
}
