using System.Data.Common;
using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Executions;

namespace Encoding.Api.Executions;

/// <summary>
/// Traite les requêtes de coordination de tâches envoyées par un pair admis : authentification par signature,
/// puis blob, contrôle (annulation/reprise), réception, disponibilité et activation de checkpoint.
/// </summary>
public sealed class ClusterTaskPeerHandler(
    ClusterIdentityRepository clusters,
    ClusterPeerAdmissionRepository admissions,
    ClusterPeerIdentityStore identity,
    ClusterTaskCoordinator coordinator,
    TaskOwnershipRepository ownership,
    TaskCheckpointRepository checkpoints,
    TaskCoordinationProjection projection,
    RuntimeArtifactStore artifacts,
    DbDataSource db)
{
    public async Task<IResult> HandleAsync(HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        var cluster = await clusters.GetAsync(ct);
        var body = await ClusterTaskTransport.ReadAsync(request.Body, ct);
        if (body is null) return Results.StatusCode(413);
        if (!Guid.TryParse(request.Headers[ClusterReconciliationSignature.PeerHeader], out var peer)
            || await admissions.GetActivePublicKeyAsync(cluster.Id, peer, ct) is not { } key
            || !ClusterReconciliationSignature.Verify(key, peer, request.Headers[ClusterReconciliationSignature.TimeHeader], request.Headers[ClusterReconciliationSignature.SignatureHeader], body))
            return Results.Unauthorized();

        ClusterTaskPeerRequest? message;
        try { message = JsonSerializer.Deserialize<ClusterTaskPeerRequest>(body, ClusterTaskTransport.Json); }
        catch (JsonException) { return Results.BadRequest(); }
        if (message is not { Version: 2 } || message.ClusterId != cluster.Id || message.Nonce == Guid.Empty) return Results.BadRequest();

        if (message.Kind == "blob") return await ServeBlobAsync(message, ct);

        ClusterTaskPeerReply? reply;
        switch (message.Kind)
        {
            case "cancel" or "retry" or "resume":
                reply = await ControlAsync(request, cluster.Id, message, ct);
                break;
            case "receive":
                reply = await ReceiveAsync(cluster.Id, peer, message, ct);
                break;
            case "ready":
                reply = await ReadyAsync(cluster.Id, message, ct);
                break;
            case "activate" when message.Payload is { } payload:
                reply = await ActivateAsync(cluster.Id, peer, message, payload, ct);
                break;
            default:
                reply = null;
                break;
        }
        if (reply is null) return Results.BadRequest();

        var result = JsonSerializer.SerializeToUtf8Bytes(reply, ClusterTaskTransport.Json);
        ClusterTaskTransport.SignResponse(response, identity, result);
        return Results.Bytes(result, "application/json");
    }

    private async Task<IResult> ServeBlobAsync(ClusterTaskPeerRequest message, CancellationToken ct)
    {
        if (message.Digest is null || !RuntimeArtifactStore.ValidDigest(message.Digest)) return Results.BadRequest();
        var allowed = await IsKnownArtifactAsync(message.ExecutionId, message.Digest, ct)
            || await IsDeclaredByLatestCheckpointAsync(message.ExecutionId, message.Digest, ct);
        var path = artifacts.PathFor(message.ExecutionId, message.Digest);
        return allowed ? FileResults.ServeOrNotFound(path, "application/octet-stream") : Results.NotFound();
    }

    private async Task<bool> IsKnownArtifactAsync(Guid executionId, string digest, CancellationToken ct)
    {
        await using var known = db.CreateCommand("SELECT EXISTS(SELECT 1 FROM execution_artifacts WHERE execution_id=@id AND sha256=@sha)");
        known.Parameters.AddWithValue("id", executionId);
        known.Parameters.AddWithValue("sha", digest);
        return await known.ScalarBoolAsync(ct);
    }

    private async Task<bool> IsDeclaredByLatestCheckpointAsync(Guid executionId, string digest, CancellationToken ct)
    {
        await using var read = db.CreateCommand("SELECT body FROM task_checkpoints WHERE execution_id=@id ORDER BY epoch DESC LIMIT 1");
        read.Parameters.AddWithValue("id", executionId);
        if (await read.ExecuteScalarAsync(ct) is not string signed) return false;
        var envelope = JsonSerializer.Deserialize<SignedTaskOwnershipPlan>(signed, TaskCheckpointRepository.Json)!;
        var taskEvent = JsonSerializer.Deserialize<ClusterTaskEvent>(envelope.Data, TaskCheckpointRepository.Json)!;
        return checkpoints.Decode(taskEvent).Artifacts.Any(artifact => artifact.Sha256 == digest);
    }

    private async Task<ClusterTaskPeerReply?> ControlAsync(HttpRequest request, Guid clusterId, ClusterTaskPeerRequest message, CancellationToken ct)
    {
        ClusterTaskControlRequest? control;
        try { control = JsonSerializer.Deserialize<ClusterTaskControlRequest>(message.Payload ?? "", TaskCheckpointRepository.Json); }
        catch (JsonException) { return null; }
        if (control is null || control.ExecutionId == Guid.Empty) return null;

        var owner = await ownership.GetAsync(control.ExecutionId, ct);
        if (owner is null || owner.OwnerPeerId != identity.PeerId)
            return new(2, clusterId, identity.PeerId, message.Nonce, false, Detail: "Propriétaire différent ; réconcilier avant de réessayer.");

        var journal = new ExecutionRepository(db, ownership: ownership);
        Guid? created = null;
        bool success;
        if (message.Kind == "cancel") success = await journal.CancelAsync(control.ExecutionId, ct, control.ExpectedRevision);
        else
        {
            created = message.Kind == "retry" ? await journal.RetryAsync(control.ExecutionId, ct) : await journal.ResumeAsync(control.ExecutionId, ct);
            success = created is not null;
        }
        if (success) request.HttpContext.RequestServices.GetService<ExecutionStreamBroadcaster>()?.Publish(control.ExecutionId);
        return new(2, clusterId, identity.PeerId, message.Nonce, success, created?.ToString("D"));
    }

    private async Task<ClusterTaskPeerReply> ReceiveAsync(Guid clusterId, Guid peer, ClusterTaskPeerRequest message, CancellationToken ct)
    {
        try { return await coordinator.ReceiveAsync(peer, message, ct); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or System.Security.Cryptography.CryptographicException or HttpRequestException)
        {
            return new(2, clusterId, identity.PeerId, message.Nonce, false, Detail: "Réception refusée : checkpoint ou artefacts indisponibles ou corrompus.");
        }
    }

    private async Task<ClusterTaskPeerReply> ReadyAsync(Guid clusterId, ClusterTaskPeerRequest message, CancellationToken ct)
    {
        await ownership.ProjectAsync(ct);
        await projection.ProjectAsync(ct);
        await using var pending = db.CreateCommand($"SELECT {TaskOwnershipRepository.BlockingPendingSql}");
        var idle = !await pending.ScalarBoolAsync(ct);
        var vector = JsonSerializer.Serialize(await new ClusterReconciliationRepository(db).GetVectorAsync(ct), TaskCheckpointRepository.Json);
        return new(2, clusterId, identity.PeerId, message.Nonce, idle, vector);
    }

    private async Task<ClusterTaskPeerReply?> ActivateAsync(Guid clusterId, Guid peer, ClusterTaskPeerRequest message, string payload, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var (taskEvent, _) = await checkpoints.VerifyAsync(payload, connection, transaction, ct);
        if (taskEvent is null || taskEvent.AuthorPeerId != peer || taskEvent.Kind != "activate") return null;
        await ClusterReconciliationRepository.SetRegisterInTransactionAsync(TaskCheckpointRepository.Prefix + taskEvent.EventId.ToString("N"), payload, connection, transaction, ct);
        await transaction.CommitAsync(ct);
        await projection.ProjectAsync(ct);
        return new(2, clusterId, identity.PeerId, message.Nonce, true);
    }
}
