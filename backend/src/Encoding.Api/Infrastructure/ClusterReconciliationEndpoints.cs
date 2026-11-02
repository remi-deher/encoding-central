using System.Text.Json;
using Encoding.Api.Auth;
using Encoding.Core.Nodes;

namespace Encoding.Api.Infrastructure;

public sealed record ClusterReconciliationExchangeRequest(
    int ProtocolVersion,
    Guid ClusterId,
    IReadOnlyDictionary<Guid, long> KnownSequences,
    IReadOnlyList<ClusterChangeEvent> Changes,
    string? AdvertisedUrl = null,
    IReadOnlyList<string>? KnownPeers = null);

public sealed record ClusterReconciliationExchangeResponse(
    int ProtocolVersion,
    Guid ClusterId,
    IReadOnlyDictionary<Guid, long> KnownSequences,
    IReadOnlyList<ClusterChangeEvent> Changes,
    string? AdvertisedUrl = null,
    IReadOnlyList<string>? KnownPeers = null);

public static class ClusterReconciliationEndpoints
{
    public const int ProtocolVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapClusterReconciliationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/cluster/reconciliation/local-identity", (
            HttpContext http,
            IConfiguration configuration,
            ClusterPeerIdentityStore localIdentity) =>
        {
            if (!LocalEnrollment.Matches(configuration[LocalEnrollment.SecretFileSetting],
                    http.Request.Headers[LocalEnrollment.HeaderName].ToString())) return Results.Unauthorized();
            var advertisedUrl = ClusterReconciliationRepository.TryNormalizePeerUrl(configuration["Cluster:Reconciliation:AdvertisedApiBaseUrl"]);
            return Results.Ok(new Encoding.Contracts.Cluster.ClusterPeerPublicIdentity(localIdentity.PeerId, localIdentity.PublicKey, advertisedUrl));
        })
        .AllowAnonymous()
        .RequireRateLimiting(AuthExtensions.AnonymousPolicy)
        .WithTags("Cluster reconciliation")
        .WithName("GetLocalClusterPeerIdentity");

        app.MapGet("/api/v1/cluster/reconciliation/status", async (ClusterReconciliationHealth health, ClusterEntityReconciliation entities,
            Encoding.Api.Executions.TaskOwnershipRepository ownership, Encoding.Api.Executions.TaskCoordinationProjection coordination, CancellationToken ct) =>
            {
                var status = health.GetStatus();
                return Results.Ok(new { status.Enabled, status.DisabledReason, status.DiscoveryAvailable,
                    status.DiscoveryError, status.Peers, Projections = await entities.ProjectionDiagnosticsAsync(ct),
                        AttributionBlockers = await ownership.ReadinessBlockersAsync(ct), Coordination = await coordination.StatusAsync(ct) });
            })
            .RequireAuthorization()
            .WithTags("Cluster reconciliation")
            .WithName("GetClusterReconciliationStatus");

        app.MapPost("/api/v1/cluster/reconciliation/exchange", async (
            HttpContext http,
            IConfiguration configuration,
            ClusterIdentityRepository identityRepository,
            ClusterPeerIdentityStore localIdentity,
            ClusterPeerAdmissionRepository peerAdmissions,
            ClusterReconciliationRepository repository,
            ClusterEntityReconciliation entityReconciliation,
            CancellationToken ct) =>
        {
            var body = await ReadBoundedBodyAsync(http.Request, ct);
            if (body is null) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            if (!Guid.TryParse(http.Request.Headers[ClusterReconciliationSignature.PeerHeader], out var peerId)) return Results.Unauthorized();
            var identity = await identityRepository.GetAsync(ct);
            var publicKey = await peerAdmissions.GetActivePublicKeyAsync(identity.Id, peerId, ct);
            if (publicKey is null || !ClusterReconciliationSignature.Verify(publicKey, peerId,
                    http.Request.Headers[ClusterReconciliationSignature.TimeHeader],
                    http.Request.Headers[ClusterReconciliationSignature.SignatureHeader], body)) return Results.Unauthorized();
            ClusterReconciliationExchangeRequest? request;
            try { request = JsonSerializer.Deserialize<ClusterReconciliationExchangeRequest>(body, JsonOptions); }
            catch (JsonException) { return Results.BadRequest(new { title = "Requête de réconciliation invalide." }); }
            if (request is null || request.KnownSequences is null || request.Changes is null)
                return Results.BadRequest(new { title = "Requête de réconciliation incomplète." });
            if (request.ProtocolVersion != ProtocolVersion) return Results.Conflict(new { title = "Version de protocole de réconciliation incompatible." });
            if (request.KnownSequences.Count > 4096 || request.KnownSequences.Any(entry => entry.Key == Guid.Empty || entry.Value < 0))
                return Results.BadRequest(new { title = "Vecteur de réconciliation invalide." });
            if (request.Changes.Count > ClusterReconciliationRepository.MaximumBatchSize)
                return Results.BadRequest(new { title = "Lot de réconciliation trop volumineux." });

            if (request.ClusterId != identity.Id) return Results.Conflict(new { title = "Les serveurs n’appartiennent pas au même cluster." });

            var ownAdvertisedUrl = ClusterReconciliationRepository.TryNormalizePeerUrl(configuration["Cluster:Reconciliation:AdvertisedApiBaseUrl"]);
            if (!string.IsNullOrWhiteSpace(request.AdvertisedUrl)
                && ClusterReconciliationRepository.TryNormalizePeerUrl(request.AdvertisedUrl) is { } reqAdv
                && !string.Equals(reqAdv, ownAdvertisedUrl, StringComparison.OrdinalIgnoreCase))
            {
                await repository.AddPeerEndpointAsync(reqAdv, "exchange", ct);
            }
            if (request.KnownPeers is { Count: > 0 })
            {
                foreach (var peer in request.KnownPeers)
                {
                    if (ClusterReconciliationRepository.TryNormalizePeerUrl(peer) is { } normPeer
                        && !string.Equals(normPeer, ownAdvertisedUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        await repository.AddPeerEndpointAsync(normPeer, "gossip", ct);
                    }
                }
            }

            try
            {
                await repository.ApplyIncomingAsync(request.Changes, ct);
                await entityReconciliation.ApplyPendingAsync(ct);
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { title = exception.Message }); }
            catch (InvalidOperationException exception) { return Results.BadRequest(new { title = exception.Message }); }
            catch (JsonException) { return Results.BadRequest(new { title = "Événement de réconciliation mal formé." }); }

            var vector = await repository.GetVectorAsync(ct);
            var changes = (await repository.GetEventsAfterAsync(identity.Id, request.KnownSequences, ClusterReconciliationRepository.MaximumBatchSize, ct)).ToList();
            var knownPeers = await repository.GetPeerEndpointsAsync(ct);
            var responseBody = SerializeBoundedResponse(identity.Id, vector, changes, ownAdvertisedUrl, knownPeers);
            var responseTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            http.Response.Headers[ClusterReconciliationSignature.PeerHeader] = localIdentity.PeerId.ToString("D");
            http.Response.Headers[ClusterReconciliationSignature.TimeHeader] = responseTime;
            http.Response.Headers[ClusterReconciliationSignature.SignatureHeader] = ClusterReconciliationSignature.Create(localIdentity, responseTime, responseBody);
            return Results.Bytes(responseBody, "application/json");
        })
        .AllowAnonymous()
        .RequireRateLimiting(AuthExtensions.ClusterMeshPolicy)
        .WithTags("Cluster reconciliation")
        .WithName("ExchangeClusterReconciliationEvents");
    }

    private static byte[] SerializeBoundedResponse(Guid clusterId, IReadOnlyDictionary<Guid, long> vector, List<ClusterChangeEvent> changes, string? advertisedUrl, IReadOnlyList<string> knownPeers)
    {
        byte[] body;
        var peerList = knownPeers.Take(8).ToArray();
        do
        {
            body = JsonSerializer.SerializeToUtf8Bytes(new ClusterReconciliationExchangeResponse(ProtocolVersion, clusterId, vector, changes, advertisedUrl, peerList), JsonOptions);
            if (body.Length <= ClusterReconciliationSignature.MaximumBodyBytes) return body;
            if (changes.Count == 0) throw new InvalidOperationException("Le vecteur de réconciliation dépasse la taille maximale autorisée.");
            changes.RemoveAt(changes.Count - 1);
        } while (true);
    }


    private static async Task<byte[]?> ReadBoundedBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is > ClusterReconciliationSignature.MaximumBodyBytes) return null;
        await using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0) return output.ToArray();
            if (output.Length + read > ClusterReconciliationSignature.MaximumBodyBytes) return null;
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
}
