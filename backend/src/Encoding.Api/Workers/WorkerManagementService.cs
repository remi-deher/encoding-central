using Encoding.Api.Infrastructure;
using Encoding.Api.Libraries;
using Encoding.Contracts.Auth;
using Encoding.Contracts.Workers;
using Encoding.Core.Workers;
using Microsoft.Extensions.Logging;

namespace Encoding.Api.Workers;

public record WorkerEnrollmentOutcome(
    bool HasValidationErrors,
    IDictionary<string, string[]>? ValidationErrors,
    bool WindowOpen,
    WorkerResponse? Worker);

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
    WorkerResponse? Worker);

public sealed class WorkerManagementService(
    WorkerRepository repository,
    IServiceProvider services)
{
    private T? Get<T>() where T : class => services.GetService(typeof(T)) as T;

    public async Task<Guid?> CreateDirectoryBrowseAsync(Guid workerId, string? path, CancellationToken ct)
    {
        var browser = Get<WorkerDirectoryBrowserRepository>();
        return browser is not null ? await browser.CreateAsync(workerId, path, ct) : null;
    }

    public async Task<WorkerDirectoryBrowserResponse?> GetDirectoryBrowseAsync(Guid requestId, Guid workerId, CancellationToken ct)
    {
        var browser = Get<WorkerDirectoryBrowserRepository>();
        return browser is not null ? await browser.GetAsync(requestId, workerId, ct) : null;
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
        var worker = await repository.EnrollAsync(request, windowOpen, ct);
        // Une demande de jumelage déjà publiée pour ce worker n'a plus d'objet une fois le worker admis.
        if (worker is not null) await repository.DeletePairingRequestAsync(request.Id, ct);

        if (worker is not null && isLocal && Get<LocalWorkerRegistry>() is { } localWorker)
            await localWorker.RegisterAsync(worker.Id, ct);

        if (worker is not null)
        {
            return new(false, null, windowOpen, worker);
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
                && await repository.ApprovePairingRequestAsync(request.Worker.Id, ct) is not null)
            {
                logger?.LogWarning(
                    "Le worker {NodeId} ({NodeName}) a été accepté sans vérification au titre de la première liaison activée par {Administrator}.",
                    request.Worker.Id, request.Worker.Name, administrator);
            }
            return new(CreatePairingOutcome.Accepted, null);
        }

        if (result == PairingRequestResult.TooMany)
            return new(CreatePairingOutcome.TooMany, null);

        return new(CreatePairingOutcome.Conflict, null);
    }

    public Task<IReadOnlyList<PendingWorkerEnrollmentResponse>> ListPendingPairingRequestsAsync(CancellationToken ct) =>
        repository.ListPendingPairingRequestsAsync(ct);

    public async Task<bool> AuthenticateTokenAsync(Guid workerId, string token, CancellationToken ct) =>
        !string.IsNullOrWhiteSpace(token) && await repository.AuthenticateTokenAsync(workerId, token, ct);

    public Task<WorkerResponse?> ApprovePairingRequestAsync(Guid workerId, CancellationToken ct) =>
        repository.ApprovePairingRequestAsync(workerId, ct);

    public Task<WorkerResponse?> ApprovePairingRequestAsync(string pairingKey, CancellationToken ct) =>
        repository.ApprovePairingRequestAsync(pairingKey, ct);

    public async Task<HeartbeatResult> RecordHeartbeatAsync(
        Guid workerId,
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

        var worker = await repository.RecordHeartbeatAsync(workerId, providedToken, request, ct);
        if (worker is not null)
        {
            if (isLocal && Get<LocalWorkerRegistry>() is { } localWorker)
                await localWorker.RegisterAsync(worker.Id, ct);

            return new(HeartbeatOutcomeKind.Ok, null, worker);
        }

        if (await repository.IsRemovedAsync(workerId, ct))
            return new(HeartbeatOutcomeKind.Gone, null, null);

        return new(HeartbeatOutcomeKind.Unauthorized, null, null);
    }

    public Task<string> LeaveAsync(Guid workerId, string token, CancellationToken ct) =>
        repository.LeaveAsync(workerId, token, ct);

    public Task<IReadOnlyList<WorkerResponse>> ListAsync(TimeSpan offlineAfter, CancellationToken ct) =>
        repository.ListAsync(offlineAfter, ct);

    public Task<WorkerResponse?> GetAsync(Guid workerId, TimeSpan offlineAfter, CancellationToken ct) =>
        repository.GetAsync(workerId, offlineAfter, ct);

    public Task<bool> SetDrainingAsync(Guid workerId, bool draining, CancellationToken ct) =>
        repository.SetDrainingAsync(workerId, draining, ct);

    public Task<bool> ClearQuarantineAsync(Guid workerId, CancellationToken ct) =>
        repository.ClearQuarantineAsync(workerId, ct);

    public Task<string> DeleteAsync(Guid workerId, CancellationToken ct) =>
        repository.DeleteAsync(workerId, ct);

    public Task<IReadOnlyDictionary<Guid, int>> ActiveTaskCountsAsync(CancellationToken ct) =>
        repository.ActiveTaskCountsAsync(ct);

    public Task<WorkerActivityResponse> ActivityAsync(Guid workerId, CancellationToken ct) =>
        repository.ActivityAsync(workerId, ct);

    public Task<WorkerProcessingSettings?> GetProcessingSettingsAsync(Guid workerId, CancellationToken ct) =>
        Get<LibraryProcessingRepository>() is { } p ? p.WorkerAsync(workerId, ct) : Task.FromResult<WorkerProcessingSettings?>(null);

    public Task<bool> SaveProcessingSettingsAsync(Guid workerId, SaveWorkerProcessingSettings request, CancellationToken ct) =>
        Get<LibraryProcessingRepository>() is { } p ? p.SaveNodeAsync(workerId, request, ct) : Task.FromResult(false);

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
