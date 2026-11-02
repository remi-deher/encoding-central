using Encoding.Api.Auth;

namespace Encoding.Api.Infrastructure;

public sealed record ClusterNameRequest(string Name);

public static class ClusterEndpoints
{
    public static void MapClusterEndpoints(this WebApplication app)
    {
        const string route = "/api/v1/cluster";
        app.MapGet(route, async (ClusterIdentityRepository repository, IServiceProvider services, CancellationToken cancellationToken) =>
        {
            var identity = await repository.GetAsync(cancellationToken);
            var tokens = services.GetService<ApiTokenRepository>();
            var hasClusterToken = tokens is not null && await tokens.HasClusterTokenAsync(cancellationToken);
            return Results.Ok(new { identity.Id, identity.Name, identity.Standalone, hasClusterToken });
        }).AllowAnonymous().WithTags("Cluster").WithName("GetClusterIdentity");

        // Un nœud autonome devient un cluster en le nommant ; ensuite seul le nom change (architecture 0045).
        app.MapPost(route, async (ClusterNameRequest request, ClusterIdentityRepository repository,
            IServiceProvider services, CancellationToken cancellationToken) =>
        {
            if (!ClusterIdentityRepository.IsValidName(request.Name))
                return Results.ValidationProblem(new Dictionary<string,
                    string[]> { ["name"] = [$"Le nom du cluster est obligatoire ({ClusterIdentityRepository.MaximumNameLength} caractères au plus)."] });
            if (!await repository.CreateAsync(request.Name, cancellationToken))
                return Results.Conflict(new { title = "Ce nœud fait déjà partie d’un cluster." });

            var tokenRepo = services.GetRequiredService<ApiTokenRepository>();
            var generatedToken = await tokenRepo.EnsureClusterTokenAsync(cancellationToken);
            var resultIdentity = await repository.GetAsync(cancellationToken);
            return Results.Ok(new { resultIdentity.Id, resultIdentity.Name, resultIdentity.Standalone, clusterToken = generatedToken });
        }).WithTags("Cluster").WithName("CreateCluster");

        app.MapPut(route, async (ClusterNameRequest request, ClusterIdentityRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (!ClusterIdentityRepository.IsValidName(request.Name))
                return Results.ValidationProblem(new Dictionary<string,
                    string[]> { ["name"] = [$"Le nom du cluster est obligatoire ({ClusterIdentityRepository.MaximumNameLength} caractères au plus)."] });
            return await repository.RenameAsync(request.Name, cancellationToken)
                ? Results.Ok(await repository.GetAsync(cancellationToken))
                : Results.Conflict(new { title = "Ce nœud est autonome : créez d’abord un cluster." });
        }).WithTags("Cluster").WithName("RenameCluster");
    }
}
