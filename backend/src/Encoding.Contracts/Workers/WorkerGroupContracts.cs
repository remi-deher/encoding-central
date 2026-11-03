namespace Encoding.Contracts.Workers;

public enum WorkerGroupMode { Manual, Automatic }

public sealed record WorkerGroupCriteria(
    IReadOnlyList<WorkerRole>? Roles = null,
    string? OperatingSystem = null,
    string? Architecture = null,
    int? MinimumMemoryMb = null,
    IReadOnlyList<string>? RequiredToolIds = null,
    int? MinimumConcurrentTasks = null,
    int? MinimumProcessorCount = null,
    IReadOnlyList<string>? RequiredEncoders = null);

public sealed record WorkerGroupResponse(
    Guid Id,
    string Name,
    string Description,
    WorkerGroupMode Mode,
    WorkerGroupCriteria Criteria,
    IReadOnlyDictionary<string, string?> Labels,
    IReadOnlyList<Guid> WorkerIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SaveWorkerGroupRequest(
    string Name,
    string Description,
    WorkerGroupMode Mode,
    WorkerGroupCriteria Criteria,
    IReadOnlyDictionary<string, string?> Labels,
    IReadOnlyList<Guid> WorkerIds);

public sealed record WorkerIdentityResponse(
    Guid WorkerId,
    string? DisplayName,
    IReadOnlyDictionary<string, string?> Labels,
    IReadOnlyDictionary<string, string?> EffectiveLabels,
    IReadOnlyList<Guid> GroupIds);

public sealed record SaveWorkerIdentityRequest(
    string? DisplayName,
    IReadOnlyDictionary<string, string?> Labels);
