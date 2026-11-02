namespace Encoding.Contracts.Flows;

public sealed record SaveFlowRequest(int ExpectedVersion, FlowDefinition Definition, int? ExpectedDraftRevision = null);
public sealed record SaveFlowDraftRequest(int ExpectedRevision, FlowDefinition Definition);
public sealed record FlowDraftImport(FlowDefinition Definition, int Revision = 1);
public sealed record FlowDraftResponse(FlowDefinition Definition, int DraftRevision, int PublishedVersion, string BackupStatus, DateTimeOffset CreatedAt);
public sealed record FlowVersionResponse(FlowDefinition Definition, string BackupStatus, DateTimeOffset CreatedAt);
public sealed record FlowSummary(Guid Id, string Name, string MediaType, int Version, int NodeCount, string BackupStatus, DateTimeOffset UpdatedAt, bool IsDraft = false);
public sealed record CloneFlowRequest(string? Name = null);
public sealed record CreateFlowFromTemplateRequest(
    string? Name = null,
    int? TemplateRevision = null,
    IReadOnlyList<FlowTemplateNodeValues>? Configuration = null);

public sealed record FlowDraftBackupSummary(int Revision, string BackupStatus, DateTimeOffset? CheckedAt);
public sealed record FlowLibraryUsage(Guid LibraryId, string LibraryName, int Version, bool IsActive);
