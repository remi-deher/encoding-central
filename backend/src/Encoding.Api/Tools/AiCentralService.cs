using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Encoding.Contracts.Tools;
using Encoding.Api.Nodes;
using Encoding.Contracts.Auth;

namespace Encoding.Api.Tools;

/// <summary>A stable session coordinator owns writes; inference may move between connected peers without moving secrets.</summary>
public sealed class AiCentralService(Encoding.Api.Infrastructure.CentralPeerIdentityStore identity, AiCentralStore store, DeviceCodeAuthService auth,
    AiSettingsService settings, AiClientFactory factory, IConfiguration configuration, LocalWorkerRegistry localNode, AiQuotaService? quotaService = null,
        AiModelCatalogService? modelCatalog = null, IServiceProvider? services = null)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new();
    private readonly ConcurrentDictionary<Guid, Lazy<Task<AiCompletionResult>>> running = new();
    public async Task<AiWorkerAvailability> LocalAsync(CancellationToken ct)
    {
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        preparation.CancelAfter(TimeSpan.FromSeconds(20));
        var token = preparation.Token;
        var providers = new List<string>();
        IReadOnlyList<AiToolInfo> tools = [];
        var models = new Dictionary<string, string?>(StringComparer.Ordinal);
        IReadOnlyList<AiModelCatalog>? catalogs = null;
        string? name = null;
        try
        {
            tools = await auth.ListToolsAsync(token);
            providers.AddRange(tools.Where(t => t.IsInstalled && t.AuthStatus.State == "authorized").Select(t => t.Id));
            var local = await GetSettingsAsync(token);
            if (!local.CodexEnabled) providers.Remove("codex-cli");
            if (!local.ClaudeEnabled) providers.Remove("claude-cli");
            if (local.OllamaEnabled && !string.IsNullOrWhiteSpace(local.OllamaBaseUrl) && !string.IsNullOrWhiteSpace(local.OllamaModel))
            {
                providers.Add("ollama");
                models["ollama"] = local.OllamaModel;
            }
            if (local.OpenAiEnabled && !string.IsNullOrWhiteSpace(local.OpenAiBaseUrl) && !string.IsNullOrWhiteSpace(local.OpenAiModel) && !string.IsNullOrWhiteSpace(local.OpenAiApiKey))
            {
                providers.Add("openai");
                models["openai"] = local.OpenAiModel;
            }
            if (local.AnthropicEnabled && !string.IsNullOrWhiteSpace(local.AnthropicModel) && !string.IsNullOrWhiteSpace(local.AnthropicApiKey))
            {
                providers.Add("anthropic");
                models["anthropic"] = local.AnthropicModel;
            }
            name = await localNode.ResolveDisplayNameAsync(token);
            if (modelCatalog is not null)
            {
                catalogs = providers.Select(provider => modelCatalog.ReadCachedOrRefresh(provider,
                    provider is "codex-cli" or "claude-cli" ? auth.ConnectionRevision(provider) : "http")).ToArray();
                var checkingCatalogs = tools.Where(tool => tool.IsInstalled && tool.AuthStatus.State == "checking"
                        && (tool.Id != "codex-cli" || local.CodexEnabled)
                        && (tool.Id != "claude-cli" || local.ClaudeEnabled))
                    .Select(tool => new AiModelCatalog(tool.Id, "loading", [], null, DateTimeOffset.UtcNow,
                        "Vérification de la connexion en cours."));
                catalogs = catalogs.Concat(checkingCatalogs).ToArray();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Preparation has one shared budget. Expired catalog reads are unavailable for this observation.
            catalogs = modelCatalog is null ? null : providers.Select(provider => new AiModelCatalog(provider, "unavailable", [], null,
                DateTimeOffset.UtcNow, "Préparation du catalogue dépassée." )).ToArray();
        }
        return new(identity.PeerId, name ?? configuration["Worker:Name"] ?? Environment.MachineName,
            providers.Distinct(StringComparer.Ordinal).ToArray(), Models: models, Catalogs: catalogs);
    }
    public async Task<AiCentralView> ViewAsync(CancellationToken ct) => new([await LocalAsync(ct)]);
    public Task<AiCentralView> CachedViewAsync(CancellationToken ct) => ViewAsync(ct);
    public async Task<AiWorkerModelCatalog> LocalModelsAsync(bool refresh, CancellationToken ct)
    {
        if (refresh && modelCatalog is not null)
            foreach (var provider in new[] { "codex-cli", "claude-cli", "openai", "anthropic", "ollama" }) modelCatalog.Invalidate(provider);
        var local = await LocalAsync(ct);
        return new(local.NodeId, local.Name, local.Catalogs ?? []);
    }
    public async Task<AiCentralModelCatalog> ModelsAsync(bool refresh, CancellationToken ct) =>
        new([await LocalModelsAsync(refresh, ct)], await ModelPolicyAsync(ct));
    public async Task<AiModelPolicy> ModelPolicyAsync(CancellationToken ct) => new(
        (await store.ReadPrefixAsync<AiModelPolicyUpdate>("model-policy/", ct))
            .Where(p => !p.Enabled).Select(p => new AiDisabledModel(p.Provider, p.Model)).ToArray());
    public async Task<AiModelPolicy> UpdateModelPolicyAsync(AiModelPolicyUpdate update, CancellationToken ct)
    {
        if (update.Provider is not ("codex-cli" or "claude-cli" or "openai" or "anthropic" or "ollama")
            || string.IsNullOrWhiteSpace(update.Model) || update.Model.Length > 256 || update.Model.Any(char.IsControl))
            throw new ArgumentException("Modèle ou fournisseur invalide.");
        // One replicated key per provider/model prevents concurrent edits to different models overwriting one another.
        var key = "model-policy/" + update.Provider + "/" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(update.Model)));
        await store.SaveAsync(key, update, ct);
        return await ModelPolicyAsync(ct);
    }
    public static bool Supports(AiWorkerAvailability node, string provider, AiPrompt prompt)
    {
        if (!node.Reachable || !node.Providers.Contains(provider) || prompt.NodeId is { } id && node.NodeId != id) return false;
        if (prompt.Model is null) return true;
        if (node.Catalogs is null) return node.Models?.GetValueOrDefault(provider) == prompt.Model && prompt.ReasoningEffort is null;
        return node.Catalogs.Any(c => c.Provider == provider && c.State == "available"
            && c.Models.Any(m => m.Id == prompt.Model && (prompt.ReasoningEffort is null || m.ReasoningEfforts.Contains(prompt.ReasoningEffort))));
    }
    public async Task<AiWorkerQuotas> LocalQuotasAsync(CancellationToken ct)
    {
        var displayName = await localNode.ResolveDisplayNameAsync(ct) ?? configuration["Worker:Name"] ?? Environment.MachineName;
        if (quotaService is null) return new(identity.PeerId, displayName, []);
        var quotas = await Task.WhenAll(new[] { "codex-cli", "claude-cli" }.Select(provider => quotaService.ReadAsync(provider, ct)));
        return new(identity.PeerId, displayName, quotas);
    }
    public async Task<AiCentralQuotas> QuotasAsync(CancellationToken ct) => new([await LocalQuotasAsync(ct)]);
    private async Task<ApiTokenReplicationNode> LocalApiTokenStateAsync(CancellationToken ct) =>
        new(identity.PeerId, await localNode.ResolveDisplayNameAsync(ct) ?? configuration["Worker:Name"] ?? Environment.MachineName, true,
            services is null ? [] : await services.GetRequiredService<Encoding.Api.Auth.ApiTokenRepository>().ReplicationStateAsync(ct));
    /// <summary>Jetons connus du central (identifiant et révocation).</summary>
    public async Task<ApiTokenReplicationView> ApiTokenReplicationAsync(CancellationToken ct) => new([await LocalApiTokenStateAsync(ct)]);
    public async Task<AiSessionState?> GetSessionAsync(string id, CancellationToken ct)
    {
        _ = Owner(id);
        return await store.ReadAsync<AiSessionState>("session/" + id, ct);
    }

    public async Task CleanupRuntimeSubtitleSessionsAsync(Guid executionId, Guid? invocationId, CancellationToken ct)
    {
        var prefix = $"runtime-ai/subtitles/{executionId:N}/" + (invocationId is { } invocation ? $"{invocation:N}/" : "");
        string? cursor = null;
        while (true)
        {
            var page = await store.ReadPrefixPageAsync<string>(prefix, cursor, 500, ct);
            foreach (var (key, sessionId) in page)
            {
                await store.DeleteAsync(key["ai/".Length..], ct);
                if (!string.IsNullOrWhiteSpace(sessionId)) await store.DeleteAsync("session/" + sessionId, ct);
            }
            if (page.Count < 500) break;
            cursor = page[^1].Key;
        }
    }
    public async Task<AiSessionPage> ListSessionsAsync(int offset, CancellationToken ct)
    {
        if (offset < 0) throw new ArgumentException("Pagination invalide.");
        var sessions = await store.ListSessionsAsync<AiSessionState>(offset, 51, ct);
        return new(sessions.Take(50).Select(session => new AiSessionSummary(
            session.Id, session.Provider, session.Model ?? session.Turns.LastOrDefault()?.Usage?.Model,
            session.PendingRequest is null ? "completed" : session.AttemptDeadline is { } deadline && deadline.AddSeconds(5) > DateTimeOffset.UtcNow ? "running" : "awaitingRetry",
            session.Turns.Count, session.Attempt, session.UpdatedAt, session.AttemptDeadline, session.LastError,
            SumUsage(session.Turns), session.Scope)).ToArray(), offset, sessions.Count > 50);
    }
    /// <summary>Consommation observée par worker exécutant, agrégée depuis les sessions répliquées (assistant et flows).</summary>
    public async Task<AiCentralUsage> UsageAsync(int days, CancellationToken ct)
    {
        if (days is < 1 or > 365) throw new ArgumentException("Période invalide.");
        const int pageSize = 200, maxSessions = 5000;
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var names = (await CachedViewAsync(ct)).Nodes.ToDictionary(node => node.NodeId, node => node.Name);
        var cells = new Dictionary<(Guid, string), long[]>();
        var costs = new Dictionary<(Guid, string), decimal?>();
        var scanned = 0;
        var truncated = false;
        for (var offset = 0; ; offset += pageSize)
        {
            var page = await store.ListSessionsAsync<AiSessionState>(offset, pageSize, ct, includeAssistant: true);
            foreach (var session in page)
                foreach (var turn in session.Turns.Where(t => (t.CompletedAt ?? session.UpdatedAt ?? DateTimeOffset.MinValue) >= since))
                {
                    var key = (turn.ExecutorNodeId ?? Guid.Empty, turn.Selection?.Provider ?? session.Provider);
                    var cell = cells.TryGetValue(key, out var existing) ? existing : cells[key] = new long[5];
                    cell[0]++;
                    if (turn.Usage is not { } usage || usage.InputTokens is null && usage.OutputTokens is null) { cell[4]++; continue; }
                    cell[1] += usage.InputTokens ?? 0;
                    cell[2] += usage.OutputTokens ?? 0;
                    cell[3] += usage.CachedInputTokens ?? 0;
                    if (usage.EstimatedCostUsd is { } cost) costs[key] = (costs.GetValueOrDefault(key) ?? 0) + cost;
                }
            scanned += page.Count;
            if (page.Count < pageSize) break;
            if (scanned >= maxSessions) { truncated = true; break; }
        }
        static AiUsageTotals Totals(long[] c, decimal? cost) => new(c[0], c[1], c[2], c[3], cost, c[4]);
        var nodes = cells.GroupBy(entry => entry.Key.Item1).Select(group =>
        {
            var providers = group.OrderBy(entry => entry.Key.Item2, StringComparer.Ordinal)
                .Select(entry => new AiProviderUsage(entry.Key.Item2, Totals(entry.Value, costs.GetValueOrDefault(entry.Key)))).ToArray();
            var total = new long[5];
            foreach (var entry in group) for (var i = 0; i < 5; i++) total[i] += entry.Value[i];
            var nodeCosts = group.Select(entry => costs.GetValueOrDefault(entry.Key)).Where(c => c is not null).ToArray();
            var id = group.Key == Guid.Empty ? (Guid?)null : group.Key;
            return new AiWorkerUsage(id, id is { } known && names.TryGetValue(known, out var name) ? name : id is null ? "Worker non enregistré" : "Worker retiré",
                providers, Totals(total, nodeCosts.Length == 0 ? null : nodeCosts.Sum()));
        }).OrderByDescending(node => node.Total.InputTokens + node.Total.OutputTokens).ThenBy(node => node.Name, StringComparer.Ordinal).ToArray();
        var grand = new long[5];
        foreach (var node in nodes) { grand[0] += node.Total.Turns; grand[1] += node.Total.InputTokens; grand[2] += node.Total.OutputTokens;
            grand[3] += node.Total.CachedInputTokens; grand[4] += node.Total.TurnsWithoutUsage; }
        var allCosts = nodes.Select(node => node.Total.CostUsd).Where(c => c is not null).ToArray();
        return new(days, since, nodes, Totals(grand, allCosts.Length == 0 ? null : allCosts.Sum()), truncated);
    }
    private static AiUsage? SumUsage(IReadOnlyList<AiSessionTurn> turns)
    {
        if (turns.Count == 0 || turns.All(t => t.Usage is null)) return null;
        return new(
            turns.All(t => t.Usage?.InputTokens is not null) ? turns.Sum(t => t.Usage!.InputTokens!.Value) : null,
            turns.All(t => t.Usage?.OutputTokens is not null) ? turns.Sum(t => t.Usage!.OutputTokens!.Value) : null,
            turns.All(t => t.Usage?.CachedInputTokens is not null) ? turns.Sum(t => t.Usage!.CachedInputTokens!.Value) : null,
            turns.All(t => t.Usage?.EstimatedCostUsd is not null) ? turns.Sum(t => t.Usage!.EstimatedCostUsd!.Value) : null);
    }
    // Doit couvrir le contexte maximal que l'assistant accepte (message + fichiers joints, 1 200 000 caractères) et le prompt système.
    public const int MaximumPromptCharacters = 1_300_000;
    public async Task<AiCompletionResult> GenerateAsync(string? provider, AiPrompt prompt, CancellationToken ct, bool localOnly = false, bool allowModelChanges = false)
    {
        provider ??= (await GetSettingsAsync(ct)).DefaultProvider;
        if (provider is not ("codex-cli" or "claude-cli" or "ollama" or "openai" or "anthropic")) throw new ArgumentException("Fournisseur IA inconnu.");
        if (localOnly && provider != "ollama") return new(false, "", "Le mode local uniquement exige Ollama.");
        if (localOnly && (!Uri.TryCreate(settings.GetSettings().OllamaBaseUrl, UriKind.Absolute, out var localUri) || !localUri.IsLoopback))
            return new(false, "", "Ollama doit être configuré sur une adresse de boucle locale pour cette politique.");
        var requestId = prompt.RequestId ?? Guid.NewGuid();
        var id = prompt.SessionId ?? $"{identity.PeerId:N}.{requestId:N}";
        if (requestId == Guid.Empty || string.IsNullOrWhiteSpace(prompt.UserPrompt) || prompt.UserPrompt.Length + prompt.SystemPrompt.Length > MaximumPromptCharacters)
            throw new ArgumentException("Demande IA invalide ou trop volumineuse.");
        if (prompt.Scope is { } scope && (string.IsNullOrWhiteSpace(scope.Operation) || scope.Operation.Length > 80
            || new[] { scope.LibraryId, scope.FlowId, scope.MediaId, scope.ExecutionId }.Any(value => value == Guid.Empty)))
            throw new ArgumentException("Périmètre de session IA invalide.");
        // Une session préparée par un autre serveur (ancienne installation) est reprise par le central si son historique est présent.
        if (await OwnerAsync(id, ct) != identity.PeerId && !await TryTakeOverAsync(id, ct))
            return new(false, "", "Session introuvable sur le central.", SessionId: id, RequestId: requestId);
        var gate = gates.GetOrAdd(id, _ => new(1));
        if (!await gate.WaitAsync(0, ct)) return new(false, "", "Une demande est déjà en cours dans cette session.", SessionId: id, RequestId: requestId);
        try
        {
            var session = await GetSessionAsync(id, ct) ?? new(id, provider, prompt.SystemPrompt, [], Model: prompt.Model, Scope: prompt.Scope, ReasoningEffort: prompt.ReasoningEffort);
            if (session.SystemPrompt != prompt.SystemPrompt || session.Scope != prompt.Scope
                || !allowModelChanges && (session.Provider != provider || session.Model != prompt.Model || session.ReasoningEffort != prompt.ReasoningEffort))
                throw new ArgumentException("La configuration de cette session ne peut pas être modifiée.");
            var selection = new AiTurnModelSelection(provider, prompt.Model, prompt.ReasoningEffort, prompt.NodeId);
            var completed = session.Turns.FirstOrDefault(t => t.RequestId == requestId);
            if (completed is not null && (completed.UserPrompt != prompt.UserPrompt || completed.Selection is { } oldSelection && oldSelection != selection))
                throw new ArgumentException("Identifiant de requête déjà utilisé pour une autre demande ou configuration.");
            if (completed is not null) return new(true, completed.Content, SessionId: id, RequestId: requestId, Usage: completed.Usage, ExecutorNodeId: completed.ExecutorNodeId);
            if (session.PendingRequest is { } pending && pending != requestId)
                return new(false, "", "Reprenez d’abord la demande interrompue avec son identifiant.", SessionId: id, RequestId: pending);
            if (session.PendingPrompt is { } saved && (saved.UserPrompt != prompt.UserPrompt || saved.Model != prompt.Model
                || saved.ReasoningEffort != prompt.ReasoningEffort || saved.NodeId != prompt.NodeId || session.Provider != provider))
                throw new ArgumentException("La demande en attente conserve son message, son modèle et son raisonnement.");
            if ((await ModelPolicyAsync(ct)).DisabledModels.Any(m => m.Provider == provider && m.Model == prompt.Model))
                throw new ArgumentException("Ce modèle est désactivé dans le central.");
            // A restarted coordinator must wait out the previous executor's hard deadline before issuing another attempt.
            if (session.AttemptDeadline is { } previousDeadline && previousDeadline.AddSeconds(5) > DateTimeOffset.UtcNow)
                return new(false, "", "La tentative précédente peut encore être active. Réessayez après expiration de son bail.", SessionId: id, RequestId: requestId);
            var context = string.Join("\n\n", session.Turns.Select(t => "Utilisateur :\n" + t.UserPrompt + "\nAssistant :\n" + t.Content));
            var executionPrompt = prompt with { UserPrompt = context + "\n\nUtilisateur :\n" + prompt.UserPrompt, SessionId = null, RequestId = null };
            if (executionPrompt.UserPrompt.Length > 240000) return new(false, "", "Contexte maximal atteint. Ouvrez une nouvelle session.", SessionId: id, RequestId: requestId);
            session = session with { Provider = provider, Model = prompt.Model, ReasoningEffort = prompt.ReasoningEffort, PendingRequest = requestId,
                PendingPrompt = prompt with { SessionId = id, RequestId = requestId }, UpdatedAt = DateTimeOffset.UtcNow, LastError = null };
            await store.SaveAsync("session/" + id, session, ct);
            var candidates = new List<AiWorkerAvailability> { await LocalAsync(ct) };
            AiCompletionResult result = new(false, "", "Aucun worker connecté à ce fournisseur. La demande reste en attente.");
            foreach (var candidate in candidates.Where(node => Supports(node, provider, prompt)).Take(localOnly ? 1 : 3))
            {
                var seconds = Math.Clamp((await GetSettingsAsync(ct)).TimeoutSeconds, 10, 600);
                var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
                session = session with { AttemptDeadline = deadline, Attempt = session.Attempt + 1 };
                await store.SaveAsync("session/" + id, session, ct);
                var attempt = new AiExecuteRequest(Guid.NewGuid(), provider, executionPrompt, deadline);
                var ambiguous = false;
                try
                {
                    result = await ExecuteAsync(attempt, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                { ambiguous = true; result = new(false, "", "Le worker d’exécution n’a pas confirmé de résultat."); }
                if (result.Success)
                {
                    session = session with { Turns = [.. session.Turns, new(requestId, prompt.UserPrompt, result.Content, result.Usage, DateTimeOffset.UtcNow,
                        selection, result.ExecutorNodeId)], PendingRequest = null, PendingPrompt = null, AttemptDeadline = null,
                        UpdatedAt = DateTimeOffset.UtcNow, LastError = null };
                    await store.SaveAsync("session/" + id, session, ct);
                    return result with { SessionId = id, RequestId = requestId };
                }
                // Never issue concurrent attempts after an ambiguous transport failure.
                var wait = deadline.AddSeconds(5) - DateTimeOffset.UtcNow;
                if (ambiguous && wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            }
            await store.SaveAsync("session/" + id, session with { AttemptDeadline = null, UpdatedAt = DateTimeOffset.UtcNow, LastError = result.ErrorMessage }, ct);
            return result with { SessionId = id, RequestId = requestId };
        }
        finally { gate.Release(); }
    }
    private async Task<AiCompletionResult> ExecuteAsync(AiExecuteRequest request, CancellationToken ct)
    {
        if (request.Id == Guid.Empty || request.Deadline <= DateTimeOffset.UtcNow || request.Deadline > DateTimeOffset.UtcNow.AddSeconds(605))
            return new(false, "", "Bail IA expiré ou invalide.");
        // Local durable claim prevents replay, including after restart. A lost result is never silently re-executed under the same attempt ID.
        return await running.GetOrAdd(request.Id, _ => new Lazy<Task<AiCompletionResult>>(() => RunClaimedAsync(request, ct))).Value;
    }
    private async Task<AiCompletionResult> RunClaimedAsync(AiExecuteRequest request, CancellationToken ct)
    {
        var directory = Path.Combine(AiCliRuntime.ResolveRoot(configuration), "attempts");
        Directory.CreateDirectory(directory);
        try { using var claim = new FileStream(Path.Combine(directory, request.Id.ToString("N")), FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch (IOException) { return new(false, "", "Tentative déjà reçue ; reprenez depuis le dernier résultat validé."); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(request.Deadline - DateTimeOffset.UtcNow);
        try
        {
            var localSettings = await GetSettingsAsync(deadline.Token);
            if (request.Provider == "codex-cli" && !localSettings.CodexEnabled
                || request.Provider == "claude-cli" && !localSettings.ClaudeEnabled)
                return new(false, "", "Ce fournisseur est désactivé dans le central.");
            var local = await LocalAsync(deadline.Token);
            if (!Supports(local, request.Provider, request.Prompt)) return new(false, "", "Modèle ou fournisseur indisponible sur ce worker.");
            var effectiveModel = request.Prompt.Model ?? local.Models?.GetValueOrDefault(request.Provider)
                ?? local.Catalogs?.FirstOrDefault(c => c.Provider == request.Provider && c.State == "available")?.Models.FirstOrDefault(m => m.IsDefault)?.Id;
            if ((await ModelPolicyAsync(deadline.Token)).DisabledModels.Any(m => m.Provider == request.Provider && m.Model == effectiveModel))
                return new(false, "", "Ce modèle est désactivé dans le central.");
            var shared = await GetSettingsAsync(deadline.Token);
            if (request.Provider == "codex-cli" && !shared.CodexEnabled
                || request.Provider == "claude-cli" && !shared.ClaudeEnabled)
                return new(false, "", "Ce fournisseur est désactivé dans le central.");
            settings.UpdateSettings(new(DefaultProvider: shared.DefaultProvider, TimeoutSeconds: shared.TimeoutSeconds,
                SubtitleBatchSize: shared.SubtitleBatchSize, CodexEnabled: shared.CodexEnabled, ClaudeEnabled: shared.ClaudeEnabled));
            return (await factory.CreateLocalClient(request.Provider).GenerateTextAsync(request.Prompt, deadline.Token)) with { ExecutorNodeId = identity.PeerId };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "", "Bail d’exécution IA expiré."); }
    }
    public async Task<AiSessionState> ForkSessionAsync(string id, CancellationToken ct)
    {
        var saved = await GetSessionAsync(id, ct) ?? throw new ArgumentException("Checkpoint encore indisponible sur ce worker. Réessayez après réconciliation.");
        if (saved.AttemptDeadline is { } deadline && deadline.AddSeconds(5) > DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Attendez la fin du bail de la tentative précédente avant de créer une nouvelle session.");
        var fork = saved with { Id = $"{identity.PeerId:N}.{Guid.NewGuid():N}", PendingRequest = null, PendingPrompt = null, AttemptDeadline = null,
            Attempt = 0, UpdatedAt = DateTimeOffset.UtcNow, LastError = null };
        await store.SaveAsync("session/" + fork.Id, fork, ct);
        return fork;
    }
    /// <summary>Coordinateur courant de la session (reprise comprise) ; null si l'identifiant n'est pas celui d'une session.</summary>
    public async Task<Guid?> CoordinatorAsync(string? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try { return await OwnerAsync(id, ct); }
        catch (ArgumentException) { return null; }
    }
    /// <summary>
    /// Fait de ce worker le coordinateur d'une session dont le coordinateur est injoignable. Refuse sans checkpoint validé présent
    /// localement, pour ne jamais repartir d'un contexte vide ; l'appelant doit avoir constaté que le coordinateur ne répond pas.
    /// </summary>
    public async Task<bool> TryTakeOverAsync(string id, CancellationToken ct)
    {
        var previous = await OwnerAsync(id, ct);
        if (previous == identity.PeerId) return true;
        if (await GetSessionAsync(id, ct) is null) return false;
        await store.SaveAsync(CoordinatorKey(id), identity.PeerId.ToString("N"), ct);
        services?.GetService<ILogger<AiCentralService>>()?.LogWarning("Session IA {Session} reprise par ce worker : son coordinateur {Coordinator} est injoignable.", id, previous);
        return true;
    }
    /// <summary>Une session auxiliaire (titre, planification) créée par le coordinateur courant le suit, même après une reprise.</summary>
    public async Task AdoptSessionAsync(string id, CancellationToken ct)
    {
        if (await OwnerAsync(id, ct) != identity.PeerId) await store.SaveAsync(CoordinatorKey(id), identity.PeerId.ToString("N"), ct);
    }
    public static string CoordinatorKey(string id) => "session-coordinator/" + id;
    /// <summary>Coordinateur de la session : celui de son identifiant, sauf reprise explicite par un autre worker (la plus récente gagne).</summary>
    private async Task<Guid> OwnerAsync(string id, CancellationToken ct) =>
        await store.ReadAsync<string>(CoordinatorKey(id), ct) is { } taken && Guid.TryParseExact(taken, "N", out var node) && node != Guid.Empty ? node : Owner(id);
    private static Guid Owner(string id)
    {
        var parts = id.Split('.');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var owner) || owner == Guid.Empty || !Guid.TryParseExact(parts[1], "N", out _))
            throw new ArgumentException("Identifiant de session invalide.");
        return owner;
    }
    public async Task<AiSettingsDto> GetSettingsAsync(CancellationToken ct)
    {
        var shared = await store.ReadAsync<AiSettingsDto>("settings", ct);
        var local = settings.GetSettings();
        return shared is null ? local : local with
        {
            DefaultProvider = shared.DefaultProvider,
            DefaultModel = shared.DefaultModel,
            DefaultReasoningEffort = shared.DefaultReasoningEffort,
            TimeoutSeconds = shared.TimeoutSeconds,
            SubtitleBatchSize = shared.SubtitleBatchSize,
            AssistantProvider = shared.AssistantProvider,
            AssistantModel = shared.AssistantModel,
            AssistantReasoningEffort = shared.AssistantReasoningEffort,
            FlowProvider = shared.FlowProvider,
            FlowModel = shared.FlowModel,
            FlowReasoningEffort = shared.FlowReasoningEffort,
            AssistantLocalOnly = shared.AssistantLocalOnly,
            FlowLocalOnly = shared.FlowLocalOnly,
            AssistantConversationRetentionDays = shared.AssistantConversationRetentionDays,
            AssistantActivityRetentionDays = shared.AssistantActivityRetentionDays,
            OllamaEnabled = shared.OllamaEnabled,
            OpenAiEnabled = shared.OpenAiEnabled,
            AnthropicEnabled = shared.AnthropicEnabled,
            CodexEnabled = shared.CodexEnabled,
            ClaudeEnabled = shared.ClaudeEnabled
        };
    }
    public async Task<AiSettingsDto> UpdateSettingsAsync(AiSettingsUpdateRequest update, CancellationToken ct)
    {
        var saved = settings.UpdateSettings(update);
        await store.SaveAsync("settings", saved with { OpenAiApiKey = null, AnthropicApiKey = null }, ct);
        return saved;
    }
}
