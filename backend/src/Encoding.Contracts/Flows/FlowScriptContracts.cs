namespace Encoding.Contracts.Flows;

public sealed record FlowScriptDraft(string Name, string Language, string Description, string Code);
public sealed record FlowScriptVersionSummary(Guid ScriptId, int Version, string Name, string Language,
    string Description, DateTimeOffset CreatedAt);
public sealed record FlowScriptSummary(Guid Id, int DraftRevision, string DraftName, string DraftLanguage,
    string DraftDescription, int PublishedVersion, DateTimeOffset UpdatedAt,
    IReadOnlyList<FlowScriptVersionSummary> Versions);
public sealed record FlowScriptDetails(FlowScriptSummary Script, FlowScriptDraft Draft);
public sealed record FlowScriptVersion(Guid ScriptId, int Version, string Name, string Language,
    string Description, string Code, DateTimeOffset CreatedAt);
public sealed record CreateFlowScriptRequest(FlowScriptDraft Draft);
public sealed record SaveFlowScriptDraftRequest(int ExpectedRevision, FlowScriptDraft Draft);
public sealed record PublishFlowScriptRequest(int ExpectedRevision);
