using Encoding.Api.Auth;
using Encoding.Api.Infrastructure;
using Encoding.Api.Resources;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Libraries;
using Encoding.Contracts.Workers;
using Microsoft.AspNetCore.Http.Features;

namespace Encoding.Api.Executions;

public static class RuntimeEndpoints
{
    public static void MapRuntimeEndpoints(this WebApplication app)
    {
        app.MapRuntimeArtifactEndpoints();
        var group = app.MapGroup("/api/v1/runtime/workers/{workerId:guid}").AllowAnonymous().WithTags("Runtime");
        group.AddEndpointFilter(async (context, next) =>
        {
            var worker = Guid.Parse((string)context.HttpContext.Request.RouteValues["workerId"]!);
            var coordinator = context.HttpContext.RequestServices.GetRequiredService<RuntimeExecutionCoordinator>();
            if (!await coordinator.AuthenticateAsync(worker, context.HttpContext.Request.Headers["X-Node-Token"].ToString(),
                context.HttpContext.RequestAborted))
            {
                return Results.Unauthorized();
            }
            return await next(context);
        });

        group.MapRuntimeWakeEndpoint();
        group.MapWorkerSocketEndpoint();
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
            Guid workerId, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.ClaimDirectoryBrowseAsync(workerId, ct) is { } claim ? Results.Ok(claim) : Results.NoContent());

        group.MapPost("/directory-browser/{requestId:guid}/complete", async (
            Guid workerId, Guid requestId, WorkerDirectoryBrowseCompletion request,
            RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.CompleteDirectoryBrowseAsync(requestId, workerId, request, ct) ? Results.NoContent() : Results.Conflict());
    }

    private static void MapRuntimeAiEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/executions/{id:guid}/ai/subtitles/translate", async (
            Guid workerId, Guid id, RuntimeAiSubtitleTranslationRequest request,
            RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
        {
            var outcome = await coordinator.TranslateSubtitlesAsync(workerId, id, request, ct);
            return outcome.Kind switch
            {
                RuntimeAiOutcomeKind.BadRequest => Results.BadRequest(new { message = outcome.ErrorMessage }),
                RuntimeAiOutcomeKind.Conflict => Results.Conflict(new { message = outcome.ErrorMessage }),
                RuntimeAiOutcomeKind.Ok => Results.Ok(outcome.Response),
                _ => Results.StatusCode(500)
            };
        }).RequireRateLimiting(AuthExtensions.HeavyOperationsPolicy);

        group.MapPost("/executions/{id:guid}/ai/metadata/enrich", async (
            Guid workerId, Guid id, RuntimeAiMetadataEnrichmentRequest request,
            RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
        {
            var outcome = await coordinator.EnrichMetadataAsync(workerId, id, request, ct);
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
            Guid workerId, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.GetWorkerCapacityAsync(workerId, ct) is { } settings
                ? (IResult)Results.Ok(new RuntimeCapacity(settings.EffectiveMaxConcurrentTasks))
                : Results.NotFound());
    }

    private static void MapRuntimeScanEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/library-scans/claim", async (
            Guid workerId, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.ClaimScanAsync(workerId, ct) is { } scan ? (IResult)Results.Ok(scan) : Results.NoContent());

        group.MapPost("/library-scans/{id:guid}/renew", async (
            Guid workerId, Guid id, LibraryScanFence request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.RenewScanAsync(id, workerId, request.Epoch, ct) ? (IResult)Results.NoContent() : Results.Conflict());

        group.MapPost("/library-scans/{id:guid}/complete", async (
            Guid workerId, Guid id, CompleteLibraryScan request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
        {
            try { return await coordinator.CompleteScanAsync(id, workerId, request, ct) ? (IResult)Results.NoContent() : Results.Conflict(); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
    }

    private static void MapRuntimeLeaseEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/claim", async (
            Guid workerId, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.ClaimExecutionAsync(workerId, ct) is { } lease ? Results.Ok(lease) : Results.NoContent());

        group.MapPost("/executions/{id:guid}/renew", async (
            Guid workerId, Guid id, RuntimeFence request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.RenewExecutionAsync(id, workerId, request.Epoch, request.Commands, ct) is { } status
                ? (IResult)Results.Ok(status)
                : Results.Conflict());

        group.MapPost("/executions/{id:guid}/operation-logs", async (
            Guid workerId, Guid id, RuntimeFence request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.ReportSourceOperationsAsync(id, workerId, request, ct) ? (IResult)Results.NoContent() : Results.Conflict());

        group.MapPost("/executions/{id:guid}/placement-plan", async (
            Guid workerId, Guid id, RuntimeFence request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.PersistPlacementPlanAsync(id, workerId, request.Epoch, ct) is { } plan ? Results.Ok(plan) : Results.Conflict());

        group.MapPost("/executions/{id:guid}/source-metadata", async (
            Guid workerId, Guid id, ReportSourceMetadata request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.RecordSourceMetadataAsync(id, workerId, request.Epoch, request.Metadata, ct) is { } plan ? Results.Ok(plan) : Results.Conflict());
    }

    private static void MapRuntimeLockAndWriteEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/executions/{id:guid}/disk-locks/acquire", async (
            Guid workerId, Guid id, DiskLockRequest request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
        {
            try { return await coordinator.TryAcquireDiskLockAsync(id, workerId, request, ct) is { } response ? (IResult)Results.Ok(response) : Results.Conflict(); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });

        group.MapPost("/executions/{id:guid}/disk-locks/release", async (
            Guid workerId, Guid id, DiskLockRequest request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
        {
            try { return await coordinator.ReleaseDiskLockAsync(id, workerId, request, ct) is not null ? (IResult)Results.NoContent() : Results.Conflict(); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });

        group.MapPost("/executions/{id:guid}/source-write/authorize", async (
            Guid workerId, Guid id, SourceWriteAuthorization request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            !string.IsNullOrWhiteSpace(request.Root) && request.Root.Length <= 4096
                && await coordinator.AuthorizeSourceWriteAsync(id, workerId, request.Epoch, request.Root, ct) ? (IResult)Results.NoContent() : Results.Conflict());

        group.MapPost("/replacements/{id:guid}/authorize", async (
            Guid workerId, Guid id, SourceWriteLocation request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            !string.IsNullOrWhiteSpace(request.Root) && request.Root.Length <= 4096
                && await coordinator.AuthorizeReplacementAsync(id, workerId, request.Root, ct) ? (IResult)Results.NoContent() : Results.Conflict());

        group.MapGet("/replacements", async (
            Guid workerId, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            Results.Ok(await coordinator.GetReplacementTasksAsync(workerId, ct)));

        group.MapPost("/replacements/{id:guid}/report", async (
            Guid workerId, Guid id, ReportReplacementTask request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.ReportReplacementTaskAsync(id, workerId, request, ct) ? (IResult)Results.NoContent() : Results.Conflict());

        group.MapPost("/executions/{id:guid}/source-failure", async (
            Guid workerId, Guid id, FinishRuntimeExecution request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.FinishExecutionAsync(id, workerId, request, true, ct) ? (IResult)Results.NoContent() : Results.Conflict());
    }

    private static void MapRuntimeStepEndpoints(this RouteGroupBuilder group)
    {
        // Le corps du 409 porte un code de motif court et stable (StepRefusalCodes) que le worker reprend dans son échec.
        group.MapPost("/executions/{id:guid}/steps/start", async (
            Guid workerId, Guid id, StartRuntimeStep request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
        {
            var (step, refusal) = await coordinator.TryStartStepAsync(id, workerId, request, ct);
            return step is not null ? (IResult)Results.Ok(step) : Results.Conflict(new RuntimeRefusal(refusal?.Code ?? "unknown"));
        });

        group.MapPost("/executions/{id:guid}/steps/complete", async (
            Guid workerId, Guid id, CompleteRuntimeStep request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
        {
            var refusal = await coordinator.TryCompleteStepAsync(id, workerId, request, ct);
            return refusal is null ? (IResult)Results.NoContent() : Results.Conflict(new RuntimeRefusal(refusal.Code));
        });

        group.MapPost("/executions/{id:guid}/wait/probe", async (
            Guid workerId, Guid id, ReportWaitProbe request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.ReportWaitProbeAsync(id, workerId, request, ct) ? (IResult)Results.NoContent() : Results.Conflict());

        group.MapPost("/executions/{id:guid}/finish", async (
            Guid workerId, Guid id, FinishRuntimeExecution request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.FinishExecutionAsync(id, workerId, request, false, ct) ? Results.NoContent() : Results.Conflict());
    }

    private static void MapRuntimeTransferEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/transfers", async (
            Guid workerId, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            Results.Ok(await coordinator.ListTransfersAsync(workerId, ct)));

        group.MapGet("/executions/{id:guid}/source", async (
            Guid workerId, Guid id, long epoch, RuntimeArtifactStore artifacts,
            RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await artifacts.AuthorizeAsync(id, workerId, epoch, ct)
                ? (IResult)Results.Ok(await coordinator.GetSourceAsync(id, ct))
                : Results.Conflict());

        group.MapPost("/executions/{id:guid}/source/prepare", async (
            Guid workerId, Guid id, RuntimeFence request, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.PrepareSourceAsync(id, workerId, request.Epoch, ct) ? (IResult)Results.NoContent() : Results.Conflict());

        group.MapGet("/executions/{id:guid}/publication", async (
            Guid workerId, Guid id, long epoch, RuntimeArtifactStore artifacts,
            RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await artifacts.AuthorizeAsync(id, workerId, epoch, ct) && await coordinator.GetPublicationStateAsync(id, ct) is { } state
                ? (IResult)Results.Ok(state)
                : Results.Conflict());

        group.MapPost("/executions/{id:guid}/publication", async (
            Guid workerId, Guid id, ConfirmRuntimePublication request,
            RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.ConfirmPublicationAsync(id, workerId, request, ct) ? (IResult)Results.NoContent() : Results.Conflict());
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
            Guid workerId, Guid id, string digest, long epoch, bool? original, string? inputPath,
            HttpRequest request, RuntimeArtifactStore artifacts, IConfiguration configuration, CancellationToken ct) =>
        {
            if (!RuntimeArtifactStore.ValidDigest(digest)) return (IResult)Results.BadRequest();
            var feature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false })
                feature.MaxRequestBodySize = configuration.GetValue<long>("Runtime:MaxArtifactBytes", 1024L * 1024 * 1024 * 1024);
            try
            {
                return await artifacts.PutAsync(id, workerId, epoch, digest, request.Body, original == true, ct, inputPath)
                    ? Results.Created(request.Path, null)
                    : Results.Conflict();
            }
            catch (InvalidDataException e) { return Results.BadRequest(new { title = e.Message }); }
        });

        group.MapGet("/executions/{id:guid}/dav/{digest}", async (
            Guid workerId, Guid id, string digest, long epoch, RuntimeArtifactStore artifacts, CancellationToken ct) =>
        {
            if (!RuntimeArtifactStore.ValidDigest(digest) || !await artifacts.AuthorizeAsync(id, workerId, epoch, ct))
                return (IResult)Results.Conflict();
            var path = artifacts.PathFor(id, digest);
            return FileResults.ServeOrNotFound(path, "application/octet-stream", etag: '"' + digest + '"');
        });

        group.MapMethods("/executions/{id:guid}/dav/{digest}", ["PROPFIND"], async (
            Guid workerId, Guid id, string digest, long epoch, RuntimeArtifactStore artifacts, CancellationToken ct) =>
        {
            if (!RuntimeArtifactStore.ValidDigest(digest) || !await artifacts.AuthorizeAsync(id, workerId, epoch, ct))
                return (IResult)Results.Conflict();
            var file = new FileInfo(artifacts.PathFor(id, digest));
            if (!file.Exists) return Results.NotFound();
            return Results.Text(
                $"<?xml version=\"1.0\"?><d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>{digest}</d:href><d:propstat><d:prop><d:getcontentlength>{file.Length}</d:getcontentlength><d:getetag>\"{digest}\"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>",
                "application/xml", statusCode: 207);
        });
    }
}
