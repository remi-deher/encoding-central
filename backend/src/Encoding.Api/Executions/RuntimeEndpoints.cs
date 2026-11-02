using Encoding.Api.Auth;
using Encoding.Api.Infrastructure;
using Encoding.Api.Resources;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Libraries;
using Encoding.Contracts.Nodes;
using Microsoft.AspNetCore.Http.Features;

namespace Encoding.Api.Executions;

public static class RuntimeEndpoints
{
    public static void MapRuntimeEndpoints(this WebApplication app)
    {
        app.MapRuntimeArtifactEndpoints();
        var group = app.MapGroup("/api/v1/runtime/nodes/{nodeId:guid}").AllowAnonymous().WithTags("Runtime");
        group.AddEndpointFilter(async (context, next) =>
        {
            var node = Guid.Parse((string)context.HttpContext.Request.RouteValues["nodeId"]!);
            var coordinator = context.HttpContext.RequestServices.GetRequiredService<RuntimeExecutionCoordinator>();
            if (!await coordinator.AuthenticateAsync(node, context.HttpContext.Request.Headers["X-Node-Token"].ToString(),
                context.HttpContext.RequestAborted))
            {
                return Results.Unauthorized();
            }
            return await next(context);
        });

        group.MapRuntimeDirectoryBrowserEndpoints();
        group.MapRuntimeAiEndpoints();
        group.MapRuntimeStatusAndCapacityEndpoints();
        group.MapRuntimeScanEndpoints();
        group.MapRuntimeLeaseEndpoints();
        group.MapRuntimeLockAndWriteEndpoints();
        group.MapRuntimeStepEndpoints();
        group.MapRuntimeTransferEndpoints();
        group.MapRuntimeResourceEndpoints();
        group.MapRuntimeDavEndpoints();
    }

    private static void MapRuntimeArtifactEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/executions/{id:guid}/artifacts", async (
            Guid id, RuntimeArtifactStore artifacts, CancellationToken ct) =>
            Results.Ok(await artifacts.ProducedAsync(id, ct)));

        app.MapGet("/api/v1/executions/{id:guid}/artifacts/{digest}", async (
            Guid id, string digest, RuntimeArtifactStore artifacts, CancellationToken ct) =>
        {
            if (!RuntimeArtifactStore.ValidDigest(digest)) return (IResult)Results.BadRequest();
            var artifact = (await artifacts.ProducedAsync(id, ct)).FirstOrDefault(item => item.Sha256 == digest);
            if (artifact is null) return Results.NotFound();
            var path = artifacts.PathFor(id, digest);
            return FileResults.ServeOrNotFound(path, "application/octet-stream", artifact.Name);
        });

        app.MapPost("/api/v1/media-executions", () =>
            Results.Json(new { title = "Lancez les traitements depuis les fichiers détectés de la bibliothèque." }, statusCode: 410));
    }

    private static void MapRuntimeDirectoryBrowserEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/directory-browser/claim", async (
            Guid nodeId, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.ClaimDirectoryBrowseAsync(nodeId, ct) is { } claim ? Results.Ok(claim) : Results.NoContent());

        group.MapPost("/directory-browser/{requestId:guid}/complete", async (
            Guid nodeId, Guid requestId, NodeDirectoryBrowseCompletion request,
            RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.CompleteDirectoryBrowseAsync(requestId, nodeId, request, ct) ? Results.NoContent() : Results.Conflict());
    }

    private static void MapRuntimeAiEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/executions/{id:guid}/ai/subtitles/translate", async (
            Guid nodeId, Guid id, RuntimeAiSubtitleTranslationRequest request,
            RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
        {
            var outcome = await coordinator.TranslateSubtitlesAsync(nodeId, id, request, ct);
            return outcome.Kind switch
            {
                RuntimeAiOutcomeKind.BadRequest => Results.BadRequest(new { message = outcome.ErrorMessage }),
                RuntimeAiOutcomeKind.Conflict => Results.Conflict(new { message = outcome.ErrorMessage }),
                RuntimeAiOutcomeKind.Ok => Results.Ok(outcome.Response),
                _ => Results.StatusCode(500)
            };
        }).RequireRateLimiting(AuthExtensions.HeavyOperationsPolicy);

        group.MapPost("/executions/{id:guid}/ai/metadata/enrich", async (
            Guid nodeId, Guid id, RuntimeAiMetadataEnrichmentRequest request,
            RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
        {
            var outcome = await coordinator.EnrichMetadataAsync(nodeId, id, request, ct);
            return outcome.Kind switch
            {
                RuntimeAiOutcomeKind.BadRequest => Results.BadRequest(new { message = outcome.ErrorMessage }),
                RuntimeAiOutcomeKind.Conflict => Results.Conflict(new { message = outcome.ErrorMessage }),
                RuntimeAiOutcomeKind.Ok => Results.Ok(outcome.Response),
                _ => Results.StatusCode(500)
            };
        }).RequireRateLimiting(AuthExtensions.HeavyOperationsPolicy);
    }

    private static void MapRuntimeStatusAndCapacityEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/execution-states", async (
            RuntimeExecutionStatesRequest request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            request.Ids is null || request.Ids.Count > 500
                ? (IResult)Results.BadRequest()
                : Results.Ok(await coordinator.GetExecutionStatesAsync(request.Ids, ct)));

        group.MapGet("/capacity", async (
            Guid nodeId, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.GetNodeCapacityAsync(nodeId, ct) is { } settings
                ? (IResult)Results.Ok(new RuntimeCapacity(settings.EffectiveMaxConcurrentTasks))
                : Results.NotFound());
    }

    private static void MapRuntimeScanEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/library-scans/claim", async (
            Guid nodeId, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.ClaimScanAsync(nodeId, ct) is { } scan ? (IResult)Results.Ok(scan) : Results.NoContent());

        group.MapPost("/library-scans/{id:guid}/renew", async (
            Guid nodeId, Guid id, LibraryScanFence request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.RenewScanAsync(id, nodeId, request.Epoch, ct) ? (IResult)Results.NoContent() : Results.Conflict());

        group.MapPost("/library-scans/{id:guid}/complete", async (
            Guid nodeId, Guid id, CompleteLibraryScan request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
        {
            try { return await coordinator.CompleteScanAsync(id, nodeId, request, ct) ? (IResult)Results.NoContent() : Results.Conflict(); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
    }

    private static void MapRuntimeLeaseEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/claim", async (
            Guid nodeId, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.ClaimExecutionAsync(nodeId, ct) is { } lease ? Results.Ok(lease) : Results.NoContent());

        group.MapPost("/executions/{id:guid}/renew", async (
            Guid nodeId, Guid id, RuntimeFence request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.RenewExecutionAsync(id, nodeId, request.Epoch, request.Commands, ct) is { } status
                ? (IResult)Results.Ok(status)
                : Results.Conflict());

        group.MapPost("/executions/{id:guid}/operation-logs", async (
            Guid nodeId, Guid id, RuntimeFence request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.ReportSourceOperationsAsync(id, nodeId, request, ct) ? (IResult)Results.NoContent() : Results.Conflict());

        group.MapPost("/executions/{id:guid}/placement-plan", async (
            Guid nodeId, Guid id, RuntimeFence request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.PersistPlacementPlanAsync(id, nodeId, request.Epoch, ct) is { } plan ? Results.Ok(plan) : Results.Conflict());

        group.MapPost("/executions/{id:guid}/source-metadata", async (
            Guid nodeId, Guid id, ReportSourceMetadata request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.RecordSourceMetadataAsync(id, nodeId, request.Epoch, request.Metadata, ct) is { } plan ? Results.Ok(plan) : Results.Conflict());
    }

    private static void MapRuntimeLockAndWriteEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/executions/{id:guid}/disk-locks/acquire", async (
            Guid nodeId, Guid id, DiskLockRequest request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
        {
            try { return await coordinator.TryAcquireDiskLockAsync(id, nodeId, request, ct) is { } response ? (IResult)Results.Ok(response) : Results.Conflict(); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });

        group.MapPost("/executions/{id:guid}/disk-locks/release", async (
            Guid nodeId, Guid id, DiskLockRequest request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
        {
            try { return await coordinator.ReleaseDiskLockAsync(id, nodeId, request, ct) is not null ? (IResult)Results.NoContent() : Results.Conflict(); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });

        group.MapPost("/executions/{id:guid}/source-write/authorize", async (
            Guid nodeId, Guid id, SourceWriteAuthorization request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            !string.IsNullOrWhiteSpace(request.Root) && request.Root.Length <= 4096
                && await coordinator.AuthorizeSourceWriteAsync(id, nodeId, request.Epoch, request.Root, ct) ? (IResult)Results.NoContent() : Results.Conflict());

        group.MapPost("/replacements/{id:guid}/authorize", async (
            Guid nodeId, Guid id, SourceWriteLocation request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            !string.IsNullOrWhiteSpace(request.Root) && request.Root.Length <= 4096
                && await coordinator.AuthorizeReplacementAsync(id, nodeId, request.Root, ct) ? (IResult)Results.NoContent() : Results.Conflict());

        group.MapPost("/source-writes/claim", async (
            Guid nodeId, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.ClaimPendingSourceWriteAsync(nodeId, ct) is { } task ? (IResult)Results.Ok(task) : Results.NoContent());

        group.MapPost("/source-writes/{id:guid}/authorize", async (
            Guid nodeId, Guid id, PendingSourceWriteFence request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            !string.IsNullOrWhiteSpace(request.Root) && request.Root.Length <= 4096
                && await coordinator.AuthorizePendingSourceWriteAsync(id, nodeId, request.ClaimId, request.Root, ct) ? (IResult)Results.NoContent() : Results.Conflict());

        group.MapPost("/source-writes/{id:guid}/complete", async (
            Guid nodeId, Guid id, PendingSourceWriteCompletion request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.CompletePendingSourceWriteAsync(id, nodeId, request, ct) ? (IResult)Results.NoContent() : Results.Conflict());

        group.MapGet("/replacements", async (
            Guid nodeId, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            Results.Ok(await coordinator.GetReplacementTasksAsync(nodeId, ct)));

        group.MapPost("/replacements/{id:guid}/report", async (
            Guid nodeId, Guid id, ReportReplacementTask request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.ReportReplacementTaskAsync(id, nodeId, request, ct) ? (IResult)Results.NoContent() : Results.Conflict());

        group.MapPost("/executions/{id:guid}/source-failure", async (
            Guid nodeId, Guid id, FinishRuntimeExecution request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.FinishExecutionAsync(id, nodeId, request, true, ct) ? (IResult)Results.NoContent() : Results.Conflict());
    }

    private static void MapRuntimeStepEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/executions/{id:guid}/steps/start", async (
            Guid nodeId, Guid id, StartRuntimeStep request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.StartStepAsync(id, nodeId, request, ct) is { } step ? (IResult)Results.Ok(step) : Results.Conflict());

        group.MapPost("/executions/{id:guid}/steps/complete", async (
            Guid nodeId, Guid id, CompleteRuntimeStep request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.CompleteStepAsync(id, nodeId, request, ct) ? Results.NoContent() : Results.Conflict());

        group.MapPost("/executions/{id:guid}/wait/probe", async (
            Guid nodeId, Guid id, ReportWaitProbe request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.ReportWaitProbeAsync(id, nodeId, request, ct) ? (IResult)Results.NoContent() : Results.Conflict());

        group.MapPost("/executions/{id:guid}/finish", async (
            Guid nodeId, Guid id, FinishRuntimeExecution request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.FinishExecutionAsync(id, nodeId, request, false, ct) ? Results.NoContent() : Results.Conflict());
    }

    private static void MapRuntimeTransferEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/transfers", async (
            Guid nodeId, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            Results.Ok(await coordinator.ListTransfersAsync(nodeId, ct)));

        group.MapGet("/executions/{id:guid}/source", async (
            Guid nodeId, Guid id, long epoch, RuntimeArtifactStore artifacts,
            RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await artifacts.AuthorizeAsync(id, nodeId, epoch, ct)
                ? (IResult)Results.Ok(await coordinator.GetSourceAsync(id, ct))
                : Results.Conflict());

        group.MapPost("/executions/{id:guid}/source/prepare", async (
            Guid nodeId, Guid id, RuntimeFence request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.PrepareSourceAsync(id, nodeId, request.Epoch, ct) ? (IResult)Results.NoContent() : Results.Conflict());

        group.MapGet("/executions/{id:guid}/publication", async (
            Guid nodeId, Guid id, long epoch, RuntimeArtifactStore artifacts,
            RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await artifacts.AuthorizeAsync(id, nodeId, epoch, ct) && await coordinator.GetPublicationStateAsync(id, ct) is { } state
                ? (IResult)Results.Ok(state)
                : Results.Conflict());

        group.MapPost("/executions/{id:guid}/publication", async (
            Guid nodeId, Guid id, ConfirmRuntimePublication request,
            RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.ConfirmPublicationAsync(id, nodeId, request, ct) ? (IResult)Results.NoContent() : Results.Conflict());
    }

    private static void MapRuntimeResourceEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/resources", async (
            RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            Results.Ok(await coordinator.GetLatestResourceVersionsAsync(ct)));

        group.MapGet("/resources/{resourceId:guid}/versions/{version:int}", async (
            Guid resourceId, int version, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.GetResourceVersionAsync(resourceId, version, ct) is { } item
                ? Results.Ok(item)
                : Results.NotFound());

        group.MapGet("/resources/blobs/{digest}", async (
            string digest, RuntimeExecutionCoordinator coordinator, ResourceBlobStore blobs, CancellationToken ct) =>
        {
            if (!ResourceBlobStore.ValidDigest(digest)) return Results.NotFound();
            if (!blobs.Exists(digest))
            {
                if (!await coordinator.EnsureBlobAsync(digest, ct)) return Results.NotFound();
            }
            return Results.File(blobs.OpenRead(digest), "application/octet-stream", enableRangeProcessing: true);
        });
    }

    private static void MapRuntimeDavEndpoints(this RouteGroupBuilder group)
    {
        group.MapPut("/executions/{id:guid}/dav/{digest}", async (
            Guid nodeId, Guid id, string digest, long epoch, bool? original, string? inputPath,
            HttpRequest request, RuntimeArtifactStore artifacts, IConfiguration configuration, CancellationToken ct) =>
        {
            if (!RuntimeArtifactStore.ValidDigest(digest)) return (IResult)Results.BadRequest();
            var feature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false })
                feature.MaxRequestBodySize = configuration.GetValue<long>("Runtime:MaxArtifactBytes", 1024L * 1024 * 1024 * 1024);
            try
            {
                return await artifacts.PutAsync(id, nodeId, epoch, digest, request.Body, original == true, ct, inputPath)
                    ? Results.Created(request.Path, null)
                    : Results.Conflict();
            }
            catch (InvalidDataException e) { return Results.BadRequest(new { title = e.Message }); }
        });

        group.MapGet("/executions/{id:guid}/dav/{digest}", async (
            Guid nodeId, Guid id, string digest, long epoch, RuntimeArtifactStore artifacts, CancellationToken ct) =>
        {
            if (!RuntimeArtifactStore.ValidDigest(digest) || !await artifacts.AuthorizeAsync(id, nodeId, epoch, ct))
                return (IResult)Results.Conflict();
            var path = artifacts.PathFor(id, digest);
            return FileResults.ServeOrNotFound(path, "application/octet-stream", etag: '"' + digest + '"');
        });

        group.MapMethods("/executions/{id:guid}/dav/{digest}", ["PROPFIND"], async (
            Guid nodeId, Guid id, string digest, long epoch, RuntimeArtifactStore artifacts, CancellationToken ct) =>
        {
            if (!RuntimeArtifactStore.ValidDigest(digest) || !await artifacts.AuthorizeAsync(id, nodeId, epoch, ct))
                return (IResult)Results.Conflict();
            var file = new FileInfo(artifacts.PathFor(id, digest));
            if (!file.Exists) return Results.NotFound();
            return Results.Text(
                $"<?xml version=\"1.0\"?><d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>{digest}</d:href><d:propstat><d:prop><d:getcontentlength>{file.Length}</d:getcontentlength><d:getetag>\"{digest}\"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>",
                "application/xml", statusCode: 207);
        });
    }
}
