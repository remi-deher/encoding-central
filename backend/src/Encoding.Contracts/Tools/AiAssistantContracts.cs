using System.Text.Json;

namespace Encoding.Contracts.Tools;

public sealed record AiAssistantSource(
    string Kind,
    Guid ResourceId,
    string Label,
    string Href,
    int? Version = null);

public sealed record AiAssistantConversation(
    string Id,
    string Provider,
    string Title,
    AiAssistantSource Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<AiAssistantSource>? AdditionalSources = null,
    string? Model = null,
    string? ReasoningEffort = null, Guid? NodeId = null,
    string? TitleSource = null,
    /// <summary>Nœud qui coordonne la discussion (reprise comprise), renseigné à la lecture.</summary>
    Guid? CoordinatorId = null);

public sealed record AiAssistantRenameRequest(string Title);

public sealed record AiAssistantModelOption(string Provider, string? Model, string Label,
    IReadOnlyList<string> ReasoningEfforts, Guid? NodeId = null, string? NodeName = null,
    string? DefaultReasoningEffort = null, bool ReasoningKnown = false, bool IsDefault = false);

public sealed record AiAssistantOptions(string DefaultProvider, string? DefaultModel,
    IReadOnlyList<AiAssistantModelOption> Models, IReadOnlyList<AiNodeModelCatalog>? Catalogs = null,
    string? DefaultReasoningEffort = null);

public sealed record AiAssistantResourceReference(string Type, Guid Id, int? Version = null);

public sealed record AiAssistantSourcesRequest(IReadOnlyList<AiAssistantResourceReference> Sources);

public sealed record AiAssistantToolCall(string Name, Guid? FlowId = null, string? State = null, int? Limit = null,
    JsonElement? Arguments = null);

public sealed record AiAssistantFileAttachment(string Name, string Content);

public sealed record AiAssistantToolResult(string Name, string Label, string Href, JsonElement Data);

public sealed record AiAssistantProposalDecision(string Decision);

public sealed record AiAssistantPrepareActionRequest(
    Guid ResourceId,
    int ExpectedVersion);

public sealed record AiAssistantPreparedAction(string ConfirmationToken, string Summary, DateTimeOffset ExpiresAt);

public sealed record AiAssistantConfirmActionRequest(string ConfirmationToken);

public sealed record AiAssistantTurn(
    Guid RequestId,
    string UserPrompt,
    string Content,
    IReadOnlyList<AiAssistantSource>? Sources = null, AiTurnModelSelection? Selection = null,
    string? ActualModel = null, Guid? ExecutorNodeId = null);

public sealed record AiAssistantConversationDetail(
    AiAssistantConversation Conversation,
    IReadOnlyList<AiAssistantTurn> Turns,
    AiAssistantPendingTurn? PendingTurn = null);

public sealed record AiAssistantConversationSearchResult(
    string ConversationId,
    string Title,
    string Provider,
    string SourceLabel,
    string Snippet,
    int? TurnIndex,
    string? MessageRole,
    DateTimeOffset UpdatedAt);

public sealed record AiAssistantPendingTurn(Guid RequestId, string UserPrompt, bool IsRunning);

public sealed record AiAssistantMessageRequest(
    Guid RequestId,
    string UserPrompt,
    string? ConversationId = null,
    string? ResourceType = null,
    Guid? ResourceId = null,
    int? ResourceVersion = null,
    IReadOnlyList<AiAssistantToolCall>? ToolCalls = null,
    string? Provider = null,
    string? Model = null,
    string? ReasoningEffort = null, Guid? NodeId = null,
    IReadOnlyList<AiAssistantFileAttachment>? Attachments = null);

public sealed record AiAssistantMessageResponse(
    bool Success,
    AiAssistantConversation Conversation,
    Guid RequestId,
    string? Content,
    string? ErrorMessage,
    IReadOnlyList<AiAssistantSource> Sources, AiTurnModelSelection? Selection = null,
    string? ActualModel = null, Guid? ExecutorNodeId = null,
    AiAssistantActionProposal? ActionProposal = null);

public sealed record AiAssistantActionProposal(
    string Action,
    string ConfirmationToken,
    string Summary,
    DateTimeOffset ExpiresAt,
    string? OperationName = null);

public sealed record AiAssistantActivity(
    Guid RequestId,
    string? ConversationId,
    string Provider,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    long? DurationMilliseconds,
    string? ErrorCategory,
    string Operation = "assistant",
    IReadOnlyList<string>? Tools = null,
    string? Progress = null,
    string? ProgressDetail = null,
    string? ConversationTitle = null);

// Only exchanged over the authenticated, encrypted peer transport; never accepted from a public HTTP body.
public sealed record AiAssistantForwardRequest(AiAssistantMessageRequest Request, Guid OwnerId, bool IsAdmin);
public sealed record AiAssistantForwardResponse(AiAssistantMessageResponse? Result, string? Error = null, bool InvalidRequest = false);
public sealed record AiAssistantForwardCancel(Guid OwnerId, Guid RequestId);
