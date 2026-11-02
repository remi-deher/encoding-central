using System.Text.Json;

namespace Encoding.Contracts.Flows;

public sealed record FlowAiGenerateRequest(
    string MediaType,
    string UserPrompt,
    string? Provider = null,
    string? SessionId = null,
    Guid? RequestId = null,
    string? Model = null,
    string? ReasoningEffort = null);

public sealed record FlowAiRefactorRequest(
    FlowDefinition CurrentFlow,
    string UserPrompt,
    string? Provider = null,
    string? SessionId = null,
    Guid? RequestId = null,
    string? Model = null,
    string? ReasoningEffort = null);

public sealed record FlowAiExplainRequest(
    FlowDefinition Flow,
    string? Provider = null,
    string? SessionId = null,
    Guid? RequestId = null,
    string? Model = null,
    string? ReasoningEffort = null);

public sealed record FlowAiDraftRefactorRequest(
    int ExpectedDraftRevision,
    string UserPrompt,
    string? Provider = null,
    string? SessionId = null,
    Guid? RequestId = null);

public sealed record FlowAiChange(string Operation, string Path, JsonElement? Before = null, JsonElement? After = null);

public sealed record FlowAiDraftRefactorProposal(
    Guid FlowId,
    int ExpectedDraftRevision,
    FlowDefinition Before,
    FlowDefinition After,
    IReadOnlyList<FlowAiChange> Changes,
    string? Explanation = null);

public sealed record FlowAiResponse(
    bool Success,
    FlowDefinition? Flow,
    string? Explanation = null,
    IReadOnlyList<FlowValidationIssue>? ValidationIssues = null,
    string? SessionId = null,
    Guid? RequestId = null);
