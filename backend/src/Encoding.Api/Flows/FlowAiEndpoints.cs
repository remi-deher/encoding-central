using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Encoding.Contracts.Flows;
using Encoding.Api.Tools;
using Encoding.Contracts.Tools;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Encoding.Api.Flows;

public static class FlowAiEndpoints
{
    public static void MapFlowAiEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/flows/ai").WithTags("Flow AI Assistant");

        group.MapPost("/generate", async (FlowAiGenerateRequest request, FlowAiGeneratorService service, AiAssistantService activity, AiClusterService cluster,
            ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.UserPrompt))
            {
                return Results.BadRequest(new { message = "Le prompt utilisateur est obligatoire." });
            }
            var requestId = request.RequestId ?? Guid.NewGuid();
            var settings = await cluster.GetSettingsAsync(ct);
            var provider = settings.FlowProvider ?? settings.DefaultProvider;
            var selection = await ResolveModelSelectionAsync(settings, provider, cluster, ct);
            request = request with { Provider = provider, Model = selection.Model, ReasoningEffort = selection.ReasoningEffort };
            var startedAt = DateTimeOffset.UtcNow;
            await activity.RecordOperationActivityAsync(user, requestId, "flow-generation", provider, "running", startedAt, null, ct);
            try
            {
                var response = await service.GenerateFlowAsync(request with { RequestId = requestId }, ct, settings.FlowLocalOnly);
                await activity.RecordOperationActivityAsync(user, requestId, "flow-generation", provider, response.Success ? "proposal-ready" : "failed", startedAt,
                    response.Success ? null : "invalid-result", ct);
                return Results.Ok(response);
            }
            catch
            {
                await activity.RecordOperationActivityAsync(user, requestId, "flow-generation", provider, "failed", startedAt, "provider-error", CancellationToken.None);
                throw;
            }
        }).WithName("GenerateFlowWithAi");

        group.MapPost("/refactor", async (FlowAiRefactorRequest request, FlowAiGeneratorService service, AiAssistantService activity, AiClusterService cluster,
            ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (request.CurrentFlow is null || string.IsNullOrWhiteSpace(request.UserPrompt))
            {
                return Results.BadRequest(new { message = "Le flow courant et le prompt sont obligatoires." });
            }
            var requestId = request.RequestId ?? Guid.NewGuid();
            var settings = await cluster.GetSettingsAsync(ct);
            var provider = settings.FlowProvider ?? settings.DefaultProvider;
            var selection = await ResolveModelSelectionAsync(settings, provider, cluster, ct);
            request = request with { Provider = provider, Model = selection.Model, ReasoningEffort = selection.ReasoningEffort };
            var startedAt = DateTimeOffset.UtcNow;
            await activity.RecordOperationActivityAsync(user, requestId, "flow-refactor", provider, "running", startedAt, null, ct);
            try
            {
                var response = await service.RefactorFlowAsync(request with { RequestId = requestId }, ct, settings.FlowLocalOnly);
                await activity.RecordOperationActivityAsync(user, requestId, "flow-refactor", provider, response.Success ? "proposal-ready" : "failed", startedAt,
                    response.Success ? null : "invalid-result", ct);
                return Results.Ok(response);
            }
            catch
            {
                await activity.RecordOperationActivityAsync(user, requestId, "flow-refactor", provider, "failed", startedAt, "provider-error", CancellationToken.None);
                throw;
            }
        }).WithName("RefactorFlowWithAi");

        group.MapPost("/{id:guid}/draft-refactor", async (Guid id, FlowAiDraftRefactorRequest request,
            FlowRepository flows, FlowAiGeneratorService service, AiAssistantService activity,
            AiClusterService cluster, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (request.ExpectedDraftRevision < 1 || string.IsNullOrWhiteSpace(request.UserPrompt))
                return Results.BadRequest(new { message = "La révision du brouillon et l’instruction sont obligatoires." });
            var draft = await flows.DraftAsync(id, ct);
            if (draft is null) return Results.NotFound(new { message = "Aucun brouillon n’est disponible pour ce flow." });
            if (draft.DraftRevision != request.ExpectedDraftRevision)
                return Results.Conflict(new { message = "Le brouillon a changé. Rechargez-le avant de proposer une modification." });

            var requestId = request.RequestId ?? Guid.NewGuid();
            var settings = await cluster.GetSettingsAsync(ct);
            var provider = settings.FlowProvider ?? request.Provider ?? settings.DefaultProvider;
            var startedAt = DateTimeOffset.UtcNow;
            await activity.RecordOperationActivityAsync(user, requestId, "flow-refactor", provider, "running", startedAt, null, ct);
            try
            {
                var result = await service.RefactorFlowAsync(
                    new FlowAiRefactorRequest(draft.Definition, request.UserPrompt, provider, request.SessionId, requestId),
                    ct, settings.FlowLocalOnly);
                if (!result.Success || result.Flow is null)
                {
                    await activity.RecordOperationActivityAsync(user, requestId, "flow-refactor", provider, "failed", startedAt, "invalid-result", ct);
                    return Results.Ok(result);
                }
                var proposal = new FlowAiDraftRefactorProposal(id, draft.DraftRevision, draft.Definition,
                    result.Flow, BuildChanges(draft.Definition, result.Flow), result.Explanation);
                await activity.RecordOperationActivityAsync(user, requestId, "flow-refactor", provider, "proposal-ready", startedAt, null, ct);
                return Results.Ok(proposal);
            }
            catch
            {
                await activity.RecordOperationActivityAsync(user, requestId, "flow-refactor", provider, "failed", startedAt, "provider-error", CancellationToken.None);
                throw;
            }
        }).WithName("ProposeFlowDraftRefactor");

        group.MapPost("/explain", async (FlowAiExplainRequest request, FlowAiGeneratorService service, AiAssistantService activity, AiClusterService cluster,
            ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (request.Flow is null)
            {
                return Results.BadRequest(new { message = "Le flow à expliquer est obligatoire." });
            }
            var requestId = request.RequestId ?? Guid.NewGuid();
            var settings = await cluster.GetSettingsAsync(ct);
            var provider = settings.FlowProvider ?? settings.DefaultProvider;
            var selection = await ResolveModelSelectionAsync(settings, provider, cluster, ct);
            request = request with { Provider = provider, Model = selection.Model, ReasoningEffort = selection.ReasoningEffort };
            var startedAt = DateTimeOffset.UtcNow;
            await activity.RecordOperationActivityAsync(user, requestId, "flow-explain", provider, "running", startedAt, null, ct);
            try
            {
                var response = await service.ExplainFlowAsync(request with { RequestId = requestId }, ct, settings.FlowLocalOnly);
                await activity.RecordOperationActivityAsync(user, requestId, "flow-explain", provider, response.Success ? "succeeded" : "failed", startedAt,
                    response.Success ? null : "provider-error", ct);
                return Results.Ok(response);
            }
            catch
            {
                await activity.RecordOperationActivityAsync(user, requestId, "flow-explain", provider, "failed", startedAt, "provider-error", CancellationToken.None);
                throw;
            }
        }).WithName("ExplainFlowWithAi");
    }

    private static IReadOnlyList<FlowAiChange> BuildChanges(FlowDefinition before, FlowDefinition after)
    {
        var changes = new List<FlowAiChange>();
        Compare(JsonSerializer.SerializeToNode(before, FlowRepository.JsonOptions),
            JsonSerializer.SerializeToNode(after, FlowRepository.JsonOptions), "", changes);
        return changes;
    }

    private static void Compare(JsonNode? before, JsonNode? after, string path, List<FlowAiChange> changes)
    {
        if (JsonNode.DeepEquals(before, after)) return;
        if (before is JsonObject oldObject && after is JsonObject newObject)
        {
            foreach (var key in oldObject.Select(pair => pair.Key).Union(newObject.Select(pair => pair.Key), StringComparer.Ordinal))
            {
                var childPath = path + "/" + key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
                if (!oldObject.TryGetPropertyValue(key, out var oldValue))
                    changes.Add(new("add", childPath, null, Element(newObject[key])));
                else if (!newObject.TryGetPropertyValue(key, out var newValue))
                    changes.Add(new("remove", childPath, Element(oldValue), null));
                else Compare(oldValue, newValue, childPath, changes);
            }
            return;
        }
        changes.Add(new("replace", string.IsNullOrEmpty(path) ? "/" : path, Element(before), Element(after)));
    }

    private static System.Text.Json.JsonElement? Element(JsonNode? node) => node is null ? null
        : JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

    private static async Task<(string? Model, string? ReasoningEffort)> ResolveModelSelectionAsync(
        AiSettingsDto settings, string provider, AiClusterService cluster, CancellationToken ct)
    {
        var model = settings.FlowModel ?? (settings.FlowProvider is null ? settings.DefaultModel : null);
        var effort = settings.FlowReasoningEffort
            ?? (settings.FlowProvider is null && (settings.FlowModel is null || settings.FlowModel == settings.DefaultModel)
                ? settings.DefaultReasoningEffort : null);
        var catalog = await cluster.ModelsAsync(false, ct);
        var nodes = settings.FlowLocalOnly ? catalog.Nodes.Take(1) : catalog.Nodes;
        var models = nodes.SelectMany(node => node.Providers)
            .Where(item => item.Provider == provider && item.State == "available")
            .SelectMany(item => item.Models)
            .Where(item => !catalog.Policy.DisabledModels.Any(disabled => disabled.Provider == provider && disabled.Model == item.Id))
            .DistinctBy(item => item.Id)
            .ToArray();
        var selected = model is null
            ? models.FirstOrDefault(item => item.IsDefault) ?? models.FirstOrDefault()
            : models.FirstOrDefault(item => item.Id == model);
        return (model ?? selected?.Id, effort ?? selected?.DefaultReasoningEffort);
    }
}
