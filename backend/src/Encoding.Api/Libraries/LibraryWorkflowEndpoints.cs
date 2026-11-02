using Encoding.Api.Auth;
using Encoding.Api.Infrastructure;
using Encoding.Contracts.Libraries;

namespace Encoding.Api.Libraries;

public static class LibraryWorkflowEndpoints
{
    public static void MapLibraryWorkflowEndpoints(this WebApplication app)
    {
        var libraries = app.MapGroup("/api/v1/libraries/{library:guid}").WithTags("Libraries");

        libraries.MapGet("/processing", async (Guid library, LibraryProcessingRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.GetAsync(library, ct)))
            .WithName("GetLibraryProcessingSettings");

        libraries.MapPut("/processing", async (Guid library, SaveLibraryProcessingSettings request, LibraryProcessingRepository repository, CancellationToken ct) =>
        {
            try
            {
                return await repository.SaveAsync(library, request, ct)
                    ? Results.NoContent()
                    : ApiProblem.Conflict("La configuration a changé. Actualisez la bibliothèque.");
            }
            catch (ArgumentException e)
            {
                return ApiProblem.BadRequest(e.Message);
            }
        }).WithName("SaveLibraryProcessingSettings");

        libraries.MapGet("/workflow", async (
            Guid library,
            string? search,
            int? limit,
            int? offset,
            LibraryWorkflowRepository repository,
            CancellationToken ct) =>
            Results.Ok(await repository.GetAsync(library, search, limit, offset, ct)))
            .WithName("GetLibraryWorkflow");

        libraries.MapPut("/flow", async (Guid library, AssignLibraryFlowRequest request, LibraryWorkflowRepository repository, CancellationToken ct) =>
            await repository.AssignAsync(library, request, ct)
                ? Results.NoContent()
                : ApiProblem.Conflict("Version publiée invalide ou configuration modifiée ailleurs."))
            .WithName("AssignLibraryFlow");

        libraries.MapPost("/discover", async (Guid library, LibraryWorkflowRepository repository, CancellationToken ct) =>
            await repository.RequestScanAsync(library, ct) is { } id
                ? Results.Accepted(value: new { id })
                : ApiProblem.Conflict("Activez la bibliothèque et attribuez-lui un flow publié avant la découverte."))
            .RequireRateLimiting(AuthExtensions.HeavyOperationsPolicy)
            .WithName("DiscoverLibraryFiles");

        libraries.MapPost("/executions", async (Guid library, EnqueueLibraryFilesRequest request, LibraryWorkflowRepository repository, CancellationToken ct) =>
            request.FileIds is not null && await repository.EnqueueAsync(library, request.FileIds, request.AssignmentRevision, ct) is { } result
                ? Results.Ok(new { queued = result.Queued, skipped = result.Skipped })
                : ApiProblem.Conflict("La sélection ou le flow attribué n’est plus disponible. Actualisez la bibliothèque."))
            .WithName("EnqueueLibraryFiles");
    }
}
