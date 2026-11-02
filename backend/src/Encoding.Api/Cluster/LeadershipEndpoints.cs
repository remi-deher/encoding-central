using Encoding.Api.Infrastructure;
using Encoding.Api.Auth;
using Encoding.Api.Nodes;
using Encoding.Contracts.Cluster;
using Microsoft.AspNetCore.Mvc;

namespace Encoding.Api.Cluster;

public static class LeadershipEndpoints
{
    public static void MapLeadershipEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/cluster/leadership").WithTags("Leadership");

        // GET /api/v1/cluster/leadership/status
        group.MapGet("/status", async (
            LeadershipRepository repository,
            NodePresenceOptions options,
            CancellationToken ct) =>
        {
            return Results.Ok(await repository.GetStatusAsync(options.OfflineAfter, ct));
        }).AllowAnonymous().WithName("GetLeadershipStatus");

        // POST /api/v1/cluster/leadership/transfer
        group.MapPost("/transfer", async (
            LeadershipTransferRequest request,
            LeadershipHandoffService handoff,
            CancellationToken ct) =>
        {
            var result = await handoff.InitiateTransferAsync(request.TargetNodeId, request.DrainTimeoutSeconds, request.TargetNodeUrl, ct);
            return result.Success ? Results.Ok(result) : Results.Conflict(result);
        }).RequireAuthorization(AuthExtensions.AdministratorPolicy).WithName("InitiateLeadershipTransfer");

        // GET /api/v1/cluster/leadership/snapshot/{transferId:guid}
        group.MapGet("/snapshot/{transferId:guid}", async (
            Guid transferId,
            LeadershipRepository repository,
            NodeRepository nodeRepo,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var token = httpContext.Request.Headers["X-Node-Token"].ToString();
            if (string.IsNullOrWhiteSpace(token)) return Results.Unauthorized();

            var transfer = await repository.GetTransferByIdAsync(transferId, ct);
            if (transfer is null || transfer.State != "snapshot_ready" || string.IsNullOrEmpty(transfer.SnapshotPath))
                return Results.NotFound(new { title = "Instantané de transfert introuvable ou expiré." });

            if (!await nodeRepo.AuthenticateTokenAsync(transfer.TargetNodeId, token, ct))
                return Results.Forbid();

            if (!FileResults.Exists(transfer.SnapshotPath))
                return Results.NotFound(new { title = "Le fichier d'instantané n'existe plus sur le serveur." });

            return Results.File(transfer.SnapshotPath, "application/x-sqlite3", enableRangeProcessing: true);
        }).AllowAnonymous().WithName("DownloadTransferSnapshot");

        // POST /api/v1/cluster/leadership/transfer/{transferId:guid}/ack
        group.MapPost("/transfer/{transferId:guid}/ack", async (
            Guid transferId,
            LeadershipRepository repository,
            LeadershipHandoffService handoff,
            NodeRepository nodeRepo,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var token = httpContext.Request.Headers["X-Node-Token"].ToString();
            if (string.IsNullOrWhiteSpace(token)) return Results.Unauthorized();

            var transfer = await repository.GetTransferByIdAsync(transferId, ct);
            if (transfer is null) return Results.NotFound();

            if (!await nodeRepo.AuthenticateTokenAsync(transfer.TargetNodeId, token, ct))
                return Results.Forbid();

            var success = await handoff.AcknowledgeAdoptionAsync(transferId, ct);
            return success ? Results.NoContent() : Results.Conflict(new { title = "La passation n'a pas pu être finalisée." });
        }).AllowAnonymous().WithName("AcknowledgeLeadershipAdoption");

        // GET /api/v1/cluster/leadership/backup/latest
        group.MapGet("/backup/latest", async (
            LeadershipRepository repository,
            NodeRepository nodeRepo,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var token = httpContext.Request.Headers["X-Node-Token"].ToString();
            if (string.IsNullOrWhiteSpace(token) || !await nodeRepo.AuthenticateTokenAsync(token, ct))
                return Results.Unauthorized();

            var backup = await repository.GetLatestBackupAsync(ct);
            if (backup is null || !FileResults.Exists(backup.FilePath))
                return Results.NotFound(new { title = "Aucune sauvegarde miroir disponible." });

            return Results.File(backup.FilePath, "application/x-sqlite3", enableRangeProcessing: true);
        }).AllowAnonymous().WithName("DownloadLatestDatabaseBackup");

        // GET /api/v1/cluster/leadership/keys
        group.MapGet("/keys", async (
            LeadershipHandoffService handoff,
            NodeRepository nodeRepo,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var token = httpContext.Request.Headers["X-Node-Token"].ToString();
            if (string.IsNullOrWhiteSpace(token) || !await nodeRepo.AuthenticateTokenAsync(token, ct))
                return Results.Unauthorized();

            return Results.Ok(handoff.GetKeyBundle());
        }).AllowAnonymous().WithName("GetLeadershipDataProtectionKeys");

        // PUT /api/v1/cluster/leadership/backup-interval
        group.MapPut("/backup-interval", async (
            [FromBody] BackupIntervalRequest request,
            LeadershipRepository repository,
            CancellationToken ct) =>
        {
            if (request.Minutes < 0) return Results.BadRequest(new { title = "L'intervalle doit être positif ou nul (0 pour désactiver)." });
            await repository.SetBackupIntervalMinutesAsync(request.Minutes, ct);
            return Results.NoContent();
        }).RequireAuthorization(AuthExtensions.AdministratorPolicy).WithName("SetDatabaseBackupInterval");
    }
}

public sealed record BackupIntervalRequest(int Minutes);
