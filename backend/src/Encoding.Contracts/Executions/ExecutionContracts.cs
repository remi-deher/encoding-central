using System.Text.Json;
namespace Encoding.Contracts.Executions;

public sealed record ExecutionResponse(
 Guid Id, string Media, string Library, string Flow, int FlowVersion,
 string State, Guid? NodeId, string? Node, decimal? Progress, string Detail,
 int Attempt, int Revision, DateTimeOffset QueuedAt, DateTimeOffset? StartedAt,
 DateTimeOffset? FinishedAt, DateTimeOffset UpdatedAt, JsonElement? BeforeMetadata = null, JsonElement? AfterMetadata = null, string? PublishedPath = null,
 IReadOnlyList<ResolvedVideoEncoding>? VideoEncodings = null, Guid? LibraryId = null, TaskOwnershipStatus? Ownership = null, ExecutionFailureInfo? Failure = null, string? CurrentPhase = null,
 bool IsActivelyProcessing = false);
public sealed record ExecutionEventResponse(Guid Id, int Revision, string State, string Detail, DateTimeOffset CreatedAt);
public sealed record CreateExecutionRequest(Guid Id, string Media, string Library, string Flow, int FlowVersion, JsonElement FlowDefinition);
public sealed record ExecutionOverviewResponse(
 IReadOnlyList<ExecutionResponse> Executions, int QueuedCount, int ActiveCount,
 int FailedCount, int CancelledCount, int SucceededCount, DateTimeOffset ObservedAt,
 IReadOnlyList<ExecutionActivityResponse> Activity, int DeferredCount = 0,
 int TotalCount = 0, int Limit = 200, int Offset = 0, int QuarantinedCount = 0);
public sealed record ExecutionFilterQuery(
 string? State = null, Guid? LibraryId = null, Guid? NodeId = null,
 DateTimeOffset? Since = null, int Limit = 200, int Offset = 0);
public sealed record ExecutionActivityResponse(Guid Id, Guid ExecutionId, string Media, string State, string Detail, DateTimeOffset CreatedAt);
public sealed record RecordExecutionProgress(Guid EventId, int ExpectedRevision, string State, Guid? NodeId, decimal? Progress, string Detail);
public sealed record ExecutionDetailSnapshot(
 ExecutionResponse Execution,
 IReadOnlyList<ExecutionEventResponse> Events,
 IReadOnlyList<ExecutionPipelineStepResponse> Pipeline,
 DateTimeOffset ObservedAt);

public sealed record ExecutionFailureInfo(string Code, string? StepId, bool? PublicationConfirmed);
