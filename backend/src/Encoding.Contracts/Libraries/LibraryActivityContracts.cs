namespace Encoding.Contracts.Libraries;

public sealed record LibraryActivityFile(
    Guid Id,
    Guid LibraryId,
    string Library,
    string RelativePath,
    long Bytes,
    DateTimeOffset ModifiedAt,
    bool LibraryActive,
    string? Flow,
    int? FlowVersion,
    string Status,
    Guid? ExecutionId,
    string? ExecutionState,
    string? Detail,
    Guid? NodeId,
    string? Node,
    decimal? Progress,
    DateTimeOffset? UpdatedAt,
    string? ExecutionOwnershipState = null,
    string? QuarantineReason = null,
    Guid? ConflictingExecutionId = null);

public sealed record LibraryActivityCounts(
    int Untreated,
    int Active,
    int Done,
    int Failed,
    int Disabled,
    int Cancelled = 0);

public sealed record LibraryActivityResponse(
    IReadOnlyList<LibraryActivityFile> Files,
    LibraryActivityCounts Counts,
    int TotalCount,
    int Limit,
    int Offset,
    DateTimeOffset ObservedAt);
