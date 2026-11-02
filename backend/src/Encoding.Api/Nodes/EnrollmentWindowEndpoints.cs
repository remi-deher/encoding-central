using Encoding.Contracts.Auth;

namespace Encoding.Api.Nodes;

public static class EnrollmentWindowEndpoints
{
    public static void MapEnrollmentWindowEndpoints(this WebApplication app)
    {
        var window = app.MapGroup("/api/v1/nodes/enrollment-window").WithTags("Nodes");
        window.MapGet("", async (NodeManagementService service, CancellationToken ct) =>
            Results.Ok(await service.GetEnrollmentWindowAsync(ct))).WithName("GetEnrollmentWindow");
        window.MapPut("", async (OpenEnrollmentWindowRequest request, NodeManagementService service, CancellationToken ct) =>
        {
            if (request.Minutes is < 1 or > NodeManagementService.MaximumEnrollmentWindowMinutes)
                return (IResult)Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["minutes"] = [$"La durée doit être comprise entre 1 et {NodeManagementService.MaximumEnrollmentWindowMinutes} minutes."],
                });
            return Results.Ok(await service.OpenEnrollmentWindowAsync(request.Minutes, ct));
        }).WithName("OpenEnrollmentWindow");
        window.MapDelete("", async (NodeManagementService service, CancellationToken ct) =>
        {
            await service.CloseEnrollmentWindowAsync(ct);
            return Results.NoContent();
        }).WithName("CloseEnrollmentWindow");

        // Première liaison implicite (0040) : réservée aux administrateurs, comme toute écriture de gestion.
        var firstLink = app.MapGroup("/api/v1/nodes/first-link").WithTags("Nodes");
        firstLink.MapGet("", async (NodeManagementService service, CancellationToken ct) =>
            Results.Ok(await service.GetFirstLinkAcceptanceAsync(ct))).WithName("GetFirstLinkAcceptance");
        firstLink.MapPut("", async (OpenEnrollmentWindowRequest request, NodeManagementService service, HttpContext http, CancellationToken ct) =>
        {
            if (request.Minutes is < 1 or > NodeManagementService.MaximumFirstLinkMinutes)
                return (IResult)Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["minutes"] = [$"La durée doit être comprise entre 1 et {NodeManagementService.MaximumFirstLinkMinutes} minutes."],
                });
            var administrator = http.User.Identity?.Name ?? "administrateur";
            return Results.Ok(await service.EnableFirstLinkAcceptanceAsync(request.Minutes, administrator, ct));
        }).WithName("EnableFirstLinkAcceptance");
        firstLink.MapDelete("", async (NodeManagementService service, CancellationToken ct) =>
        {
            await service.DisableFirstLinkAcceptanceAsync(ct);
            return Results.NoContent();
        }).WithName("DisableFirstLinkAcceptance");
    }
}
