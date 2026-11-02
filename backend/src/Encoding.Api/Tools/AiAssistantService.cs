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

/// <summary>Thin façade for assistant endpoints; work lives in Conversations/Send/Actions/Support.</summary>
public sealed class AiAssistantService(
    AiAssistantSupportService support,
    AiAssistantConversationsService conversations,
    AiAssistantSendService send,
    AiAssistantActionsService actions)
{
    public static AiAssistantService Create(
        AiClusterStore store,
        AiClusterService cluster,
        AiClusterTransport transport,
        FlowRepository flows,
        ExecutionRepository executions,
        LibraryWorkflowRepository libraryWorkflows,
        LibraryRepository libraries,
        NodeRepository nodes,
        NodePresenceOptions nodePresence,
        McpService mcp,
        IDataProtectionProvider protection,
        FlowTemplateRepository? flowTemplates = null)
    {
        var support = new AiAssistantSupportService(store, cluster, transport, flows, executions, libraryWorkflows,
            libraries, nodes, nodePresence, mcp, protection, flowTemplates);
        var conversations = new AiAssistantConversationsService(support);
        var actions = new AiAssistantActionsService(support, conversations);
        var send = new AiAssistantSendService(support, actions);
        return new AiAssistantService(support, conversations, send, actions);
    }

    public Task<AiAssistantOptions> OptionsAsync(CancellationToken ct, bool refresh = false) => support.OptionsAsync(ct, refresh);
    public Task<int> PurgeExpiredAsync(CancellationToken ct) => support.PurgeExpiredAsync(ct);
    public Task<bool> IsAssistantConversationAsync(string id, CancellationToken ct) => support.IsAssistantConversationAsync(id, ct);
    public Task<AiAssistantConversation?> RenameAsync(string id, string? title, ClaimsPrincipal user, CancellationToken ct) => support.RenameAsync(id, title, user, ct);

    public Task<IReadOnlyList<AiAssistantConversation>> ListAsync(ClaimsPrincipal user, CancellationToken ct) => conversations.ListAsync(user, ct);
    public Task<IReadOnlyList<AiAssistantConversationSearchResult>> SearchAsync(string? q, ClaimsPrincipal user, CancellationToken ct) => conversations.SearchAsync(q, user, ct);
    public Task<bool> DeleteConversationAsync(string id, ClaimsPrincipal user, CancellationToken ct) => conversations.DeleteConversationAsync(id, user, ct);
    public Task<IReadOnlyList<AiAssistantActivity>> ActivityAsync(ClaimsPrincipal user, CancellationToken ct) => conversations.ActivityAsync(user, ct);
    public Task<AiAssistantActivity?> GetActivityAsync(Guid requestId, ClaimsPrincipal user, CancellationToken ct) => conversations.GetActivityAsync(requestId, user, ct);
    public Task<Guid?> GetRequestCoordinatorIdAsync(Guid requestId, ClaimsPrincipal user, CancellationToken ct) => conversations.GetRequestCoordinatorIdAsync(requestId, user, ct);
    public Task RecordOperationActivityAsync(
        ClaimsPrincipal user, Guid requestId, string operation, string provider, string status,
        DateTimeOffset startedAt, string? errorCategory, CancellationToken ct) =>
        conversations.RecordOperationActivityAsync(user, requestId, operation, provider, status, startedAt, errorCategory, ct);
    public bool CancelRequest(Guid requestId, ClaimsPrincipal user) => conversations.CancelRequest(requestId, user);
    public Task<bool> CancelRequestAsync(Guid requestId, ClaimsPrincipal user, CancellationToken ct) => conversations.CancelRequestAsync(requestId, user, ct);
    public Task<bool> UpdateFlowProposalActivityAsync(Guid requestId, string decision, ClaimsPrincipal user, CancellationToken ct) =>
        conversations.UpdateFlowProposalActivityAsync(requestId, decision, user, ct);
    public Task<AiAssistantConversationDetail?> GetAsync(string id, ClaimsPrincipal user, CancellationToken ct) => conversations.GetAsync(id, user, ct);
    public Task<AiAssistantConversation?> UpdateSourcesAsync(string id, AiAssistantSourcesRequest request, ClaimsPrincipal user, CancellationToken ct) =>
        conversations.UpdateSourcesAsync(id, request, user, ct);

    public Task<AiAssistantMessageResponse?> SendAsync(AiAssistantMessageRequest request, ClaimsPrincipal user, CancellationToken ct) => send.SendAsync(request, user, ct);

    public Task<AiAssistantPreparedAction?> PrepareDeleteFlowAsync(
        AiAssistantPrepareActionRequest request, ClaimsPrincipal user, CancellationToken ct)
        => actions.PrepareDeleteFlowAsync(request, user, ct);
    public Task<AiAssistantPreparedAction?> PreparePublishFlowAsync(
        AiAssistantPrepareActionRequest request, ClaimsPrincipal user, CancellationToken ct)
        => actions.PreparePublishFlowAsync(request, user, ct);
    public Task<AiAssistantPreparedAction?> PrepareStartLibraryAsync(
        AiAssistantPrepareActionRequest request, ClaimsPrincipal user, CancellationToken ct)
        => actions.PrepareStartLibraryAsync(request, user, ct);
    public Task<AiAssistantPreparedAction?> PrepareCancelExecutionAsync(
        AiAssistantPrepareActionRequest request, ClaimsPrincipal user, CancellationToken ct)
        => actions.PrepareCancelExecutionAsync(request, user, ct);
    public Task<AiAssistantPreparedAction?> PrepareAssignLibraryFlowAsync(
        AiAssistantActionCall request, ClaimsPrincipal user, CancellationToken ct)
        => actions.PrepareAssignLibraryFlowAsync(request, user, ct);
    public Task<AiAssistantPreparedAction?> PrepareCreateFlowTemplateAsync(
        AiAssistantActionCall request, ClaimsPrincipal user, CancellationToken ct)
        => actions.PrepareCreateFlowTemplateAsync(request, user, ct);
    public Task<bool> ConfirmDeleteFlowAsync(AiAssistantConfirmActionRequest request, ClaimsPrincipal user, CancellationToken ct) => actions.ConfirmDeleteFlowAsync(request, user, ct);
    public Task<bool> ConfirmPublishFlowAsync(AiAssistantConfirmActionRequest request, ClaimsPrincipal user, CancellationToken ct) => actions.ConfirmPublishFlowAsync(request, user, ct);
    public Task<bool> ConfirmCancelExecutionAsync(AiAssistantConfirmActionRequest request, ClaimsPrincipal user, CancellationToken ct) => actions.ConfirmCancelExecutionAsync(request, user, ct);
    public Task<bool> ConfirmAssignLibraryFlowAsync(AiAssistantConfirmActionRequest request, ClaimsPrincipal user, CancellationToken ct) => actions.ConfirmAssignLibraryFlowAsync(request, user, ct);
    public Task<bool> ConfirmCreateFlowTemplateAsync(AiAssistantConfirmActionRequest request, ClaimsPrincipal user, CancellationToken ct) => actions.ConfirmCreateFlowTemplateAsync(request, user, ct);
    public Task<bool> ConfirmBusinessOperationAsync(AiAssistantConfirmActionRequest request, ClaimsPrincipal user, CancellationToken ct) => actions.ConfirmBusinessOperationAsync(request, user, ct);
    public Task<(
        int Queued, int Skipped)?> ConfirmStartLibraryAsync(AiAssistantConfirmActionRequest request, ClaimsPrincipal user, CancellationToken ct)
        => actions.ConfirmStartLibraryAsync(request, user, ct);
}

public sealed class AiAssistantRetentionService(
    AiAssistantService assistant,
    ClusterReconciliationRepository journal,
    IConfiguration configuration,
    ILogger<AiAssistantRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var removed = await assistant.PurgeExpiredAsync(stoppingToken);
                var ownUrl = ClusterReconciliationRepository.TryNormalizePeerUrl(configuration["Cluster:Reconciliation:AdvertisedApiBaseUrl"]);
                var configuredPeers = configuration.GetSection("Cluster:Reconciliation:Peers").Get<string[]>() ?? [];
                var peers = configuredPeers.Concat(await journal.GetPeerEndpointsAsync(stoppingToken))
                    .Select(ClusterReconciliationRepository.TryNormalizePeerUrl)
                    .OfType<string>()
                    .Where(url => !string.Equals(url, ownUrl, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var purgedEvents = await journal.PruneAssistantHistoryAsync(peers, DateTimeOffset.UtcNow.AddHours(-24), stoppingToken);
                if (removed > 0 || purgedEvents > 0)
                    logger.LogInformation("La rétention IA a expiré {Count} enregistrements et purgé {Events} anciens événements confirmés par les pairs.", removed, purgedEvents);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "La rétention IA a échoué et sera retentée à la prochaine passe.");
            }

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}

