using Encoding.Api.Auth;
using Encoding.Api.Infrastructure;
using Encoding.Contracts.Workers;
using Encoding.Core.Workers;

namespace Encoding.Api.Workers;

public static class WorkerEndpoints
{
    public static void MapNodeEndpoints(this WebApplication app)
    {
        var nodes = app.MapGroup("/api/v1/workers").WithTags("Nodes");
        nodes.MapNodeDirectoryBrowseEndpoints();
        nodes.MapNodeEnrollmentEndpoints();
        nodes.MapNodeHeartbeatEndpoints();
        nodes.MapNodeLifecycleEndpoints();
        nodes.MapNodeProcessingEndpoints();
    }

    private static void MapNodeDirectoryBrowseEndpoints(this RouteGroupBuilder nodes)
    {
        nodes.MapPost("/{nodeId:guid}/directory-browse", async (
            Guid nodeId, WorkerDirectoryBrowserRequest request, WorkerManagementService service, CancellationToken ct) =>
        {
            if (await service.CreateDirectoryBrowseAsync(nodeId, request.Path, ct) is not { } id) return Results.NotFound();
            return Results.Accepted($"/api/v1/workers/{nodeId:D}/directory-browse/{id:D}", new { id });
        }).RequireAuthorization(AuthExtensions.AdministratorPolicy)
          .RequireRateLimiting(AuthExtensions.HeavyOperationsPolicy)
          .WithName("RequestNodeDirectoryBrowse");

        nodes.MapGet("/{nodeId:guid}/directory-browse/{requestId:guid}", async (
            Guid nodeId, Guid requestId, WorkerManagementService service, CancellationToken ct) =>
        {
            var result = await service.GetDirectoryBrowseAsync(requestId, nodeId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireAuthorization(AuthExtensions.AdministratorPolicy).WithName("GetNodeDirectoryBrowse");
    }

    /// <summary>Session d'un compte administrateur (pas un jeton API, pas l'identité synthétique d'une authentification désactivée), avec l'en-tête anti-CSRF.</summary>
    internal static bool IsAdministratorSession(HttpContext http) =>
        http.User.Identity?.IsAuthenticated == true
        && http.User.IsInRole("admin")
        && http.User.FindFirst("auth_type")?.Value == "session"
        && http.Request.Headers[ManagementAccessHandler.CsrfHeader] == "1";

    private static void MapNodeEnrollmentEndpoints(this RouteGroupBuilder nodes)
    {
        nodes.MapPost("/enroll", async (
            WorkerEnrollmentRequest request,
            WorkerManagementService service,
            IConfiguration configuration,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var isLocal = LocalEnrollment.Matches(configuration[LocalEnrollment.SecretFileSetting],
                httpContext.Request.Headers[LocalEnrollment.HeaderName].ToString());
            // ADR 0034 : le compte administrateur du central admet le worker hors fenêtre d'enrôlement (session réelle, jamais un jeton API).
            var byAdministrator = IsAdministratorSession(httpContext);
            var outcome = await service.EnrollAsync(request, isLocal, cancellationToken, byAdministrator);
            if (outcome.HasValidationErrors) return (IResult)Results.ValidationProblem(outcome.ValidationErrors!);
            if (outcome.Worker is not null) return Results.Ok(outcome.Worker);

            return outcome.WindowOpen
                ? Results.Conflict(new { title = "Cet identifiant appartient déjà à un autre worker." })
                : Results.Json(new
                {
                    title = "L’enrôlement direct est fermé. Ouvrez la fenêtre d’enrôlement depuis l’interface ou utilisez une clé de jumelage."
                }, statusCode: StatusCodes.Status403Forbidden);
        }).AllowAnonymous().RequireRateLimiting(AuthExtensions.AnonymousPolicy).WithName("EnrollNode");

        nodes.MapPost("/enrollment-requests", async (
            CreateWorkerEnrollmentRequest request,
            WorkerManagementService service,
            AuthOptions authOptions,
            CancellationToken cancellationToken) =>
        {
            var outcome = await service.CreateEnrollmentRequestAsync(
                request, authOptions.MaxPendingPairingRequests, cancellationToken);
            return outcome.Outcome switch
            {
                CreatePairingOutcome.ValidationProblem => Results.ValidationProblem(outcome.ValidationErrors!),
                CreatePairingOutcome.Accepted => Results.Accepted(),
                CreatePairingOutcome.TooMany => Results.Json(new
                {
                    title = "Trop de demandes de jumelage sont en attente. Approuvez ou laissez expirer les demandes existantes."
                }, statusCode: StatusCodes.Status429TooManyRequests),
                _ => Results.Conflict(new { title = "Une demande de jumelage active existe déjà pour une autre identité." }),
            };
        }).AllowAnonymous().RequireRateLimiting(AuthExtensions.AnonymousPolicy).WithName("CreateWorkerEnrollmentRequest");

        nodes.MapGet("/enrollment-requests", async (
            WorkerManagementService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ListPendingPairingRequestsAsync(cancellationToken)))
            .WithName("ListPendingNodeEnrollmentRequests");

        nodes.MapPost("/enrollment-requests/{nodeId:guid}/approve", async (
            Guid nodeId,
            WorkerManagementService service,
            CancellationToken cancellationToken) =>
        {
            var node = await service.ApprovePairingRequestAsync(nodeId, cancellationToken);
            return node is null
                ? (IResult)Results.NotFound(new { title = "Cette demande est expirée ou n’existe plus." })
                : Results.Ok(node);
        }).WithName("ApprovePendingNodeEnrollmentRequest");

        nodes.MapPost("/enrollment-requests/approve", async (
            ApproveWorkerEnrollmentRequest request,
            WorkerManagementService service,
            CancellationToken cancellationToken) =>
        {
            var errors = WorkerHeartbeatValidator.Validate(request);
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            var node = await service.ApprovePairingRequestAsync(request.PairingKey, cancellationToken);
            return node is null
                ? Results.NotFound(new { title = "Cette clé de jumelage est invalide, expirée ou déjà utilisée." })
                : Results.Ok(node);
        }).WithName("ApproveWorkerEnrollmentRequest");
    }

    private static void MapNodeHeartbeatEndpoints(this RouteGroupBuilder nodes)
    {
        nodes.MapPut("/{nodeId:guid}/heartbeat", async (
            Guid nodeId,
            WorkerHeartbeatRequest request,
            WorkerManagementService service,
            IConfiguration configuration,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var providedToken = httpContext.Request.Headers["X-Node-Token"].ToString();
            var isLocal = LocalEnrollment.Matches(configuration[LocalEnrollment.SecretFileSetting],
                httpContext.Request.Headers[LocalEnrollment.HeaderName].ToString());
            var outcome = await service.RecordHeartbeatAsync(nodeId, providedToken, request, isLocal, cancellationToken);

            return outcome.Kind switch
            {
                HeartbeatOutcomeKind.Unauthorized => Results.Unauthorized(),
                HeartbeatOutcomeKind.ValidationProblem => Results.ValidationProblem(outcome.ValidationErrors!),
                HeartbeatOutcomeKind.Ok => Results.Ok(outcome.Worker),
                HeartbeatOutcomeKind.Gone => Results.Json(new { title = "Ce worker a été retiré du central." },
                    statusCode: StatusCodes.Status410Gone),
                _ => Results.Unauthorized()
            };
        }).AllowAnonymous().WithName("RecordNodeHeartbeat");

        nodes.MapPost("/{nodeId:guid}/leave", async (
            Guid nodeId,
            WorkerManagementService service,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var token = httpContext.Request.Headers["X-Node-Token"].ToString();
            if (string.IsNullOrWhiteSpace(token)) return (IResult)Results.Unauthorized();
            return await service.LeaveAsync(nodeId, token, ct) switch
            {
                "deleted" => Results.NoContent(),
                "coordination" => Results.Conflict(new
                {
                    title = "Le central ne peut pas quitter le central : il en est l'unique serveur."
                }),
                "busy" => Results.Conflict(new
                {
                    title = "Ce worker participe à un traitement ou une découverte. Attendez sa fin avant de quitter le central."
                }),
                _ => Results.Unauthorized()
            };
        }).AllowAnonymous().RequireRateLimiting(AuthExtensions.AnonymousPolicy).WithName("LeaveCluster");
    }

    private static void MapNodeLifecycleEndpoints(this RouteGroupBuilder nodes)
    {
        nodes.MapGet("", async (WorkerManagementService service, WorkerPresenceOptions options, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(options.OfflineAfter, cancellationToken)))
            .WithName("ListNodes");

        nodes.MapGet("/{nodeId:guid}", async (
            Guid nodeId, WorkerManagementService service, WorkerPresenceOptions options, CancellationToken cancellationToken) =>
        {
            var node = await service.GetAsync(nodeId, options.OfflineAfter, cancellationToken);
            return node is null ? (IResult)Results.NotFound() : Results.Ok(node);
        }).WithName("GetNode");

        nodes.MapPut("/{nodeId:guid}/draining", async (
            Guid nodeId, DrainWorkerRequest request, WorkerManagementService service, CancellationToken ct) =>
            await service.SetDrainingAsync(nodeId, request.Draining, ct) ? Results.Accepted() : Results.NotFound())
            .WithName("SetNodeDraining");

        nodes.MapPost("/{nodeId:guid}/quarantine/clear", async (Guid nodeId, WorkerManagementService service, CancellationToken ct) =>
            await service.ClearQuarantineAsync(nodeId, ct)
                ? Results.Accepted()
                : Results.NotFound(new { title = "Ce worker n'est pas en quarantaine." }))
            .WithName("ClearNodeQuarantine");

        nodes.MapDelete("/{nodeId:guid}", async (Guid nodeId, WorkerManagementService service, CancellationToken ct) =>
        {
            return await service.DeleteAsync(nodeId, ct) switch
            {
                "deleted" => (IResult)Results.NoContent(),
                "coordination" => Results.Conflict(new { title = "Le central ne peut pas être retiré : il est l'unique serveur du central." }),
                "busy" => Results.Conflict(new
                {
                    title = "Ce worker participe à un traitement ou une découverte. Attendez sa fin ou annulez les tâches avant de le supprimer."
                }),
                _ => Results.NotFound()
            };
        });

        nodes.MapGet("/activity", async (WorkerManagementService service, CancellationToken ct) =>
            Results.Ok((await service.ActiveTaskCountsAsync(ct)).ToDictionary(item => item.Key.ToString("D"), item => item.Value)))
            .WithName("ListNodesActiveTasks");

        nodes.MapGet("/{nodeId:guid}/activity", async (Guid nodeId, WorkerManagementService service, CancellationToken ct) =>
            Results.Ok(await service.ActivityAsync(nodeId, ct)));
    }

    private static void MapNodeProcessingEndpoints(this RouteGroupBuilder nodes)
    {
        nodes.MapGet("/{nodeId:guid}/processing", async (Guid nodeId, WorkerManagementService service, CancellationToken ct) =>
            await service.GetProcessingSettingsAsync(nodeId, ct) is { } settings ? (IResult)Results.Ok(settings) : Results.NotFound());

        nodes.MapPut("/{nodeId:guid}/processing", async (
            Guid nodeId, SaveWorkerProcessingSettings request, WorkerManagementService service, CancellationToken ct) =>
        {
            try
            {
                return await service.SaveProcessingSettingsAsync(nodeId, request, ct)
                    ? (IResult)Results.NoContent()
                    : Results.Conflict(new { title = "La configuration du worker a changé." });
            }
            catch (ArgumentException e) { return Results.BadRequest(new { title = e.Message }); }
        });
    }
}
