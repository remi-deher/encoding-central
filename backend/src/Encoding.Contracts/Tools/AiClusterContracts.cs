namespace Encoding.Contracts.Tools;

/// <summary>
/// Réplication d'une connexion IA sur un nœud : <c>State</c> vaut <c>synced</c> (identique à la version du cluster), <c>pending</c>
/// (écart en cours de résorption ou synchronisation en échec) ou <c>absent</c> (aucune connexion dans le cluster). Aucun secret.
/// </summary>
public sealed record AiCredentialStatus(string Provider, string State, long? Revision, DateTimeOffset? SyncedAt);
public sealed record AiNodeAvailability(Guid NodeId, string Name, IReadOnlyList<string> Providers, bool Reachable = true,
    IReadOnlyDictionary<string, string?>? Models = null, IReadOnlyList<AiModelCatalog>? Catalogs = null,
    IReadOnlyList<AiCredentialStatus>? Credentials = null, IReadOnlyList<string>? Reconnect = null);
public sealed record AiClusterView(IReadOnlyList<AiNodeAvailability> Nodes);
public sealed record AiSessionTurn(Guid RequestId, string UserPrompt, string Content, AiUsage? Usage = null, DateTimeOffset? CompletedAt = null,
    AiTurnModelSelection? Selection = null, Guid? ExecutorNodeId = null);
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
public sealed record AiNodeUsage(Guid? NodeId, string Name, IReadOnlyList<AiProviderUsage> Providers, AiUsageTotals Total);
public sealed record AiClusterUsage(int Days, DateTimeOffset Since, IReadOnlyList<AiNodeUsage> Nodes, AiUsageTotals Total, bool Truncated);
