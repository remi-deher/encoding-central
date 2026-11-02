using System.Security.Claims;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Api.Nodes;
using Encoding.Api.Mcp;
using Encoding.Api.Libraries;
using Encoding.Api.Infrastructure;
using Encoding.Api.Cluster;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Libraries;
using Encoding.Contracts.Nodes;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

public sealed class AiAssistantConversationsService(AiAssistantSupportService support)
{

    public async Task<IReadOnlyList<AiAssistantConversation>> ListAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var ownerId = AiAssistantSupportService.OwnerId(user);
        var conversations = await support.Store.ReadPrefixAsync<StoredConversation>(AiAssistantSupportService.ConversationPrefix + ownerId + "/", ct);
        var page = conversations
            .Select(record => record.Conversation)
            .OrderByDescending(conversation => conversation.UpdatedAt)
            .Take(100)
            .ToArray();
        for (var index = 0; index < page.Length; index++)
            page[index] = page[index] with { CoordinatorId = await support.Cluster.CoordinatorAsync(page[index].Id, ct) };
        return page;
    }

    public async Task<IReadOnlyList<AiAssistantConversationSearchResult>> SearchAsync(
        string? query,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var originalQuery = query?.Trim() ?? string.Empty;
        var normalizedQuery = NormalizeSearchText(originalQuery);
        if (normalizedQuery.Length is < 2 or > 120) return [];

        var ownerId = AiAssistantSupportService.OwnerId(user);
        var records = (await support.Store.ReadPrefixAsync<StoredConversation>(AiAssistantSupportService.ConversationPrefix + ownerId + "/", ct))
            .OrderByDescending(record => record.Conversation.UpdatedAt)
            .Take(100)
            .ToArray();
        var results = new List<AiAssistantConversationSearchResult>(8);

        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            var conversation = record.Conversation;
            var sourceLabel = conversation.Source.Label;
            if (ContainsSearchText(conversation.Title, normalizedQuery)
                || ContainsSearchText(sourceLabel, normalizedQuery)
                || ContainsSearchText(conversation.Provider, normalizedQuery)
                || (conversation.AdditionalSources ?? []).Any(source => ContainsSearchText(source.Label, normalizedQuery)))
            {
                results.Add(new(conversation.Id, conversation.Title, conversation.Provider,
                    sourceLabel, $"Conversation · {sourceLabel} · {conversation.Provider}",
                    null, null, conversation.UpdatedAt));
            }
            else if (await support.Cluster.GetSessionAsync(conversation.Id, ct) is { } session)
            {
                for (var turnIndex = 0; turnIndex < session.Turns.Count; turnIndex++)
                {
                    var turn = session.Turns[turnIndex];
                    var userPrompt = record.UserPrompts?.GetValueOrDefault(turn.RequestId) ?? turn.UserPrompt;
                    var promptMatch = ContainsSearchText(userPrompt, normalizedQuery);
                    var answerMatch = ContainsSearchText(turn.Content, normalizedQuery);
                    if (!promptMatch && !answerMatch) continue;

                    var matchedText = promptMatch ? userPrompt : turn.Content;
                    results.Add(new(conversation.Id, conversation.Title, conversation.Provider,
                        sourceLabel, SearchSnippet(matchedText, originalQuery), turnIndex,
                        promptMatch ? "user" : "assistant", conversation.UpdatedAt));
                    break;
                }
            }

            if (results.Count == 8) break;
        }

        return results;
    }

    public static bool ContainsSearchText(string? value, string normalizedQuery) =>
        value is not null && NormalizeSearchText(value).Contains(normalizedQuery, StringComparison.Ordinal);

    public static string NormalizeSearchText(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var normalized = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                normalized.Append(char.ToLowerInvariant(character));
        }
        return normalized.ToString().Normalize(NormalizationForm.FormC);
    }

    public static string SearchSnippet(string value, string query)
    {
        var index = NormalizeSearchText(value).IndexOf(NormalizeSearchText(query), StringComparison.Ordinal);
        if (index < 0) index = 0;
        var start = Math.Max(0, index - 48);
        var length = Math.Min(140, value.Length - start);
        var snippet = value.Substring(start, length).Trim();
        return $"{(start > 0 ? "…" : string.Empty)}{snippet}{(start + length < value.Length ? "…" : string.Empty)}";
    }

    public async Task<bool> DeleteConversationAsync(string id, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!AiAssistantSupportService.IsSessionId(id)) return false;
        var ownerId = AiAssistantSupportService.OwnerId(user);
        var stored = await support.ReadConversationAsync(ownerId, id, ct);
        if (stored is null) return false;
        var session = await support.Cluster.GetSessionAsync(id, ct);
        if (session?.PendingRequest is not null)
            throw new InvalidOperationException("Terminez ou reprenez la demande en cours avant de supprimer cette conversation.");

        await DeleteStoredConversationAsync(stored, ct);
        return true;
    }

    public async Task DeleteStoredConversationAsync(StoredConversation stored, CancellationToken ct)
    {
        await support.Store.DeleteAsync(AiAssistantSupportService.ConversationKey(stored.OwnerId, stored.Conversation.Id), ct);
        await support.Store.DeleteAsync(AiAssistantSupportService.SessionOwnerKey(stored.Conversation.Id), ct);
        await support.Store.DeleteAsync("session/" + stored.Conversation.Id, ct);
            await support.Store.DeleteAsync(AiClusterService.CoordinatorKey(stored.Conversation.Id), ct);
        for (var attempt = 1; attempt <= AiAssistantSupportService.MaxTitleAttemptTurns; attempt++)
        {
            var titleSessionId = $"{stored.Conversation.Id.Split('.')[0]}.{AiAssistantSupportService.TitleRequestId(stored.Conversation.Id, attempt):N}";
            await support.Store.DeleteAsync(AiAssistantSupportService.SessionOwnerKey(titleSessionId), ct);
            await support.DeleteHelperSessionAsync(titleSessionId, ct);
        }
        foreach (var requestId in stored.TurnPromptSnapshots?.Keys ?? Array.Empty<Guid>())
        {
            var plannerRequestId = AiAssistantSupportService.DeriveRequestId(
                stored.Conversation.Id + "/" + requestId.ToString("N"), "assistant-read-tool-planner");
            await support.DeleteHelperSessionAsync($"{stored.Conversation.Id.Split('.')[0]}.{plannerRequestId:N}", ct);
        }
    }

    public async Task<IReadOnlyList<AiAssistantActivity>> ActivityAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var ownerId = AiAssistantSupportService.OwnerId(user);
        var records = await support.Store.ReadPrefixAsync<StoredActivity>(AiAssistantSupportService.ActivityPrefix + ownerId + "/", ct);
        var titles = (await support.Store.ReadPrefixAsync<StoredConversation>(AiAssistantSupportService.ConversationPrefix + ownerId + "/", ct))
            .ToDictionary(record => record.Conversation.Id, record => record.Conversation.Title, StringComparer.Ordinal);
        return records.Select(record => WithTitle(record.Activity, titles))
            .OrderByDescending(activity => activity.UpdatedAt)
            .Take(100)
            .ToArray();
    }

    public static AiAssistantActivity WithTitle(AiAssistantActivity activity, IReadOnlyDictionary<string, string> titles) =>
        activity.ConversationId is { } id && titles.TryGetValue(id, out var title) ? activity with { ConversationTitle = title } : activity;

    public async Task<AiAssistantActivity?> GetActivityAsync(Guid requestId, ClaimsPrincipal user, CancellationToken ct)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("Identifiant de demande invalide.");
        var ownerId = AiAssistantSupportService.OwnerId(user);
        var activity = await ReadActivityAsync(ownerId, requestId, ct);
        if (activity?.ConversationId is { } id && AiAssistantSupportService.IsSessionId(id) && await support.ReadConversationAsync(ownerId, id, ct) is { } stored)
            activity = activity with { ConversationTitle = stored.Conversation.Title };
        return activity;
    }

    public async Task<Guid?> GetRequestCoordinatorIdAsync(Guid requestId, ClaimsPrincipal user, CancellationToken ct)
    {
        if (requestId == Guid.Empty) return null;
        var activity = await ReadActivityAsync(AiAssistantSupportService.OwnerId(user), requestId, ct);
        return await support.Cluster.CoordinatorAsync(activity?.ConversationId, ct);
    }

    public async Task<AiAssistantActivity?> ReadActivityAsync(string ownerId, Guid requestId, CancellationToken ct) =>
        (await support.Store.ReadAsync<StoredActivity>(AiAssistantSupportService.ActivityPrefix + ownerId + "/" + requestId.ToString("N"), ct))?.Activity;

    public async Task RecordOperationActivityAsync(
        ClaimsPrincipal user,
        Guid requestId,
        string operation,
        string provider,
        string status,
        DateTimeOffset startedAt,
        string? errorCategory,
        CancellationToken ct)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("Identifiant de demande invalide.");
        var updatedAt = DateTimeOffset.UtcNow;
        var activity = new AiAssistantActivity(requestId, null, provider, status, startedAt, updatedAt,
            status == "running" ? null : (long)(updatedAt - startedAt).TotalMilliseconds,
            errorCategory, operation);
        await support.SaveActivityAsync(AiAssistantSupportService.OwnerId(user), activity, ct);
    }

    public bool CancelRequest(Guid requestId, ClaimsPrincipal user)
    {
        if (requestId == Guid.Empty) return false;
        var key = (AiAssistantSupportService.OwnerId(user), requestId);
        if (!support.activeRequests.TryGetValue(key, out var cancellation)) return false;
        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException) { return false; }
    }

    public Task<bool> CancelRequestAsync(Guid requestId, ClaimsPrincipal user, CancellationToken ct) =>
        Task.FromResult(CancelRequest(requestId, user));

    public async Task<bool> UpdateFlowProposalActivityAsync(
        Guid requestId,
        string decision,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (decision is not ("applied-to-editor" or "rejected"))
            throw new ArgumentException("Décision de proposition invalide.");
        var ownerId = AiAssistantSupportService.OwnerId(user);
        var key = AiAssistantSupportService.ActivityPrefix + ownerId + "/" + requestId.ToString("N");
        var stored = await support.Store.ReadAsync<StoredActivity>(key, ct);
        if (stored is null || stored.Activity.Operation is not ("flow-generation" or "flow-refactor")) return false;
        if (stored.Activity.Status == decision) return true;
        if (stored.Activity.Status != "proposal-ready") return false;
        await support.SaveActivityAsync(ownerId, stored.Activity with { Status = decision, UpdatedAt = DateTimeOffset.UtcNow }, ct);
        return true;
    }

    public async Task<AiAssistantConversationDetail?> GetAsync(string id, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!AiAssistantSupportService.IsSessionId(id)) return null;
        var stored = await support.ReadConversationAsync(ownerId: AiAssistantSupportService.OwnerId(user), id, ct);
        if (stored is null) return null;
        var conversation = stored.Conversation with { CoordinatorId = await support.Cluster.CoordinatorAsync(id, ct) };

        var session = await support.Cluster.GetSessionAsync(id, ct);
        var turns = session?.Turns.Select(turn => new AiAssistantTurn(
            turn.RequestId, stored.UserPrompts?.GetValueOrDefault(turn.RequestId) ?? turn.UserPrompt, turn.Content,
            stored.TurnSources?.GetValueOrDefault(turn.RequestId) ?? [conversation.Source],
            stored.TurnSelections?.GetValueOrDefault(turn.RequestId) ?? turn.Selection,
            stored.TurnActualModels?.GetValueOrDefault(turn.RequestId) ?? turn.Usage?.Model,
            turn.ExecutorNodeId)).ToArray()
            ?? Array.Empty<AiAssistantTurn>();
        var pending = session?.PendingRequest is { } requestId && session.PendingPrompt is { } prompt
            ? new AiAssistantPendingTurn(requestId, stored.UserPrompts?.GetValueOrDefault(requestId) ?? prompt.UserPrompt,
                session.AttemptDeadline is { } deadline && deadline > DateTimeOffset.UtcNow)
            : null;
        return new(conversation, turns, pending);
    }

    public async Task<AiAssistantConversation?> UpdateSourcesAsync(
        string id,
        AiAssistantSourcesRequest request,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (!AiAssistantSupportService.IsSessionId(id)) return null;
        var ownerId = AiAssistantSupportService.OwnerId(user);
        var stored = await support.ReadConversationAsync(ownerId, id, ct);
        if (stored is null) return null;
        var session = await support.Cluster.GetSessionAsync(id, ct);
        if (session?.PendingRequest is not null)
            throw new InvalidOperationException("Reprenez ou terminez la demande en cours avant de modifier ses ressources.");
        if (session is not null && session.SystemPrompt != AiAssistantSupportService.BuildSystemPrompt())
            throw new InvalidOperationException("Cette ancienne conversation conserve son contexte initial. Créez une nouvelle conversation pour modifier ses ressources.");
        if (request.Sources is null || request.Sources.Count is < 1 or > 5)
            throw new ArgumentException("Une conversation doit conserver entre une et cinq ressources.");
        if (request.Sources.Any(source => source.Id == Guid.Empty)
            || request.Sources.Select(source => (source.Type, source.Id, source.Version)).Distinct().Count() != request.Sources.Count)
            throw new ArgumentException("Les ressources doivent être valides et sans doublon.");

        var resolved = new List<AiAssistantSource>(request.Sources.Count);
        foreach (var reference in request.Sources)
        {
            var context = await support.ResolveContextAsync(reference.Type, reference.Id, reference.Version, ct);
            if (context is null) return null;
            resolved.Add(context.Source);
        }

        var conversation = stored.Conversation with
        {
            Source = resolved[0],
            AdditionalSources = resolved.Skip(1).ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await support.SaveConversationAsync(ownerId, conversation, ct);
        return conversation;
    }

}
