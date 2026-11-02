using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Auth;
using Encoding.Api.Infrastructure;
using Microsoft.AspNetCore.Http.Features;

namespace Encoding.Api.Resources;

public static class ResourceEndpoints
{
    public static void MapResourceEndpoints(this WebApplication app)
    {
        var resources = app.MapGroup("/api/v1/resources").WithTags("Ressources");
        resources.MapGet("", async (ResourceRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.ListAsync(ct))).WithName("ListResources");
        resources.MapGet("/{id:guid}", async (Guid id, ResourceRepository repository, CancellationToken ct) =>
            await repository.GetAsync(id, ct) is { } details ? Results.Ok(details) : Results.NotFound()).WithName("GetResource");
        resources.MapGet("/{id:guid}/versions/{version:int}", async (Guid id, int version, ResourceRepository repository, CancellationToken ct) =>
            await repository.GetVersionAsync(id, version, ct) is { } item ? Results.Ok(item) : Results.NotFound()).WithName("GetResourceVersion");

        // Envoi d'un fichier (corps brut, un fichier par requête) : il rejoint le magasin, puis une ressource ou une
        // nouvelle version le référence par son empreinte. Réservé au rôle admin (toute écriture l'est par défaut).
        resources.MapPost("/blobs", async (HttpRequest request, string kind, string name, ResourceBlobStore blobs, CancellationToken ct) =>
        {
            if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = ResourceBlobStore.MaxFileBytes + 1024;
            if (request.ContentLength > ResourceBlobStore.MaxFileBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            try { return Results.Ok(await blobs.PutAsync(request.Body, name, kind, ct)); }
            catch (ResourceException exception) { return Results.UnprocessableEntity(new { title = exception.Message }); }
        }).WithName("UploadResourceFile");

        resources.MapPost("", async (CreateResourceRequest request, ResourceRepository repository, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (request.Kind != "font") errors["kind"] = ["Type de ressource non pris en charge (« font » uniquement)."];
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128) errors["name"] = ["Le nom contient entre 1 et 128 caractères."];
            if ((request.Description ?? "").Trim().Length > 1000) errors["description"] = ["La description contient 1000 caractères au plus."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            try
            {
                return await UniqueConflict.GuardAsync(async () =>
                {
                    var id = Guid.NewGuid();
                    var created = await repository.CreateAsync(id, request.Kind, request.Name, request.Description ?? "", request.Files ?? [], ct);
                    return Results.Created($"/api/v1/resources/{id:D}", created);
                }, () => Results.Conflict(new { title = "Une ressource porte déjà ce nom." }));
            }
            catch (ResourceException exception) { return Results.UnprocessableEntity(new { title = exception.Message }); }
        }).WithName("CreateResource");

        resources.MapPost("/{id:guid}/versions", async (Guid id, AddResourceVersionRequest request, ResourceRepository repository, CancellationToken ct) =>
        {
            try
            {
                return await repository.AddVersionAsync(id, request.Files ?? [], ct) is { } version
                    ? Results.Created($"/api/v1/resources/{id:D}/versions/{version.Version}", version)
                    : Results.NotFound();
            }
            catch (ResourceException exception) { return Results.UnprocessableEntity(new { title = exception.Message }); }
        }).WithName("AddResourceVersion");

        resources.MapPut("/{id:guid}", async (Guid id, UpdateResourceRequest request, ResourceRepository repository, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128) errors["name"] = ["Le nom contient entre 1 et 128 caractères."];
            if (request.Description is null || request.Description.Trim().Length > 1000) errors["description"] = ["La description contient 1000 caractères au plus."];
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            return await UniqueConflict.GuardAsync(
                async () => await repository.UpdateAsync(id, request.Name, request.Description ?? "", ct) is { } updated ? Results.Ok(updated) : Results.NotFound(),
                () => Results.Conflict(new { title = "Une ressource porte déjà ce nom." }));
        }).WithName("UpdateResource");

        // Lecture d'un fichier par un pair admis (reprise par Range). Pas de corps : la signature couvre la méthode, le chemin
        // et l'horodatage ; l'octet est adressé par son empreinte, que le demandeur revérifie à l'arrivée.
        app.MapGet(ResourceBlobFetcher.RoutePrefix + "{digest}", async (string digest, HttpContext http, ClusterIdentityRepository identityRepository,
            ClusterPeerAdmissionRepository peerAdmissions, ResourceBlobStore blobs, CancellationToken ct) =>
        {
            if (!ResourceBlobStore.ValidDigest(digest)) return Results.NotFound();
            if (!Guid.TryParse(http.Request.Headers[ClusterReconciliationSignature.PeerHeader], out var peerId)) return Results.Unauthorized();
            var identity = await identityRepository.GetAsync(ct);
            var publicKey = await peerAdmissions.GetActivePublicKeyAsync(identity.Id, peerId, ct);
            if (publicKey is null || !ClusterReconciliationSignature.Verify(publicKey, peerId,
                    http.Request.Headers[ClusterReconciliationSignature.TimeHeader],
                    http.Request.Headers[ClusterReconciliationSignature.SignatureHeader], ResourceBlobFetcher.SignedRequest(digest)))
                return Results.Unauthorized();
            return blobs.Exists(digest)
                ? Results.File(blobs.OpenRead(digest), "application/octet-stream", enableRangeProcessing: true)
                : Results.NotFound();
        })
        .AllowAnonymous()
        .RequireRateLimiting(AuthExtensions.ClusterMeshPolicy)
        .WithTags("Cluster reconciliation")
        .WithName("GetClusterResourceBlob");

        resources.MapDelete("/{id:guid}", async (Guid id, ResourceRepository repository, CancellationToken ct) =>
        {
            var usage = await repository.UsageAsync(id, ct);
            if (usage.Count > 0)
                return Results.Conflict(new
                {
                    title = "Cette ressource est utilisée : "
                        + string.Join(" ; ", usage.Select(item => $"flow « {item.FlowName} » (version {item.FlowVersion}), bloc « {item.NodeName ?? item.NodeId} »")),
                    usage
                });
            return await repository.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound();
        }).WithName("DeleteResource");
    }
}
