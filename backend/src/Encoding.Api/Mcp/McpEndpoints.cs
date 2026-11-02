using System.Security.Claims;
using System.Text.Json;
using Encoding.Api.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Encoding.Api.Mcp;

public static class McpEndpoints
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void MapMcpEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/mcp").WithTags("MCP");

        // Transport HTTP/SSE : Connexion initiale du client MCP
        group.MapGet("/sse", async (
            HttpContext http,
            McpSessionManager sessionManager,
            CancellationToken ct) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            var sessionId = Guid.NewGuid().ToString("N");
            var ownerId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(ownerId) || !sessionManager.TryCreateSession(sessionId, ownerId, out var reader))
            {
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            try
            {
                await http.Response.WriteAsync($"event: endpoint\ndata: /api/v1/mcp/message?sessionId={sessionId}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);

                while (!ct.IsCancellationRequested && await reader.WaitToReadAsync(ct))
                {
                    while (reader.TryRead(out var response))
                    {
                        var json = JsonSerializer.Serialize(response, JsonOptions);
                        await http.Response.WriteAsync($"event: message\ndata: {json}\n\n", ct);
                        await http.Response.Body.FlushAsync(ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                sessionManager.CloseSession(sessionId);
            }
        }).AllowViewer().WithName("McpSse");

        // Transport HTTP/SSE : Envoi d'un message JSON-RPC vers la session active
        group.MapPost("/message", async (
            [FromQuery] string sessionId,
            [FromBody] McpJsonRpcRequest request,
            McpService mcpService,
            McpSessionManager sessionManager,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return Results.BadRequest("Paramètre 'sessionId' manquant.");
            if (request.Id is null && request.Method != "notifications/initialized")
                return Results.BadRequest("Une requête MCP sur le transport SSE doit contenir un identifiant JSON-RPC.");

            var ownerId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(ownerId)
                || !sessionManager.TryReserveRequest(sessionId, ownerId, request.Id, out var duplicate, out var limitReached))
                return Results.NotFound($"Session MCP '{sessionId}' expirée, introuvable ou inaccessible.");
            if (duplicate)
                return Results.Conflict("L'identifiant JSON-RPC a déjà été traité pour cette session.");
            if (limitReached)
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);

            var response = await mcpService.HandleMessageAsync(request, user, ct);
            if (response is not null)
            {
                var delivered = await sessionManager.SendMessageAsync(sessionId, ownerId, response, ct);
                if (!delivered)
                    return Results.NotFound($"Session MCP '{sessionId}' expirée ou introuvable.");
            }

            return Results.Accepted();
        }).AllowViewer().WithName("McpMessage").WithMetadata(new RequestSizeLimitAttribute(1_048_576));

        // Transport HTTP direct (sans SSE) : Requête/Réponse JSON-RPC unitaire
        group.MapPost("", async (
            [FromBody] McpJsonRpcRequest request,
            McpService mcpService,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var response = await mcpService.HandleMessageAsync(request, user, ct);
            return response is not null ? Results.Ok(response) : Results.NoContent();
        }).AllowViewer().WithName("McpDirect").WithMetadata(new RequestSizeLimitAttribute(1_048_576));
    }
}
