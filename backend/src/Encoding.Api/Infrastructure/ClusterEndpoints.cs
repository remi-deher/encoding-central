using Encoding.Api.Auth;
using Encoding.Core.Nodes;

namespace Encoding.Api.Infrastructure;

using System.Text.Json;
using Encoding.Contracts.Cluster;

public sealed record ClusterNameRequest(string Name);
public sealed record ClusterJoinRequest(Guid ClusterId, string ClusterName, Guid NodeId, ClusterJoinBootstrap? Bootstrap = null, string? PeerUrl = null);

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
        app.MapPost(route, async (ClusterNameRequest request, ClusterIdentityRepository repository, ClusterPeerAdmissionRepository admissions,
            IServiceProvider services, CancellationToken cancellationToken) =>
        {
            if (!ClusterIdentityRepository.IsValidName(request.Name))
                return Results.ValidationProblem(new Dictionary<string,
                    string[]> { ["name"] = [$"Le nom du cluster est obligatoire ({ClusterIdentityRepository.MaximumNameLength} caractères au plus)."] });
            var current = await repository.GetAsync(cancellationToken);
            await admissions.EnsureRootAdmissionAsync(current.Id, cancellationToken);
            if (!await repository.CreateAsync(request.Name, cancellationToken))
                return Results.Conflict(new { title = "Ce nœud fait déjà partie d’un cluster." });

            var tokenRepo = services.GetRequiredService<ApiTokenRepository>();
            var generatedToken = await tokenRepo.EnsureClusterTokenAsync(cancellationToken);
            var resultIdentity = await repository.GetAsync(cancellationToken);
            return Results.Ok(new { resultIdentity.Id, resultIdentity.Name, resultIdentity.Standalone, clusterToken = generatedToken });
        }).WithTags("Cluster").WithName("CreateCluster");

        app.MapPut(route, async (ClusterNameRequest request, ClusterIdentityRepository repository, ClusterPeerAdmissionRepository admissions,
            ClusterDataKeyStore dataKeyStore, IServiceProvider services, CancellationToken cancellationToken) =>
        {
            if (!ClusterIdentityRepository.IsValidName(request.Name))
                return Results.ValidationProblem(new Dictionary<string,
                    string[]> { ["name"] = [$"Le nom du cluster est obligatoire ({ClusterIdentityRepository.MaximumNameLength} caractères au plus)."] });
            var current = await repository.GetAsync(cancellationToken);
            if (!dataKeyStore.HasKey(current.Id))
                await admissions.EnsureRootAdmissionAsync(current.Id, cancellationToken);
            return await repository.RenameAsync(request.Name, cancellationToken)
                ? Results.Ok(await repository.GetAsync(cancellationToken))
                : Results.Conflict(new { title = "Ce nœud est autonome : créez d’abord un cluster." });
        }).WithTags("Cluster").WithName("RenameCluster");

        app.MapGet(route + "/join-collisions", async (ClusterJoinCollisionRepository collisions, CancellationToken ct) =>
            Results.Ok(await collisions.ListAsync(ct))).RequireAuthorization(AuthExtensions.AdministratorPolicy);
        app.MapPost(route + "/join-collisions/{id:guid}/resolve", async (Guid id, ResolveJoinUserCollision request, ClusterJoinCollisionRepository collisions, CancellationToken ct) =>
            await collisions.ResolveAsync(id, request,
                ct) ? Results.NoContent() : (IResult)Results.Conflict(new { title = "Le nom est réservé, invalide, ou le compte a changé. Actualisez la proposition." }))
            .RequireAuthorization(AuthExtensions.AdministratorPolicy);

        // L'agent local peut aligner l'identité de sa base SQLite après l'approbation du cluster d'accueil.
        app.MapPost(route + "/join", async (
            ClusterJoinRequest request,
            IConfiguration configuration,
            HttpContext http,
            ClusterIdentityRepository repository,
            ClusterPeerAdmissionRepository admissions,
            ClusterReconciliationRepository reconciliationRepository,
            ClusterJoinCollisionRepository collisions,
            CancellationToken cancellationToken) =>
        {
            if (!LocalEnrollment.Matches(configuration[LocalEnrollment.SecretFileSetting], http.Request.Headers[LocalEnrollment.HeaderName].ToString()))
                return Results.Unauthorized();
            if (request.ClusterId == Guid.Empty || !ClusterIdentityRepository.IsValidName(request.ClusterName))
                return Results.BadRequest(new { title = "Identité du cluster invalide." });
            if (request.NodeId == Guid.Empty || request.Bootstrap is null || request.Bootstrap.ClusterId != request.ClusterId
                || !string.Equals(request.Bootstrap.ClusterName, request.ClusterName, StringComparison.Ordinal))
                return Results.BadRequest(new { title = "La preuve d’admission du cluster est absente ou incohérente." });
            try
            {
                admissions.ValidateJoinBootstrap(request.Bootstrap, request.NodeId);
                if (!await collisions.PrepareAsync(request.Bootstrap, cancellationToken))
                    return Results.Conflict(new { code = "join_username_resolution_required",
                        title = "Résolvez les collisions de comptes dans Paramètres > Utilisateurs. Un cluster ancien doit être mis à jour pour fournir la liste signée des comptes." });
                await admissions.ImportBootstrapAsync(request.Bootstrap, request.NodeId, cancellationToken);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or System.Security.Cryptography.CryptographicException or KeyNotFoundException)
            {
                return Results.BadRequest(new { title = "La signature ou la clé de données du cluster est invalide." });
            }
            await repository.AdoptAsync(request.ClusterId, request.ClusterName, cancellationToken);
            if (!string.IsNullOrWhiteSpace(request.PeerUrl))
                await reconciliationRepository.AddPeerEndpointAsync(request.PeerUrl, "bootstrap", cancellationToken);
            return Results.Ok(await repository.GetAsync(cancellationToken));
        })
        .AllowAnonymous()
        .RequireRateLimiting(AuthExtensions.AnonymousPolicy)
        .WithTags("Cluster")
        .WithName("AdoptJoinedClusterIdentity");
    }
}
