using Encoding.Api.Auth;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

public static class AiToolEndpoints
{
    public static void MapAiToolEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/tools/ai").WithTags("AI Tools");
        group.MapGet("/", async (DeviceCodeAuthService service, CancellationToken ct) => Results.Ok(await service.ListToolsAsync(ct)));
        group.MapGet("/models", async (bool? refresh, AiClusterService service, CancellationToken ct) =>
            Results.Ok(await service.ModelsAsync(refresh ?? false, ct))).RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapPut("/models/policy", async (AiModelPolicyUpdate request, AiClusterService service, CancellationToken ct) =>
            await GuardAsync(async () => Results.Ok(await service.UpdateModelPolicyAsync(request, ct))))
            .RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapGet("/cluster", async (AiClusterService service, CancellationToken ct) => Results.Ok(await service.CachedViewAsync(ct)));
        group.MapGet("/quotas", async (AiClusterService service, CancellationToken ct) =>
            Results.Ok(await service.QuotasAsync(ct))).RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapGet("/usage", async (int? days, AiClusterService service, CancellationToken ct) =>
            await GuardAsync(async () => Results.Ok(await service.UsageAsync(days ?? 30, ct)))).RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapGet("/{id}/status", async (string id, DeviceCodeAuthService service, CancellationToken ct) => await GuardAsync(async () => Results.Ok(await service.GetStatusAsync(id, ct))));
        group.MapPost("/{id}/auth/device-code/start", async (string id, DeviceCodeAuthService service, AiQuotaService quotas, AiModelCatalogService models, CancellationToken ct) =>
            await GuardAsync(async () => { quotas.Invalidate(id); models.Invalidate(id); return Results.Ok(await service.StartDeviceAuthAsync(id, ct)); }))
            .RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapPost("/{id}/auth/input", async (string id, AiCliAuthInputRequest request, DeviceCodeAuthService service, CancellationToken ct) =>
            await GuardAsync(async () => { await service.SubmitAuthInputAsync(id, request.Input, ct, request.AttemptId); return Results.Ok(new { accepted = true }); }))
            .RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapGet("/{id}/auth/device-code/status", async (string id, DeviceCodeAuthService service,
            CancellationToken ct) => await GuardAsync(async () => Results.Ok(await service.GetStatusAsync(id, ct))));
        group.MapPost("/claude-cli/auth/token", async (ClaudeTokenRequest request, DeviceCodeAuthService service, AiQuotaService quotas, AiModelCatalogService models, CancellationToken ct) =>
            await GuardAsync(async () => { quotas.Invalidate("claude-cli"); models.Invalidate("claude-cli"); try { await service.SaveClaudeTokenAsync(request.Token,
                ct); return Results.Ok(new { saved = true }); } finally { quotas.Invalidate("claude-cli"); models.Invalidate("claude-cli"); } }))
            .RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapPost("/{id}/auth/cancel", async (string id, AiCliAuthCancelRequest request, DeviceCodeAuthService service) =>
        {
            await service.CancelAsync(id, request.AttemptId);
            return Results.Ok();
        }).RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapDelete("/{id}/auth", async (string id, DeviceCodeAuthService service, AiQuotaService quotas, AiModelCatalogService models, CancellationToken ct) =>
            await GuardAsync(async () => { quotas.Invalidate(id); models.Invalidate(id); try { return Results.Ok(new { revoked = await service.RevokeAuthAsync(id,
                ct) }); } finally { quotas.Invalidate(id); models.Invalidate(id); } }))
            .RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapGet("/settings", async (AiClusterService service, CancellationToken ct) => PublicSettings(await service.GetSettingsAsync(ct)));
        group.MapGet("/settings/model-options", async (bool? refresh, AiClusterService service, CancellationToken ct) =>
            Results.Ok(await service.ModelsAsync(refresh ?? false, ct)))
            .RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapPut("/settings", async (AiSettingsUpdateRequest request, AiClusterService service, AiSettingsService settings, CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                var current = await service.GetSettingsAsync(ct);
                settings.ValidateModelSelections(current, request, await service.ModelsAsync(false, ct));
                return PublicSettings(await service.UpdateSettingsAsync(request, ct));
            }))
            .RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapGet("/sessions", async (int? offset, AiClusterService service, CancellationToken ct) =>
            await GuardAsync(async () => Results.Ok(await service.ListSessionsAsync(offset ?? 0, ct))))
            .RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapGet("/sessions/{id}", async (string id, AiClusterService service, AiAssistantService assistant, CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                if (await assistant.IsAssistantConversationAsync(id, ct)) return Results.NotFound();
                var state = await service.GetSessionAsync(id, ct);
                return state is null ? Results.NotFound() : Results.Ok(state);
            }))
            .RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapPost("/sessions/{id}/fork", async (string id, AiClusterService service, AiAssistantService assistant, CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                if (await assistant.IsAssistantConversationAsync(id, ct)) return Results.NotFound();
                return Results.Ok(await service.ForkSessionAsync(id, ct));
            }))
            .RequireAuthorization(AuthExtensions.AdministratorPolicy);
        group.MapPost("/generate", async (AiGenerateRequest request, AiClusterService service, AiAssistantService assistant, CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                if (request.Prompt.SessionId is { } sessionId && await assistant.IsAssistantConversationAsync(sessionId, ct))
                    return Results.NotFound();
                return Results.Ok(await service.GenerateAsync(request.Provider, request.Prompt, ct, allowModelChanges: request.AllowModelChanges));
            }))
            .RequireAuthorization(AuthExtensions.AdministratorPolicy).RequireRateLimiting(AuthExtensions.HeavyOperationsPolicy);
        // Signed peer admission + AES-GCM payload are checked inside ReceiveAsync, independently of user sessions.
        app.MapPost(AiClusterTransport.Path, async (HttpContext http, AiClusterTransport transport, AiClusterService service, CancellationToken ct) =>
            await GuardAsync(() => transport.ReceiveAsync(http, service, ct)))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(20 * 1024 * 1024))
            .AllowAnonymous().RequireRateLimiting(AuthExtensions.ClusterMeshPolicy);
    }
    private static IResult PublicSettings(AiSettingsDto settings) => Results.Ok(new
    {
        settings.DefaultProvider, settings.OllamaBaseUrl, settings.OllamaModel, settings.OpenAiBaseUrl,
        settings.OpenAiModel, HasOpenAiApiKey = !string.IsNullOrWhiteSpace(settings.OpenAiApiKey),
        HasAnthropicApiKey = !string.IsNullOrWhiteSpace(settings.AnthropicApiKey), settings.AnthropicModel, settings.AnthropicEnabled, settings.TimeoutSeconds, settings.SubtitleBatchSize,
        settings.AssistantProvider, settings.FlowProvider, settings.AssistantLocalOnly, settings.FlowLocalOnly,
        settings.DefaultModel, settings.DefaultReasoningEffort, settings.AssistantModel, settings.AssistantReasoningEffort,
        settings.FlowModel, settings.FlowReasoningEffort,
        settings.OllamaEnabled, settings.OpenAiEnabled, settings.CodexEnabled, settings.ClaudeEnabled
    });
    private static async Task<IResult> GuardAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ArgumentException ex) { return Results.BadRequest(new { title = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { title = ex.Message }); }
        catch (System.Security.Cryptography.CryptographicException) { return Results.BadRequest(new { title = "Message IA invalide." }); }
        catch (System.Text.Json.JsonException) { return Results.BadRequest(new { title = "Message IA invalide." }); }
        catch (FormatException) { return Results.BadRequest(new { title = "Message IA invalide." }); }
    }
}
