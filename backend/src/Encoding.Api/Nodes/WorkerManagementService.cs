using Encoding.Api.Infrastructure;
using Encoding.Api.Libraries;
using Encoding.Contracts.Auth;
using Encoding.Contracts.Nodes;
using Encoding.Core.Nodes;
using Microsoft.Extensions.Logging;

namespace Encoding.Api.Nodes;

public record WorkerEnrollmentOutcome(
    bool HasValidationErrors,
    IDictionary<string, string[]>? ValidationErrors,
    bool WindowOpen,
    WorkerResponse? Node);

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
    WorkerResponse? Node);

public sealed class WorkerManagementService(
    WorkerRepository repository,
    IServiceProvider services)
{
    private T? Get<T>() where T : class => services.GetService(typeof(T)) as T;

    public async Task<Guid?> CreateDirectoryBrowseAsync(Guid nodeId, string? path, CancellationToken ct)
    {
        var browser = Get<WorkerDirectoryBrowserRepository>();
        return browser is not null ? await browser.CreateAsync(nodeId, path, ct) : null;
    }

    public async Task<WorkerDirectoryBrowserResponse?> GetDirectoryBrowseAsync(Guid requestId, Guid nodeId, CancellationToken ct)
    {
        var browser = Get<WorkerDirectoryBrowserRepository>();
        return browser is not null ? await browser.GetAsync(requestId, nodeId, ct) : null;
    }

    public async Task<WorkerEnrollmentOutcome> EnrollAsync(
        WorkerEnrollmentRequest request,
        bool isLocal,
        CancellationToken ct,
        bool byAdministrator = false)
    {
        var errors = WorkerHeartbeatValidator.Validate(request);
        if (errors.Count > 0) return new(true, errors, false, null);

        var window = Get<EnrollmentWindowRepository>();
        var windowOpen = isLocal || byAdministrator || (window is null || (await window.GetAsync(ct)).IsOpen);
        var node = await repository.EnrollAsync(request, windowOpen, ct);
        // Une demande de jumelage déjà publiée pour ce worker n'a plus d'objet une fois le worker admis.
        if (node is not null) await repository.DeletePairingRequestAsync(request.Id, ct);

        if (node is not null && isLocal && Get<LocalWorkerRegistry>() is { } localNode)
            await localNode.RegisterAsync(node.Id, ct);

        if (node is not null)
        {
            return new(false, null, windowOpen, node);
        }

        return new(false, null, windowOpen, null);
    }

    public async Task<CreateEnrollmentRequestResult> CreateEnrollmentRequestAsync(
        CreateWorkerEnrollmentRequest request,
        int maxPendingPairingRequests,
        CancellationToken ct)
    {
        var errors = WorkerHeartbeatValidator.Validate(request);
        if (errors.Count > 0) return new(CreatePairingOutcome.ValidationProblem, errors);

        var result = await repository.CreatePairingRequestAsync(request, maxPendingPairingRequests, ct);
        if (result == PairingRequestResult.Created)
        {
            var firstLink = Get<FirstLinkRepository>();
            var logger = Get<ILogger<WorkerManagementService>>();
            if (firstLink is not null && await firstLink.TryConsumeAsync(ct) is { } administrator
                && await repository.ApprovePairingRequestAsync(request.Node.Id, ct) is not null)
            {
                logger?.LogWarning(
                    "Le worker {NodeId} ({NodeName}) a été accepté sans vérification au titre de la première liaison activée par {Administrator}.",
                    request.Node.Id, request.Node.Name, administrator);
            }
            return new(CreatePairingOutcome.Accepted, null);
        }

        if (result == PairingRequestResult.TooMany)
            return new(CreatePairingOutcome.TooMany, null);

        return new(CreatePairingOutcome.Conflict, null);
    }

    public Task<IReadOnlyList<PendingWorkerEnrollmentResponse>> ListPendingPairingRequestsAsync(CancellationToken ct) =>
        repository.ListPendingPairingRequestsAsync(ct);

    public async Task<bool> AuthenticateTokenAsync(Guid nodeId, string token, CancellationToken ct) =>
        !string.IsNullOrWhiteSpace(token) && await repository.AuthenticateTokenAsync(nodeId, token, ct);

    public Task<WorkerResponse?> ApprovePairingRequestAsync(Guid nodeId, CancellationToken ct) =>
        repository.ApprovePairingRequestAsync(nodeId, ct);

    public Task<WorkerResponse?> ApprovePairingRequestAsync(string pairingKey, CancellationToken ct) =>
        repository.ApprovePairingRequestAsync(pairingKey, ct);

    public async Task<HeartbeatResult> RecordHeartbeatAsync(
        Guid nodeId,
        string? providedToken,
        WorkerHeartbeatRequest request,
        bool isLocal,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(providedToken))
            return new(HeartbeatOutcomeKind.Unauthorized, null, null);

        var errors = WorkerHeartbeatValidator.Validate(request);
        if (errors.Count > 0)
            return new(HeartbeatOutcomeKind.ValidationProblem, errors, null);

        var node = await repository.RecordHeartbeatAsync(nodeId, providedToken, request, ct);
        if (node is not null)
        {
            if (isLocal && Get<LocalWorkerRegistry>() is { } localNode)
                await localNode.RegisterAsync(node.Id, ct);

            var config = Get<IConfiguration>();
            var masterUrl = config?["Central:AdvertisedApiBaseUrl"];

            return new(HeartbeatOutcomeKind.Ok, null, node with
            {
                MasterUrl = masterUrl
            });
        }

        if (await repository.IsRemovedAsync(nodeId, ct))
            return new(HeartbeatOutcomeKind.Gone, null, null);

        return new(HeartbeatOutcomeKind.Unauthorized, null, null);
    }

    public Task<string> LeaveAsync(Guid nodeId, string token, CancellationToken ct) =>
        repository.LeaveAsync(nodeId, token, ct);

    public Task<IReadOnlyList<WorkerResponse>> ListAsync(TimeSpan offlineAfter, CancellationToken ct) =>
        repository.ListAsync(offlineAfter, ct);

    public Task<WorkerResponse?> GetAsync(Guid nodeId, TimeSpan offlineAfter, CancellationToken ct) =>
        repository.GetAsync(nodeId, offlineAfter, ct);

    public Task<bool> SetDrainingAsync(Guid nodeId, bool draining, CancellationToken ct) =>
        repository.SetDrainingAsync(nodeId, draining, ct);

    public Task<bool> ClearQuarantineAsync(Guid nodeId, CancellationToken ct) =>
        repository.ClearQuarantineAsync(nodeId, ct);

    public Task<string> DeleteAsync(Guid nodeId, CancellationToken ct) =>
        repository.DeleteAsync(nodeId, ct);

    public Task<IReadOnlyDictionary<Guid, int>> ActiveTaskCountsAsync(CancellationToken ct) =>
        repository.ActiveTaskCountsAsync(ct);

    public Task<WorkerActivityResponse> ActivityAsync(Guid nodeId, CancellationToken ct) =>
        repository.ActivityAsync(nodeId, ct);

    public Task<WorkerProcessingSettings?> GetProcessingSettingsAsync(Guid nodeId, CancellationToken ct) =>
        Get<LibraryProcessingRepository>() is { } p ? p.NodeAsync(nodeId, ct) : Task.FromResult<WorkerProcessingSettings?>(null);

    public Task<bool> SaveProcessingSettingsAsync(Guid nodeId, SaveWorkerProcessingSettings request, CancellationToken ct) =>
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
