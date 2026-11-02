using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Auth;
using Encoding.Contracts.Tools;
using Encoding.Api.Mcp;

namespace Encoding.Api.Tools;

public static class AiAssistantEndpoints
{
    public static void MapAiAssistantEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/tools/ai/assistant").WithTags("AI Assistant");

        group.MapGet("/options", async (bool? refresh, AiAssistantService service, CancellationToken ct) =>
            await GuardAsync(async () => Results.Ok(await service.OptionsAsync(ct, refresh ?? false))))
        .AllowAssistantConversation();

        group.MapGet("/conversations", async (HttpContext http, AiAssistantService service, CancellationToken ct) =>
            await GuardAsync(async () => Results.Ok(await service.ListAsync(http.User, ct))));

        group.MapGet("/conversations/search", async (string? q, HttpContext http, AiAssistantService service, CancellationToken ct) =>
            await GuardAsync(async () => Results.Ok(await service.SearchAsync(q, http.User, ct))));

        group.MapGet("/activity", async (HttpContext http, AiAssistantService service, CancellationToken ct) =>
            await GuardAsync(async () => Results.Ok(await service.ActivityAsync(http.User, ct))));

        group.MapGet("/activity/{requestId:guid}", async (Guid requestId, HttpContext http, AiAssistantService service, CancellationToken ct) =>
            await GuardAsync(async () => await service.GetActivityAsync(requestId, http.User, ct) is { } activity
                ? Results.Ok(activity)
                : Results.NoContent()));

        group.MapGet("/conversations/{id}", async (string id, HttpContext http, AiAssistantService service, CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                var detail = await service.GetAsync(id, http.User, ct);
                return detail is null ? Results.NotFound() : Results.Ok(detail);
            }));

        group.MapDelete("/conversations/{id}", async (string id, HttpContext http, AiAssistantService service, CancellationToken ct) =>
            await GuardAsync(async () => await service.DeleteConversationAsync(id, http.User, ct)
                ? Results.NoContent()
                : Results.NotFound()));

        group.MapPut("/conversations/{id}/title", async (string id, AiAssistantRenameRequest request, HttpContext http, AiAssistantService service, CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                var conversation = await service.RenameAsync(id, request.Title, http.User, ct);
                return conversation is null ? Results.NotFound() : Results.Ok(conversation);
            }));

        group.MapPut("/conversations/{id}/sources", async (string id, AiAssistantSourcesRequest request, HttpContext http, AiAssistantService service, CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                var conversation = await service.UpdateSourcesAsync(id, request, http.User, ct);
                return conversation is null ? Results.NotFound() : Results.Ok(conversation);
            }))
        .AllowAssistantConversation();

        group.MapPost("/messages", async (AiAssistantMessageRequest request, HttpContext http, AiAssistantService service, AiClusterTransport transport,
            AiClusterService cluster, CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                if (await cluster.CoordinatorAsync(request.ConversationId, ct) is { } coordinatorId && coordinatorId != transport.LocalId
                    && !await TakeOverLostCoordinatorAsync(request.ConversationId!, coordinatorId, transport, cluster, ct))
                {
                    if (!Guid.TryParse(http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var ownerId)
                        || ownerId == Guid.Empty)
                        return Results.BadRequest(new { title = "Identité utilisateur indisponible." });
                    var forwarded = await transport.CallNodeAsync<AiAssistantForwardResponse>(coordinatorId, "assistant-message",
                        new AiAssistantForwardRequest(request, ownerId, http.User.IsInRole("admin")), TimeSpan.FromMinutes(35), ct);
                    if (forwarded.InvalidRequest) return Results.BadRequest(new { title = forwarded.Error });
                    if (forwarded.Error is not null) return Results.Conflict(new { title = forwarded.Error });
                    return forwarded.Result is null ? Results.NotFound() : Results.Ok(forwarded.Result);
                }
                var result = await service.SendAsync(request, http.User, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }))
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(50 * 1024 * 1024))
        .AllowAssistantConversation()
        .RequireRateLimiting(AuthExtensions.HeavyOperationsPolicy);

        group.MapPost("/requests/{requestId:guid}/cancel", async (Guid requestId, HttpContext http, AiAssistantService service, AiClusterTransport transport,
            AiClusterService cluster, CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                var coordinatorId = await cluster.CoordinatorAsync(http.Request.Query["conversationId"].ToString(), ct)
                    ?? await service.GetRequestCoordinatorIdAsync(requestId, http.User, ct);
                if (coordinatorId is null)
                    return Results.Conflict(new { title = "Aucune demande active ne correspond à cet identifiant." });

                bool cancelled;
                if (coordinatorId == transport.LocalId)
                    cancelled = await service.CancelRequestAsync(requestId, http.User, ct);
                else
                {
                    if (!Guid.TryParse(http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var ownerId)
                        || ownerId == Guid.Empty)
                        return Results.BadRequest(new { title = "Identité utilisateur indisponible." });
                    cancelled = await transport.CallNodeAsync<bool>(coordinatorId.Value, "assistant-cancel",
                        new AiAssistantForwardCancel(ownerId, requestId), TimeSpan.FromSeconds(15), ct);
                }
                return cancelled
                    ? Results.Accepted(value: new { requestId, state = "cancellation-requested" })
                    : Results.Conflict(new { title = "Aucune demande active ne correspond à cet identifiant." });
            }))
        .AllowAssistantConversation();

        group.MapPost("/tools/call", async (AiAssistantToolCall call, McpService mcp, CancellationToken ct) =>
            await GuardAsync(async () => Results.Ok(await mcp.CallAssistantReadToolAsync(call, ct))))
        .AllowAssistantConversation()
        .RequireRateLimiting(AuthExtensions.HeavyOperationsPolicy);

        group.MapPost("/actions/delete-flow/prepare", async (AiAssistantPrepareActionRequest request, HttpContext http, AiAssistantService service, CancellationToken ct) =>
        {
            if (!http.User.IsInRole("admin")) return Results.Forbid();
            return await GuardAsync(async () => await service.PrepareDeleteFlowAsync(request, http.User, ct) is { } prepared
                ? Results.Ok(prepared)
                : Results.NotFound());
        }).AllowAssistantConversation();

        group.MapPost("/actions/delete-flow/confirm", async (AiAssistantConfirmActionRequest request, HttpContext http, AiAssistantService service, CancellationToken ct) =>
        {
            if (!http.User.IsInRole("admin")) return Results.Forbid();
            return await GuardAsync(async () => await service.ConfirmDeleteFlowAsync(request, http.User, ct)
                ? Results.Ok(new { deleted = true })
                : Results.NotFound());
        }).AllowAssistantConversation();

        group.MapPost("/actions/publish-flow/prepare", async (AiAssistantPrepareActionRequest request, HttpContext http, AiAssistantService service, CancellationToken ct) =>
        {
            if (!http.User.IsInRole("admin")) return Results.Forbid();
            return await GuardAsync(async () => await service.PreparePublishFlowAsync(request, http.User, ct) is { } prepared
                ? Results.Ok(prepared)
                : Results.NotFound());
        }).AllowAssistantConversation();

        group.MapPost("/actions/publish-flow/confirm", async (AiAssistantConfirmActionRequest request, HttpContext http, AiAssistantService service, CancellationToken ct) =>
        {
            if (!http.User.IsInRole("admin")) return Results.Forbid();
            return await GuardAsync(async () => await service.ConfirmPublishFlowAsync(request, http.User, ct)
                ? Results.Ok(new { published = true })
                : Results.Conflict(new { title = "Le flow n’a pas pu être publié." }));
        }).AllowAssistantConversation();

        group.MapPost("/actions/cancel-execution/prepare", async (AiAssistantPrepareActionRequest request, HttpContext http, AiAssistantService service, CancellationToken ct) =>
        {
            if (!http.User.IsInRole("admin")) return Results.Forbid();
            return await GuardAsync(async () => await service.PrepareCancelExecutionAsync(request, http.User, ct) is { } prepared
                ? Results.Ok(prepared)
                : Results.NotFound());
        }).AllowAssistantConversation();

        group.MapPost("/actions/cancel-execution/confirm", async (AiAssistantConfirmActionRequest request, HttpContext http, AiAssistantService service, CancellationToken ct) =>
        {
            if (!http.User.IsInRole("admin")) return Results.Forbid();
            return await GuardAsync(async () => await service.ConfirmCancelExecutionAsync(request, http.User, ct)
                ? Results.Ok(new { cancellationRequested = true })
                : Results.Conflict(new { title = "L’exécution n’est plus annulable." }));
        }).AllowAssistantConversation();

        group.MapPost("/actions/start-library/prepare", async (AiAssistantPrepareActionRequest request, HttpContext http, AiAssistantService service, CancellationToken ct) =>
        {
            if (!http.User.IsInRole("admin")) return Results.Forbid();
            return await GuardAsync(async () => await service.PrepareStartLibraryAsync(request, http.User, ct) is { } prepared
                ? Results.Ok(prepared)
                : Results.NotFound());
        }).AllowAssistantConversation();

        group.MapPost("/actions/start-library/confirm", async (AiAssistantConfirmActionRequest request, HttpContext http, AiAssistantService service, CancellationToken ct) =>
        {
            if (!http.User.IsInRole("admin")) return Results.Forbid();
            return await GuardAsync(async () => await service.ConfirmStartLibraryAsync(request, http.User, ct) is { } result
                ? Results.Ok(new { queued = result.Queued, skipped = result.Skipped })
                : Results.Conflict(new { title = "La bibliothèque ou son affectation n’est plus disponible." }));
        }).AllowAssistantConversation();

        group.MapPost("/actions/assign-library-flow/confirm", async (AiAssistantConfirmActionRequest request, HttpContext http, AiAssistantService service, CancellationToken ct) =>
        {
            if (!http.User.IsInRole("admin")) return Results.Forbid();
            return await GuardAsync(async () => await service.ConfirmAssignLibraryFlowAsync(request, http.User, ct)
                ? Results.Ok(new { assigned = true })
                : Results.Conflict(new { title = "L’affectation du flow n’a pas pu être modifiée." }));
        }).AllowAssistantConversation();

        group.MapPost("/actions/create-flow-template/confirm", async (AiAssistantConfirmActionRequest request, HttpContext http, AiAssistantService service, CancellationToken ct) =>
        {
            if (!http.User.IsInRole("admin")) return Results.Forbid();
            return await GuardAsync(async () => await service.ConfirmCreateFlowTemplateAsync(request, http.User, ct)
                ? Results.Ok(new { created = true })
                : Results.Conflict(new { title = "Le modèle de flow n’a pas pu être créé." }));
        }).AllowAssistantConversation();

        group.MapPost("/actions/business-operation/confirm", async (AiAssistantConfirmActionRequest request, HttpContext http, AiAssistantService service, CancellationToken ct) =>
        {
            if (!http.User.IsInRole("admin")) return Results.Forbid();
            return await GuardAsync(async () => await service.ConfirmBusinessOperationAsync(request, http.User, ct)
                ? Results.Ok(new { applied = true })
                : Results.Conflict(new { title = "L’opération n’a pas pu être appliquée." }));
        }).AllowAssistantConversation();

        group.MapPost("/activity/{requestId:guid}/proposal", async (Guid requestId, AiAssistantProposalDecision request, HttpContext http, AiAssistantService service, CancellationToken ct) =>
            await GuardAsync(async () => await service.UpdateFlowProposalActivityAsync(requestId, request.Decision, http.User, ct)
                ? Results.Ok(new { requestId, status = request.Decision })
                : Results.Conflict(new { title = "Cette proposition n’est plus en attente de décision." })))
        .AllowAssistantConversation();
    }

    /// <summary>
    /// Reprise d'une discussion dont le coordinateur ne répond à aucune sonde ; faux (donc transmission au coordinateur, qui échoue
    /// proprement) s'il répond encore ou si l'historique validé n'est pas présent sur ce nœud.
    /// </summary>
    private static async Task<bool> TakeOverLostCoordinatorAsync(string conversationId, Guid coordinatorId, AiClusterTransport transport,
        AiClusterService cluster, CancellationToken ct) =>
        await transport.FindNodeUrlAsync(coordinatorId, ct) is null && await cluster.TryTakeOverAsync(conversationId, ct);

    private static async Task<IResult> GuardAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ArgumentException ex) { return Results.BadRequest(new { title = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { title = ex.Message }); }
        catch (CryptographicException) { return Results.BadRequest(new { title = "Message IA invalide." }); }
        catch (JsonException) { return Results.BadRequest(new { title = "Message IA invalide." }); }
        catch (FormatException) { return Results.BadRequest(new { title = "Message IA invalide." }); }
    }
}
