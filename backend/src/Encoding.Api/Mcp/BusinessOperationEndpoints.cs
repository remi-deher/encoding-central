using System.Text.Json;
using Encoding.Api.Auth;

namespace Encoding.Api.Mcp;

public sealed record BusinessOperationRequest(string Name, JsonElement Arguments);

public static class BusinessOperationEndpoints
{
    public static void MapBusinessOperationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/business-operations").WithTags("Business operations");
        group.MapGet("/", () => Results.Ok(BusinessOperationCatalog.Describe()))
            .AllowViewer().WithName("ListBusinessOperations");
        group.MapPost("/execute", async (BusinessOperationRequest request, HttpContext http, McpService service, CancellationToken ct) =>
        {
            var operation = BusinessOperationCatalog.FindTool(request.Name);
            if (operation is null)
                return Results.NotFound(new { title = $"Opération inconnue : {request.Name}" });
            if (McpAccess.MissingScope(http.User, operation) is { } missing)
            {
                await service.ExecuteForPrincipalAsync(request.Name, request.Arguments, http.User, ct);
                return Results.Json(new { title = McpAccess.MissingScopeMessage(missing) }, statusCode: StatusCodes.Status403Forbidden);
            }
            var result = await service.ExecuteForPrincipalAsync(request.Name, request.Arguments, http.User, ct);
            return result.IsError
                ? operation.RequiresAdministrator && !http.User.IsInRole("admin")
                    ? Results.Forbid()
                    : Results.BadRequest(new { title = result.Content.FirstOrDefault()?.Text ?? "Opération invalide." })
                : Results.Ok(result);
        }).AllowViewer().RequireRateLimiting(AuthExtensions.HeavyOperationsPolicy).WithName("ExecuteBusinessOperation");
    }
}
