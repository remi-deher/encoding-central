namespace Encoding.Contracts.Tools;

public sealed record AiWorkerAvailability(Guid WorkerId, string Name, IReadOnlyList<string> Providers, bool Reachable = true,
    IReadOnlyDictionary<string, string?>? Models = null, IReadOnlyList<AiModelCatalog>? Catalogs = null);
public sealed record AiCentralView(IReadOnlyList<AiWorkerAvailability> Workers);
public sealed record AiSessionTurn(Guid RequestId, string UserPrompt, string Content, AiUsage? Usage = null, DateTimeOffset? CompletedAt = null,
    AiTurnModelSelection? Selection = null, Guid? ExecutorWorkerId = null);
public sealed record AiSessionState(string Id, string Provider, string SystemPrompt, IReadOnlyList<AiSessionTurn> Turns,
    Guid? PendingRequest = null, DateTimeOffset? AttemptDeadline = null, AiPrompt? PendingPrompt = null, int Attempt = 0,
    string? Model = null, DateTimeOffset? UpdatedAt = null, string? LastError = null, AiSessionScope? Scope = null,
    string? ReasoningEffort = null);
public sealed record AiSessionSummary(string Id, string Provider, string? Model, string Status,
    int TurnCount, int Attempt, DateTimeOffset? UpdatedAt, DateTimeOffset? AttemptDeadline, string? LastError,
    AiUsage? Usage, AiSessionScope? Scope = null);
public sealed record AiSessionPage(IReadOnlyList<AiSessionSummary> Items, int Offset, bool HasMore);
public sealed record AiGenerateRequest(string? Provider, AiPrompt Prompt, bool AllowModelChanges = false);
public sealed record AiExecuteRequest(Guid Id, string Provider, AiPrompt Prompt, DateTimeOffset Deadline);
public sealed record ClaudeTokenRequest(string Token);

/// <summary>Consommation observée (tokens rapportés par les fournisseurs) ; jamais estimée quand elle n'est pas rapportée.</summary>
public sealed record AiUsageTotals(long Turns, long InputTokens, long OutputTokens, long CachedInputTokens, decimal? CostUsd, long TurnsWithoutUsage);
public sealed record AiProviderUsage(string Provider, AiUsageTotals Totals);
public sealed record AiWorkerUsage(Guid? WorkerId, string Name, IReadOnlyList<AiProviderUsage> Providers, AiUsageTotals Total);
public sealed record AiCentralUsage(int Days, DateTimeOffset Since, IReadOnlyList<AiWorkerUsage> Workers, AiUsageTotals Total, bool Truncated);
