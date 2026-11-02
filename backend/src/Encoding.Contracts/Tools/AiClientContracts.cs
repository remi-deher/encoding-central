namespace Encoding.Contracts.Tools;

/// <summary>Prompt passed to an AI text inference client.</summary>
public sealed record AiPrompt(
    string SystemPrompt,
    string UserPrompt,
    string? Model = null,
    double Temperature = 0.3,
    string? SessionId = null,
    Guid? RequestId = null,
    AiSessionScope? Scope = null,
    string? ReasoningEffort = null, Guid? NodeId = null);

/// <summary>Result returned by an AI text client.</summary>
public sealed record AiCompletionResult(
    bool Success,
    string Content,
    string? ErrorMessage = null,
    bool IsRateLimited = false,
    string? SessionId = null,
    Guid? RequestId = null,
    AiUsage? Usage = null, Guid? ExecutorNodeId = null);

/// <summary>Observed provider usage; null values are unavailable, never zero estimates.</summary>
public sealed record AiUsage(long? InputTokens = null, long? OutputTokens = null, long? CachedInputTokens = null,
    decimal? EstimatedCostUsd = null, string? Model = null);

/// <summary>Contract for AI text inference clients (CLI or HTTP API).</summary>
public interface IAiTextClient
{
    Task<AiCompletionResult> GenerateTextAsync(AiPrompt prompt, CancellationToken ct = default);
}

/// <summary>Application association only; it does not grant access to files or share context across sessions.</summary>
public sealed record AiSessionScope(string Operation, Guid? LibraryId = null, Guid? FlowId = null,
    Guid? MediaId = null, Guid? ExecutionId = null);
