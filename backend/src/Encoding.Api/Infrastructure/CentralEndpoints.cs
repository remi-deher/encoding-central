using Encoding.Api.Auth;

namespace Encoding.Api.Infrastructure;

public sealed record CentralNameRequest(string Name);

public static class CentralEndpoints
{
    public static void MapClusterEndpoints(this WebApplication app)
    {
        const string route = "/api/v1/central";
        app.MapGet(route, async (CentralIdentityRepository repository, IServiceProvider services, CancellationToken cancellationToken) =>
        {
            var identity = await repository.GetAsync(cancellationToken);
            var tokens = services.GetService<ApiTokenRepository>();
            var hasClusterToken = tokens is not null && await tokens.HasClusterTokenAsync(cancellationToken);
            return Results.Ok(new { identity.Id, identity.Name, identity.Standalone, hasClusterToken });
        }).AllowAnonymous().WithTags("Cluster").WithName("GetClusterIdentity");

        // Un worker autonome devient un central en le nommant ; ensuite seul le nom change (architecture 0045).
        app.MapPost(route, async (CentralNameRequest request, CentralIdentityRepository repository,
            IServiceProvider services, CancellationToken cancellationToken) =>
        {
            if (!CentralIdentityRepository.IsValidName(request.Name))
                return Results.ValidationProblem(new Dictionary<string,
                    string[]> { ["name"] = [$"Le nom du central est obligatoire ({CentralIdentityRepository.MaximumNameLength} caractères au plus)."] });
            if (!await repository.CreateAsync(request.Name, cancellationToken))
                return Results.Conflict(new { title = "Ce worker fait déjà partie d’un central." });

            var tokenRepo = services.GetRequiredService<ApiTokenRepository>();
            var generatedToken = await tokenRepo.EnsureClusterTokenAsync(cancellationToken);
            var resultIdentity = await repository.GetAsync(cancellationToken);
            return Results.Ok(new { resultIdentity.Id, resultIdentity.Name, resultIdentity.Standalone, clusterToken = generatedToken });
        }).WithTags("Cluster").WithName("CreateCluster");

        app.MapPut(route, async (CentralNameRequest request, CentralIdentityRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (!CentralIdentityRepository.IsValidName(request.Name))
                return Results.ValidationProblem(new Dictionary<string,
                    string[]> { ["name"] = [$"Le nom du central est obligatoire ({CentralIdentityRepository.MaximumNameLength} caractères au plus)."] });
            return await repository.RenameAsync(request.Name, cancellationToken)
                ? Results.Ok(await repository.GetAsync(cancellationToken))
                : Results.Conflict(new { title = "Ce worker est autonome : créez d’abord un central." });
        }).WithTags("Cluster").WithName("RenameCluster");
    }
}
