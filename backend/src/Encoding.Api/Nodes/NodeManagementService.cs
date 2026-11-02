using Encoding.Api.Infrastructure;
using Encoding.Api.Libraries;
using Encoding.Contracts.Auth;
using Encoding.Contracts.Cluster;
using Encoding.Contracts.Nodes;
using Encoding.Core.Nodes;
using Microsoft.Extensions.Logging;

namespace Encoding.Api.Nodes;

public record NodeEnrollmentOutcome(
    bool HasValidationErrors,
    IDictionary<string, string[]>? ValidationErrors,
    bool WindowOpen,
    NodeResponse? Node);

public enum CreatePairingOutcome
{
    ValidationProblem,
    Accepted,
    TooMany,
    Conflict
}

public record CreateEnrollmentRequestResult(
    CreatePairingOutcome Outcome,
    IDictionary<string, string[]>? ValidationErrors);

public enum HeartbeatOutcomeKind
{
    Unauthorized,
    ValidationProblem,
    Ok,
    Gone
}

public record HeartbeatResult(
    HeartbeatOutcomeKind Kind,
    IDictionary<string, string[]>? ValidationErrors,
    NodeResponse? Node);

public sealed class NodeManagementService(
    NodeRepository repository,
    IServiceProvider services)
{
    private T? Get<T>() where T : class => services.GetService(typeof(T)) as T;

    public async Task<Guid?> CreateDirectoryBrowseAsync(Guid nodeId, string? path, CancellationToken ct)
    {
        var browser = Get<NodeDirectoryBrowserRepository>();
        return browser is not null ? await browser.CreateAsync(nodeId, path, ct) : null;
    }

    public async Task<NodeDirectoryBrowserResponse?> GetDirectoryBrowseAsync(Guid requestId, Guid nodeId, CancellationToken ct)
    {
        var browser = Get<NodeDirectoryBrowserRepository>();
        return browser is not null ? await browser.GetAsync(requestId, nodeId, ct) : null;
    }

    public async Task<NodeEnrollmentOutcome> EnrollAsync(
        NodeEnrollmentRequest request,
        bool isLocal,
        CancellationToken ct)
    {
        var errors = NodeHeartbeatValidator.Validate(request);
        if (errors.Count > 0) return new(true, errors, false, null);

        var identityRepo = Get<ClusterIdentityRepository>();
        var dataKeyStore = Get<ClusterDataKeyStore>();
        var peerAdmissions = Get<ClusterPeerAdmissionRepository>();
        if (request.ClusterPeerId is not null && identityRepo is not null && dataKeyStore is not null && peerAdmissions is not null)
        {
            var identity = await identityRepo.GetAsync(ct);
            if (!dataKeyStore.HasKey(identity.Id))
                await peerAdmissions.EnsureRootAdmissionAsync(identity.Id, ct);
        }

        var window = Get<EnrollmentWindowRepository>();
        var windowOpen = isLocal || (window is null || (await window.GetAsync(ct)).IsOpen);
        var node = await repository.EnrollAsync(request, windowOpen, ct);

        if (node is not null && isLocal && Get<LocalNodeRegistry>() is { } localNode)
            await localNode.RegisterAsync(node.Id, ct);

        if (node is not null)
        {
            if (request.ClusterPeerId is { } peerId && request.ClusterPeerPublicKey is { } publicKey && peerAdmissions is not null)
                await peerAdmissions.CreateDirectApprovalGrantAsync(node.Id, peerId, publicKey, ct);

            if (request.ClusterPeerId is not null && request.ClusterPeerPublicKey is not null
                && !string.IsNullOrWhiteSpace(request.ClusterPeerReconciliationUrl)
                && Get<ClusterReconciliationRepository>() is { } reconciliation)
            {
                await reconciliation.AddPeerEndpointAsync(request.ClusterPeerReconciliationUrl, "enroll", ct);
            }

            var bootstrap = peerAdmissions is not null
                ? await peerAdmissions.GetBootstrapForNodeAsync(node.Id, ct)
                : null;
            return new(false, null, windowOpen, node with { ClusterBootstrap = bootstrap });
        }

        return new(false, null, windowOpen, null);
    }

    public async Task<CreateEnrollmentRequestResult> CreateEnrollmentRequestAsync(
        CreateNodeEnrollmentRequest request,
        int maxPendingPairingRequests,
        CancellationToken ct)
    {
        var errors = NodeHeartbeatValidator.Validate(request);
        if (errors.Count > 0) return new(CreatePairingOutcome.ValidationProblem, errors);

        var identityRepo = Get<ClusterIdentityRepository>();
        var dataKeyStore = Get<ClusterDataKeyStore>();
        var peerAdmissions = Get<ClusterPeerAdmissionRepository>();
        if (request.Node.ClusterPeerId is not null && identityRepo is not null && dataKeyStore is not null && peerAdmissions is not null)
        {
            var identity = await identityRepo.GetAsync(ct);
            if (!dataKeyStore.HasKey(identity.Id))
                await peerAdmissions.EnsureRootAdmissionAsync(identity.Id, ct);
        }

        var result = await repository.CreatePairingRequestAsync(request, maxPendingPairingRequests, ct);
        if (result == PairingRequestResult.Created)
        {
            var firstLink = Get<FirstLinkRepository>();
            var logger = Get<ILogger<NodeManagementService>>();
            if (firstLink is not null && await firstLink.TryConsumeAsync(ct) is { } administrator
                && await repository.ApprovePairingRequestAsync(request.Node.Id, ct) is not null)
            {
                logger?.LogWarning(
                    "Le nœud {NodeId} ({NodeName}) a été accepté sans vérification au titre de la première liaison activée par {Administrator}.",
                    request.Node.Id, request.Node.Name, administrator);
            }
            return new(CreatePairingOutcome.Accepted, null);
        }

        if (result == PairingRequestResult.TooMany)
            return new(CreatePairingOutcome.TooMany, null);

        return new(CreatePairingOutcome.Conflict, null);
    }

    public Task<IReadOnlyList<PendingNodeEnrollmentResponse>> ListPendingPairingRequestsAsync(CancellationToken ct) =>
        repository.ListPendingPairingRequestsAsync(ct);

    public async Task<bool> AuthenticateTokenAsync(Guid nodeId, string token, CancellationToken ct) =>
        !string.IsNullOrWhiteSpace(token) && await repository.AuthenticateTokenAsync(nodeId, token, ct);

    public async Task<ClusterJoinBootstrap?> GetClusterBootstrapAsync(Guid nodeId, CancellationToken ct) =>
        Get<ClusterPeerAdmissionRepository>() is { } peerAdmissions
            ? await peerAdmissions.GetBootstrapForNodeAsync(nodeId, ct)
            : null;

    public Task<NodeResponse?> ApprovePairingRequestAsync(Guid nodeId, CancellationToken ct) =>
        repository.ApprovePairingRequestAsync(nodeId, ct);

    public Task<NodeResponse?> ApprovePairingRequestAsync(string pairingKey, CancellationToken ct) =>
        repository.ApprovePairingRequestAsync(pairingKey, ct);

    public async Task<HeartbeatResult> RecordHeartbeatAsync(
        Guid nodeId,
        string? providedToken,
        NodeHeartbeatRequest request,
        bool isLocal,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(providedToken))
            return new(HeartbeatOutcomeKind.Unauthorized, null, null);

        var errors = NodeHeartbeatValidator.Validate(request);
        if (errors.Count > 0)
            return new(HeartbeatOutcomeKind.ValidationProblem, errors, null);

        var node = await repository.RecordHeartbeatAsync(nodeId, providedToken, request, ct);
        if (node is not null)
        {
            if (isLocal && Get<LocalNodeRegistry>() is { } localNode)
                await localNode.RegisterAsync(node.Id, ct);

            var config = Get<IConfiguration>();
            var serverUrls = new List<string>();
            if (config is not null
                && ClusterReconciliationRepository.TryNormalizePeerUrl(config["Cluster:Reconciliation:AdvertisedApiBaseUrl"]) is { } advertised)
            {
                serverUrls.Add(advertised);
            }

            if (Get<ClusterReconciliationRepository>() is { } reconciliation)
            {
                foreach (var peerUrl in await reconciliation.GetPeerEndpointsAsync(ct))
                {
                    if (!serverUrls.Contains(peerUrl, StringComparer.OrdinalIgnoreCase))
                        serverUrls.Add(peerUrl);
                }
            }

            var leadership = Get<Encoding.Api.Cluster.LeadershipRepository>();
            Encoding.Contracts.Cluster.LeadershipHandoffInstruction? handoff = null;
            var desiredRoles = node.DesiredRoles;
            if (leadership is not null)
            {
                var activeTransfer = await leadership.GetActiveTransferForNodeAsync(nodeId, ct);
                if (activeTransfer is not null)
                {
                    handoff = new Encoding.Contracts.Cluster.LeadershipHandoffInstruction(
                        activeTransfer.Id,
                        $"/api/v1/cluster/leadership/snapshot/{activeTransfer.Id:D}",
                        activeTransfer.SnapshotSha256 ?? string.Empty,
                        activeTransfer.CreatedAt);
                    desiredRoles = [NodeRole.Server, NodeRole.Worker];
                }
            }

            Encoding.Contracts.Cluster.ClusterDatabaseBackupNotification? backupNotification = null;
            if (leadership is not null)
            {
                var latestBackup = await leadership.GetLatestBackupAsync(ct);
                if (latestBackup is not null)
                {
                    backupNotification = new(
                        latestBackup.Digest,
                        latestBackup.SizeBytes,
                        latestBackup.CreatedAt,
                        "/api/v1/cluster/leadership/backup/latest");
                }
            }

            string? masterUrl = null;
            if (leadership is not null)
                masterUrl = await leadership.GetMasterUrlAsync(ct);
            if (string.IsNullOrWhiteSpace(masterUrl) && config is not null)
                masterUrl = config["Cluster:AdvertisedApiBaseUrl"];

            return new(HeartbeatOutcomeKind.Ok, null, node with
            {
                ClusterServerUrls = serverUrls,
                DesiredRoles = desiredRoles,
                LatestBackup = backupNotification,
                LeadershipHandoff = handoff,
                MasterUrl = masterUrl
            });
        }

        if (await repository.IsRemovedAsync(nodeId, ct))
            return new(HeartbeatOutcomeKind.Gone, null, null);

        return new(HeartbeatOutcomeKind.Unauthorized, null, null);
    }

    public Task<string> LeaveAsync(Guid nodeId, string token, CancellationToken ct) =>
        repository.LeaveAsync(nodeId, token, ct);

    public Task<IReadOnlyList<NodeResponse>> ListAsync(TimeSpan offlineAfter, CancellationToken ct) =>
        repository.ListAsync(offlineAfter, ct);

    public Task<NodeResponse?> GetAsync(Guid nodeId, TimeSpan offlineAfter, CancellationToken ct) =>
        repository.GetAsync(nodeId, offlineAfter, ct);

    public Task<NodeRepository.ModeRequestResult> RequestModeAsync(
        Guid nodeId,
        bool server,
        bool worker,
        TimeSpan offlineAfter,
        CancellationToken ct) =>
        repository.RequestModeAsync(nodeId, server, worker, offlineAfter, ct);

    public Task<bool> SetDrainingAsync(Guid nodeId, bool draining, CancellationToken ct) =>
        repository.SetDrainingAsync(nodeId, draining, ct);

    public Task<bool> ClearQuarantineAsync(Guid nodeId, CancellationToken ct) =>
        repository.ClearQuarantineAsync(nodeId, ct);

    public Task<string> DeleteAsync(Guid nodeId, CancellationToken ct) =>
        repository.DeleteAsync(nodeId, ct);

    public Task<IReadOnlyDictionary<Guid, int>> ActiveTaskCountsAsync(CancellationToken ct) =>
        repository.ActiveTaskCountsAsync(ct);

    public Task<NodeActivityResponse> ActivityAsync(Guid nodeId, CancellationToken ct) =>
        repository.ActivityAsync(nodeId, ct);

    public Task<NodeProcessingSettings?> GetProcessingSettingsAsync(Guid nodeId, CancellationToken ct) =>
        Get<LibraryProcessingRepository>() is { } p ? p.NodeAsync(nodeId, ct) : Task.FromResult<NodeProcessingSettings?>(null);

    public Task<bool> SaveProcessingSettingsAsync(Guid nodeId, SaveNodeProcessingSettings request, CancellationToken ct) =>
        Get<LibraryProcessingRepository>() is { } p ? p.SaveNodeAsync(nodeId, request, ct) : Task.FromResult(false);

    public const int MaximumEnrollmentWindowMinutes = EnrollmentWindowRepository.MaximumMinutes;
    public const int MaximumFirstLinkMinutes = FirstLinkRepository.MaximumMinutes;

    public async Task<EnrollmentWindowResponse> GetEnrollmentWindowAsync(CancellationToken ct)
    {
        var window = Get<EnrollmentWindowRepository>() ?? throw new InvalidOperationException("EnrollmentWindowRepository non disponible.");
        return await window.GetAsync(ct);
    }

    public async Task<EnrollmentWindowResponse> OpenEnrollmentWindowAsync(int minutes, CancellationToken ct)
    {
        var window = Get<EnrollmentWindowRepository>() ?? throw new InvalidOperationException("EnrollmentWindowRepository non disponible.");
        return await window.OpenAsync(minutes, ct);
    }

    public async Task CloseEnrollmentWindowAsync(CancellationToken ct)
    {
        var window = Get<EnrollmentWindowRepository>();
        if (window is not null)
            await window.CloseAsync(ct);
    }

    public async Task<FirstLinkResponse> GetFirstLinkAcceptanceAsync(CancellationToken ct)
    {
        var firstLink = Get<FirstLinkRepository>() ?? throw new InvalidOperationException("FirstLinkRepository non disponible.");
        return await firstLink.GetAsync(ct);
    }

    public async Task<FirstLinkResponse> EnableFirstLinkAcceptanceAsync(int minutes, string administrator, CancellationToken ct)
    {
        var firstLink = Get<FirstLinkRepository>() ?? throw new InvalidOperationException("FirstLinkRepository non disponible.");
        var logger = Get<ILogger<FirstLinkRepository>>();
        logger?.LogWarning("Acceptation de la première liaison activée par {Administrator} pour {Minutes} minute(s).", administrator, minutes);
        return await firstLink.EnableAsync(minutes, administrator, ct);
    }

    public async Task DisableFirstLinkAcceptanceAsync(CancellationToken ct)
    {
        var firstLink = Get<FirstLinkRepository>();
        if (firstLink is not null)
            await firstLink.DisableAsync(ct);
    }
}
