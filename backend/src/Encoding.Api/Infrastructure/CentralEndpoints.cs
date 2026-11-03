namespace Encoding.Api.Infrastructure;

public sealed record CentralNameRequest(string Name);

public static class CentralEndpoints
{
    public static void MapCentralEndpoints(this WebApplication app)
    {
        const string route = "/api/v1/central";
        app.MapGet(route, async (CentralIdentityRepository repository, CancellationToken cancellationToken) =>
            Results.Ok(await repository.GetAsync(cancellationToken))).AllowAnonymous().WithTags("Central").WithName("GetClusterIdentity");

        app.MapPut(route, async (CentralNameRequest request, CentralIdentityRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (!CentralIdentityRepository.IsValidName(request.Name))
                return Results.ValidationProblem(new Dictionary<string,
                    string[]> { ["name"] = [$"Le nom du central est obligatoire ({CentralIdentityRepository.MaximumNameLength} caractères au plus)."] });
            await repository.RenameAsync(request.Name, cancellationToken);
            return Results.Ok(await repository.GetAsync(cancellationToken));
        }).WithTags("Central").WithName("RenameCluster");
    }
}
