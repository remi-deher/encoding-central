namespace Encoding.Contracts.Nodes;

public enum NodeGroupMode { Manual, Automatic }

public sealed record NodeGroupCriteria(
    IReadOnlyList<NodeRole>? Roles = null,
    string? OperatingSystem = null,
    string? Architecture = null,
    int? MinimumMemoryMb = null,
    IReadOnlyList<string>? RequiredToolIds = null,
    int? MinimumConcurrentTasks = null,
    int? MinimumProcessorCount = null,
    IReadOnlyList<string>? RequiredEncoders = null);

public sealed record NodeGroupResponse(
    Guid Id,
    string Name,
    string Description,
    NodeGroupMode Mode,
    NodeGroupCriteria Criteria,
    IReadOnlyDictionary<string, string?> Labels,
    IReadOnlyList<Guid> NodeIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SaveNodeGroupRequest(
    string Name,
    string Description,
    NodeGroupMode Mode,
    NodeGroupCriteria Criteria,
    IReadOnlyDictionary<string, string?> Labels,
    IReadOnlyList<Guid> NodeIds);

public sealed record NodeIdentityResponse(
    Guid NodeId,
    string? DisplayName,
    IReadOnlyDictionary<string, string?> Labels,
    IReadOnlyDictionary<string, string?> EffectiveLabels,
    IReadOnlyList<Guid> GroupIds);

public sealed record SaveNodeIdentityRequest(
    string? DisplayName,
    IReadOnlyDictionary<string, string?> Labels);
