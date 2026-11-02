using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Libraries;

namespace Encoding.Api.Libraries;

public static class LibraryEndpoints
{
    public static void MapLibraryEndpoints(this WebApplication app)
    {
        var libraries = app.MapGroup("/api/v1/libraries").WithTags("Libraries");
        libraries.MapGet("", async (LibraryRepository repository, CancellationToken cancellationToken) =>
            Results.Ok(await repository.ListAsync(cancellationToken)))
            .WithName("ListLibraries");
        libraries.MapGet("/overview", async (LibraryRepository repository, CancellationToken cancellationToken) =>
            Results.Ok(await repository.OverviewAsync(cancellationToken)))
            .WithName("GetLibrariesOverview");
        libraries.MapGet("/export", async (LibraryRepository repository, Infrastructure.ClusterIdentityRepository cluster, HttpContext http, CancellationToken cancellationToken) =>
        {
            var identity = await cluster.GetAsync(cancellationToken);
            http.Response.Headers.ContentDisposition = "attachment; filename=\"bibliotheques.json\"";
            return Results.Ok(new LibraryExport(1, DateTimeOffset.UtcNow, identity.Name, await repository.ListAsync(cancellationToken)));
        }).WithName("ExportLibraries");
        libraries.MapPost("", async (CreateLibraryRequest request, LibraryRepository repository, CancellationToken cancellationToken) =>
        {
            var created = await repository.CreateAsync(request, cancellationToken);
            return created is null
                ? ApiProblem.Conflict("Une bibliothèque porte déjà ce nom.")
                : Results.Created($"/api/v1/libraries/{created.Id}", created);
        }).AddEndpointFilter<ValidateLibraryNameFilter>().WithName("CreateLibrary");

        libraries.MapPut("/{libraryId:guid}", async (
            Guid libraryId,
            UpdateLibraryRequest request,
            LibraryRepository repository,
            CancellationToken cancellationToken) =>
        {
            return await UniqueConflict.GuardAsync(async () => {
                var updated = await repository.UpdateAsync(libraryId, request, cancellationToken);
                return updated is null ? ApiProblem.NotFound() : Results.Ok(updated);
            }, () => ApiProblem.Conflict("Une bibliothèque porte déjà ce nom."));
        }).AddEndpointFilter<ValidateLibraryNameFilter>().WithName("UpdateLibrary");

        libraries.MapPut("/{libraryId:guid}/activation", async (
            Guid libraryId,
            SetLibraryActivationRequest request,
            LibraryRepository repository,
            CancellationToken cancellationToken) =>
        {
            var updated = await repository.SetActiveAsync(libraryId, request.IsActive, cancellationToken);
            return updated is null ? ApiProblem.NotFound() : Results.Ok(updated);
        }).WithName("SetLibraryActivation");

        libraries.MapDelete("/{libraryId:guid}", async (
            Guid libraryId,
            LibraryRepository repository,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return await repository.DeleteAsync(libraryId, cancellationToken) ? Results.NoContent() : ApiProblem.NotFound();
            }
            catch (LibraryInUseException)
            {
                return ApiProblem.Conflict("Des traitements de cette bibliothèque sont en attente ou en cours. Désactivez-la, annulez-les, puis réessayez.");
            }
        }).WithName("DeleteLibrary");

        libraries.MapGet("/{libraryId:guid}/node-access", async (
            Guid libraryId,
            LibraryNodeAccessRepository repository,
            CancellationToken cancellationToken) =>
        {
            var accesses = await repository.ListAsync(libraryId, cancellationToken);
            return accesses is null ? ApiProblem.NotFound() : Results.Ok(accesses);
        }).WithName("ListLibraryNodeAccesses");

        libraries.MapPut("/{libraryId:guid}/node-access", async (
            Guid libraryId,
            ConfigureLibraryNodeAccessRequest request,
            LibraryNodeAccessRepository repository,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var accesses = await repository.ConfigureAsync(libraryId, request, cancellationToken);
                return accesses is null ? ApiProblem.NotFound() : Results.Ok(accesses);
            }
            catch (LibraryNodeAccessValidationException exception)
            {
                return ApiProblem.Validation(exception.Errors.ToDictionary(pair => pair.Key, pair => pair.Value));
            }
        }).WithName("ConfigureLibraryNodeAccesses");

        libraries.MapPut("/{libraryId:guid}/node-access/{nodeId:guid}/activation", async (
            Guid libraryId,
            Guid nodeId,
            SetLibraryNodeAccessActivationRequest request,
            LibraryNodeAccessRepository repository,
            CancellationToken cancellationToken) =>
        {
            var access = await repository.SetEnabledAsync(libraryId, nodeId, request.IsEnabled, cancellationToken);
            return access is null ? ApiProblem.NotFound() : Results.Ok(access);
        }).WithName("SetLibraryNodeAccessActivation");
    }
}
