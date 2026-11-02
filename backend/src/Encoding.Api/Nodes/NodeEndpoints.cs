using Encoding.Api.Auth;
using Encoding.Api.Infrastructure;
using Encoding.Contracts.Nodes;
using Encoding.Core.Nodes;

namespace Encoding.Api.Nodes;

public static class NodeEndpoints
{
    public static void MapNodeEndpoints(this WebApplication app)
    {
        var nodes = app.MapGroup("/api/v1/nodes").WithTags("Nodes");
        nodes.MapNodeDirectoryBrowseEndpoints();
        nodes.MapNodeEnrollmentEndpoints();
        nodes.MapNodeHeartbeatEndpoints();
        nodes.MapNodeLifecycleEndpoints();
        nodes.MapNodeProcessingEndpoints();
    }

    private static void MapNodeDirectoryBrowseEndpoints(this RouteGroupBuilder nodes)
    {
        nodes.MapPost("/{nodeId:guid}/directory-browse", async (
            Guid nodeId, NodeDirectoryBrowserRequest request, NodeManagementService service, CancellationToken ct) =>
        {
            if (await service.CreateDirectoryBrowseAsync(nodeId, request.Path, ct) is not { } id) return Results.NotFound();
            return Results.Accepted($"/api/v1/nodes/{nodeId:D}/directory-browse/{id:D}", new { id });
        }).RequireAuthorization(AuthExtensions.AdministratorPolicy)
          .RequireRateLimiting(AuthExtensions.HeavyOperationsPolicy)
          .WithName("RequestNodeDirectoryBrowse");

        nodes.MapGet("/{nodeId:guid}/directory-browse/{requestId:guid}", async (
            Guid nodeId, Guid requestId, NodeManagementService service, CancellationToken ct) =>
        {
            var result = await service.GetDirectoryBrowseAsync(requestId, nodeId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireAuthorization(AuthExtensions.AdministratorPolicy).WithName("GetNodeDirectoryBrowse");
    }

    private static void MapNodeEnrollmentEndpoints(this RouteGroupBuilder nodes)
    {
        nodes.MapPost("/enroll", async (
            NodeEnrollmentRequest request,
            NodeManagementService service,
            IConfiguration configuration,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var isLocal = LocalEnrollment.Matches(configuration[LocalEnrollment.SecretFileSetting],
                httpContext.Request.Headers[LocalEnrollment.HeaderName].ToString());
            var outcome = await service.EnrollAsync(request, isLocal, cancellationToken);
            if (outcome.HasValidationErrors) return (IResult)Results.ValidationProblem(outcome.ValidationErrors!);
            if (outcome.Node is not null) return Results.Ok(outcome.Node);

            return outcome.WindowOpen
                ? Results.Conflict(new { title = "Cet identifiant appartient déjà à un autre nœud." })
                : Results.Json(new
                {
                    title = "L’enrôlement direct est fermé. Ouvrez la fenêtre d’enrôlement depuis l’interface ou utilisez une clé de jumelage."
                }, statusCode: StatusCodes.Status403Forbidden);
        }).AllowAnonymous().RequireRateLimiting(AuthExtensions.AnonymousPolicy).WithName("EnrollNode");

        nodes.MapPost("/enrollment-requests", async (
            CreateNodeEnrollmentRequest request,
            NodeManagementService service,
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
        }).AllowAnonymous().RequireRateLimiting(AuthExtensions.AnonymousPolicy).WithName("CreateNodeEnrollmentRequest");

        nodes.MapGet("/enrollment-requests", async (
            NodeManagementService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ListPendingPairingRequestsAsync(cancellationToken)))
            .WithName("ListPendingNodeEnrollmentRequests");

        nodes.MapGet("/{nodeId:guid}/cluster-bootstrap", async (
            Guid nodeId,
            NodeManagementService service,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var token = httpContext.Request.Headers["X-Node-Token"].ToString();
            if (!await service.AuthenticateTokenAsync(nodeId, token, cancellationToken))
                return (IResult)Results.Unauthorized();
            var bootstrap = await service.GetClusterBootstrapAsync(nodeId, cancellationToken);
            return bootstrap is null
                ? Results.NotFound(new { title = "Aucune admission de serveur n’est disponible pour ce nœud." })
                : Results.Ok(bootstrap);
        }).AllowAnonymous().RequireRateLimiting(AuthExtensions.AnonymousPolicy).WithName("GetClusterBootstrapForNode");

        nodes.MapPost("/enrollment-requests/{nodeId:guid}/approve", async (
            Guid nodeId,
            NodeManagementService service,
            CancellationToken cancellationToken) =>
        {
            var node = await service.ApprovePairingRequestAsync(nodeId, cancellationToken);
            return node is null
                ? (IResult)Results.NotFound(new { title = "Cette demande est expirée ou n’existe plus." })
                : Results.Ok(node);
        }).WithName("ApprovePendingNodeEnrollmentRequest");

        nodes.MapPost("/enrollment-requests/approve", async (
            ApproveNodeEnrollmentRequest request,
            NodeManagementService service,
            CancellationToken cancellationToken) =>
        {
            var errors = NodeHeartbeatValidator.Validate(request);
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            var node = await service.ApprovePairingRequestAsync(request.PairingKey, cancellationToken);
            return node is null
                ? Results.NotFound(new { title = "Cette clé de jumelage est invalide, expirée ou déjà utilisée." })
                : Results.Ok(node);
        }).WithName("ApproveNodeEnrollmentRequest");
    }

    private static void MapNodeHeartbeatEndpoints(this RouteGroupBuilder nodes)
    {
        nodes.MapPut("/{nodeId:guid}/heartbeat", async (
            Guid nodeId,
            NodeHeartbeatRequest request,
            NodeManagementService service,
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
                HeartbeatOutcomeKind.Ok => Results.Ok(outcome.Node),
                HeartbeatOutcomeKind.Gone => Results.Json(new { title = "Ce nœud a été retiré du cluster." },
                    statusCode: StatusCodes.Status410Gone),
                _ => Results.Unauthorized()
            };
        }).AllowAnonymous().WithName("RecordNodeHeartbeat");

        nodes.MapPost("/{nodeId:guid}/leave", async (
            Guid nodeId,
            NodeManagementService service,
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
                    title = "Départ du serveur différé : réconcilier les pairs et déplacer ou terminer ses tâches."
                }),
                "busy" => Results.Conflict(new
                {
                    title = "Ce nœud participe à un traitement ou une découverte. Attendez sa fin avant de quitter le cluster."
                }),
                _ => Results.Unauthorized()
            };
        }).AllowAnonymous().RequireRateLimiting(AuthExtensions.AnonymousPolicy).WithName("LeaveCluster");
    }

    private static void MapNodeLifecycleEndpoints(this RouteGroupBuilder nodes)
    {
        nodes.MapGet("", async (NodeManagementService service, NodePresenceOptions options, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(options.OfflineAfter, cancellationToken)))
            .WithName("ListNodes");

        nodes.MapGet("/{nodeId:guid}", async (
            Guid nodeId, NodeManagementService service, NodePresenceOptions options, CancellationToken cancellationToken) =>
        {
            var node = await service.GetAsync(nodeId, options.OfflineAfter, cancellationToken);
            return node is null ? (IResult)Results.NotFound() : Results.Ok(node);
        }).WithName("GetNode");

        nodes.MapPut("/{nodeId:guid}/mode", async (
            Guid nodeId, ChangeNodeModeRequest request, NodeManagementService service, NodePresenceOptions options, CancellationToken ct) =>
        {
            if (!request.Server && !request.Worker)
                return (IResult)Results.BadRequest(new { title = "Le nœud doit garder au moins une fonction : serveur ou worker." });
            return await service.RequestModeAsync(nodeId, request.Server, request.Worker, options.OfflineAfter, ct) switch
            {
                NodeRepository.ModeRequestResult.NotFound => Results.NotFound(),
                NodeRepository.ModeRequestResult.CoordinationUnavailable => Results.Conflict(new
                {
                    title = "Modification de topologie différée : réconcilier les serveurs et vider les files possédées."
                }),
                NodeRepository.ModeRequestResult.LastServer => Results.Conflict(new
                {
                    title = "Ce nœud est le dernier serveur en ligne : sa fonction serveur ne peut pas être retirée."
                }),
                _ => Results.Accepted()
            };
        }).WithName("RequestNodeMode");

        nodes.MapPut("/{nodeId:guid}/draining", async (
            Guid nodeId, DrainNodeRequest request, NodeManagementService service, CancellationToken ct) =>
            await service.SetDrainingAsync(nodeId, request.Draining, ct) ? Results.Accepted() : Results.NotFound())
            .WithName("SetNodeDraining");

        nodes.MapPost("/{nodeId:guid}/quarantine/clear", async (Guid nodeId, NodeManagementService service, CancellationToken ct) =>
            await service.ClearQuarantineAsync(nodeId, ct)
                ? Results.Accepted()
                : Results.NotFound(new { title = "Ce nœud n'est pas en quarantaine." }))
            .WithName("ClearNodeQuarantine");

        nodes.MapDelete("/{nodeId:guid}", async (Guid nodeId, NodeManagementService service, CancellationToken ct) =>
        {
            return await service.DeleteAsync(nodeId, ct) switch
            {
                "deleted" => (IResult)Results.NoContent(),
                "coordination" => Results.Conflict(new { title = "Retrait différé : réconcilier les serveurs du cluster." }),
                "busy" => Results.Conflict(new
                {
                    title = "Ce nœud participe à un traitement ou une découverte. Attendez sa fin ou annulez les tâches avant de le supprimer."
                }),
                _ => Results.NotFound()
            };
        });

        nodes.MapGet("/activity", async (NodeManagementService service, CancellationToken ct) =>
            Results.Ok((await service.ActiveTaskCountsAsync(ct)).ToDictionary(item => item.Key.ToString("D"), item => item.Value)))
            .WithName("ListNodesActiveTasks");

        nodes.MapGet("/{nodeId:guid}/activity", async (Guid nodeId, NodeManagementService service, CancellationToken ct) =>
            Results.Ok(await service.ActivityAsync(nodeId, ct)));
    }

    private static void MapNodeProcessingEndpoints(this RouteGroupBuilder nodes)
    {
        nodes.MapGet("/{nodeId:guid}/processing", async (Guid nodeId, NodeManagementService service, CancellationToken ct) =>
            await service.GetProcessingSettingsAsync(nodeId, ct) is { } settings ? (IResult)Results.Ok(settings) : Results.NotFound());

        nodes.MapPut("/{nodeId:guid}/processing", async (
            Guid nodeId, SaveNodeProcessingSettings request, NodeManagementService service, CancellationToken ct) =>
        {
            try
            {
                return await service.SaveProcessingSettingsAsync(nodeId, request, ct)
                    ? (IResult)Results.NoContent()
                    : Results.Conflict(new { title = "La configuration du nœud a changé." });
            }
            catch (ArgumentException e) { return Results.BadRequest(new { title = e.Message }); }
        });
    }
}
