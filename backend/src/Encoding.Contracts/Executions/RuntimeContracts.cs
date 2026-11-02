using System.Text.Json;
using Encoding.Contracts.Flows;

namespace Encoding.Contracts.Executions;

public sealed record EnqueueMediaRequest(Guid Id, Guid LibraryId, string MediaPath, Guid FlowId, int FlowVersion);
public sealed record RuntimeLease(Guid ExecutionId, int Attempt, int Revision, long Epoch, DateTimeOffset ExpiresAt,
    Guid SourceNodeId, string? SourceRoot, string MediaPath, string? SourceSha256, FlowDefinition Flow, RuntimeCheckpoint? Checkpoint, string EntryNodeId,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? PluginConfigurations = null, Guid? LibraryId = null,
    RuntimeWaitProbe? Probe = null, IReadOnlyList<RuntimePublicationGuard>? Guards = null, bool DirectRead = false,bool SourceWriteGuardsSupported=false,
        bool VerifyPublicationAccess=false, bool VerifyReplacementAccess=false,
    FlowPlacementPlan? PlacementPlan = null);
public sealed record RuntimeFence(long Epoch, IReadOnlyList<RuntimeStepCommand>? Commands = null);
/// <summary>Métadonnées d'origine publiées dès que le worker a sondé la source, sans attendre la fin d'une étape.</summary>
public sealed record ReportSourceMetadata(long Epoch, JsonElement Metadata);
public sealed record RuntimeAiSubtitleItem(string Id, string Text);
public sealed record RuntimeAiSubtitleTranslationRequest(long Epoch, Guid InvocationId, string FlowNodeId,
    int BatchIndex, IReadOnlyList<RuntimeAiSubtitleItem> Items);
public sealed record RuntimeAiSubtitleTranslationResponse(bool Success,
    IReadOnlyList<RuntimeAiSubtitleItem>? Items = null, string? ErrorCode = null, string? ErrorMessage = null);
public sealed record RuntimeAiMetadataEnrichmentRequest(long Epoch, Guid InvocationId, string FlowNodeId,
    string FileName, string MediaType, JsonElement Metadata);
public sealed record RuntimeAiMetadataEnrichmentResponse(bool Success, JsonElement? Metadata = null,
    string? ErrorCode = null, string? ErrorMessage = null);
public sealed record RuntimeLeaseStatus(bool CancelRequested, int Revision, DateTimeOffset ExpiresAt);
public sealed record RuntimeSourceState(string? SourceSha256, IReadOnlyDictionary<string,string>? Inputs = null);
public sealed record RuntimeTransfer(Guid ExecutionId, long Epoch, string Root, string MediaPath, string? SourceSha256,
    string? ResultSha256, string? ResultExtension, bool Publish, IReadOnlyList<string>? InputPaths = null, string MediaType = "video", bool CanWrite = true, int? ReplacementGraceHours = null,
    Guid? LibraryId = null, IReadOnlyList<RuntimePublicationGuard>? Guards = null, RuntimeSourceObservation? Observation = null,
        bool VerifyPublicationAccess = false, bool VerifyReplacementAccess = false,
    string? PublicationStepNodeId = null, Guid? PublicationStepInvocationId = null);
/// <summary>Garde de publication à consulter avant un remplacement de source (architecture 0059) : composant de plugin à invoquer sur le nœud d'accès.</summary>
public sealed record RuntimePublicationGuard(string PluginId, string PluginVersion, string ComponentId, string ComponentVersion, int ConfigurationSchemaVersion,
    IReadOnlyDictionary<string,JsonElement> Configuration, IReadOnlyDictionary<string,JsonElement> PluginConfiguration, int MaxWaitMinutes,
    IReadOnlyList<string> WaitOutputs, IReadOnlyList<string> AllowOutputs);
public sealed record RuntimeContext(string CurrentSha256, string Extension, IReadOnlyDictionary<string,JsonElement> Variables,
    JsonElement? BeforeMetadata, JsonElement? CurrentMetadata, IReadOnlyList<RuntimePlanStep> Plan,
    IReadOnlyList<ResolvedVideoEncoding>? VideoEncodings = null, RuntimeSourceObservation? DirectSource = null);
/// <summary>Size and modification time observed before direct inspection; never a content checksum.</summary>
public sealed record RuntimeSourceObservation(long Bytes, long LastWriteUtcTicks)
{
    public const string Capability = "runtime/direct-read-v1";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => Bytes > 0 && LastWriteUtcTicks > 0 && LastWriteUtcTicks <= DateTime.MaxValue.Ticks;
}
public sealed record ResolvedVideoEncoding(string FlowNodeId, Guid WorkerId, string Codec, string EncoderId,
    string FfmpegVersion, string Acceleration, string QualityMode, string QualityOption, decimal Quality,
    string PresetId, string? PresetOption, string? PresetValue,
    Guid ProfileId, int ProfileVersion, string ProfileName, int Priority, DateTimeOffset ResolvedAt);
public sealed record RuntimePlanStep(string ComponentId, IReadOnlyDictionary<string,JsonElement> Configuration, string? FlowNodeId = null);
public sealed record RuntimeCheckpoint(string NodeId, Guid InvocationId, string? OutputId, string? NextNodeId, RuntimeContext Context, string? TerminalResult,
    RuntimeHandoffSelector? Handoff = null, RuntimeWait? Wait = null, bool PlacementHandoff = false);
public sealed record RuntimeCommandLog(Guid Id, string Command, string StandardOutput, string StandardError, int? ExitCode,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, bool Truncated, decimal? ProgressPercent = null,
    string? Kind = null, long? BytesProcessed = null);
public sealed record RuntimeStepCommand(string NodeId, Guid InvocationId, RuntimeCommandLog Command);
public sealed record ExecutionPipelineStepResponse(string NodeId, string NodeName, Guid InvocationId,
    string ComponentKind, string Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    string? OutputId, string? OutputLabel, string? NextNodeId, IReadOnlyList<RuntimeCommandLog> Commands, RuntimeStepDiagnostic? Diagnostic = null);
public sealed record StartRuntimeStep(long Epoch, string NodeId);
public sealed record StartedRuntimeStep(Guid InvocationId, RuntimeCheckpoint? Completed);
public sealed record CompleteRuntimeStep(long Epoch, Guid EventId, int ExpectedRevision, RuntimeCheckpoint Checkpoint,
    IReadOnlyList<RuntimeCommandLog>? Commands = null, bool CleanupAiSessions = false);
public sealed record FinishRuntimeExecution(long Epoch, Guid EventId, int ExpectedRevision, string State, string Detail, string? ResultSha256, string? Extension,
    JsonElement? BeforeMetadata = null, JsonElement? AfterMetadata = null, int? DeferMinutes = null,
    IReadOnlyList<RuntimeStepCommand>? Commands = null, bool ContinueFlow = false);
/// <summary>État de la publication demandée par un bloc de flow (ADR 0003) : le worker reprend le flow lorsque <see cref="Published"/> est vrai.</summary>
public sealed record RuntimePublicationState(bool Published, string? Path, string? Sha256, bool Requested = false);
public sealed record ReportRuntimeCommand(long Epoch, string NodeId, Guid InvocationId, RuntimeCommandLog Command);
public sealed record ConfirmRuntimePublication(long Epoch, Guid EventId, int ExpectedRevision, string PublishedPath, string Sha256, SourceReplacementReport? Replacement = null);

/// <summary>Demande l'état courant d'exécutions connues du nœud, pour décider de la suppression de leurs dossiers de travail.</summary>
public sealed record RuntimeExecutionStatesRequest(IReadOnlyList<Guid> Ids);
public sealed record RuntimeExecutionState(Guid Id, string State);

public sealed record RuntimeStepDiagnostic(string Code, string Message);
