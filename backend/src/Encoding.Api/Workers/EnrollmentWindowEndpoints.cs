using Encoding.Contracts.Auth;

namespace Encoding.Api.Workers;

public static class EnrollmentWindowEndpoints
{
    public static void MapEnrollmentWindowEndpoints(this WebApplication app)
    {
        var window = app.MapGroup("/api/v1/workers/enrollment-window").WithTags("Workers");
        window.MapGet("", async (WorkerManagementService service, CancellationToken ct) =>
            Results.Ok(await service.GetEnrollmentWindowAsync(ct))).WithName("GetEnrollmentWindow");
        window.MapPut("", async (OpenEnrollmentWindowRequest request, WorkerManagementService service, CancellationToken ct) =>
        {
            if (request.Minutes is < 1 or > WorkerManagementService.MaximumEnrollmentWindowMinutes)
                return (IResult)Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["minutes"] = [$"La durée doit être comprise entre 1 et {WorkerManagementService.MaximumEnrollmentWindowMinutes} minutes."],
                });
            return Results.Ok(await service.OpenEnrollmentWindowAsync(request.Minutes, ct));
        }).WithName("OpenEnrollmentWindow");
        window.MapDelete("", async (WorkerManagementService service, CancellationToken ct) =>
        {
            await service.CloseEnrollmentWindowAsync(ct);
            return Results.NoContent();
        }).WithName("CloseEnrollmentWindow");

        // Première liaison implicite (0040) : réservée aux administrateurs, comme toute écriture de gestion.
        var firstLink = app.MapGroup("/api/v1/workers/first-link").WithTags("Workers");
        firstLink.MapGet("", async (WorkerManagementService service, CancellationToken ct) =>
            Results.Ok(await service.GetFirstLinkAcceptanceAsync(ct))).WithName("GetFirstLinkAcceptance");
        firstLink.MapPut("", async (OpenEnrollmentWindowRequest request, WorkerManagementService service, HttpContext http, CancellationToken ct) =>
        {
            if (request.Minutes is < 1 or > WorkerManagementService.MaximumFirstLinkMinutes)
                return (IResult)Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["minutes"] = [$"La durée doit être comprise entre 1 et {WorkerManagementService.MaximumFirstLinkMinutes} minutes."],
                });
            var administrator = http.User.Identity?.Name ?? "administrateur";
            return Results.Ok(await service.EnableFirstLinkAcceptanceAsync(request.Minutes, administrator, ct));
        }).WithName("EnableFirstLinkAcceptance");
        firstLink.MapDelete("", async (WorkerManagementService service, CancellationToken ct) =>
        {
            await service.DisableFirstLinkAcceptanceAsync(ct);
            return Results.NoContent();
        }).WithName("DisableFirstLinkAcceptance");
    }
}
