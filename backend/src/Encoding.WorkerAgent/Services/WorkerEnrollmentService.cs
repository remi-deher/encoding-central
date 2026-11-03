using System.Net.Http.Json;
using Encoding.Contracts.Workers;
using Encoding.Core.Workers;
using System.Runtime.InteropServices;

namespace Encoding.WorkerAgent.Services;

public sealed class WorkerEnrollmentService(
    WorkerAgentOptions options,
    WorkerIdentityStore identityStore,
    WorkerCentralDiscovery discovery,
    IToolCapabilityProbe capabilityProbe,
    ILogger<WorkerEnrollmentService> logger,
    Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions)
{
    public async Task<WorkerResponse> EnrollAsync(Guid centralId, CancellationToken cancellationToken, string? displayName = null, string? centralSession = null)
    {
        var central = discovery.List().FirstOrDefault(candidate => candidate.Id == centralId)
            ?? throw new InvalidOperationException("Ce central n’est plus visible sur le réseau. Actualisez la découverte et réessayez.");
        var identity = await identityStore.GetAsync(cancellationToken);
        if (identity.IsEnrolled)
            throw new InvalidOperationException("Ce worker est déjà rattaché à un central. Quittez-le d’abord.");

        var effectiveDisplayName = string.IsNullOrWhiteSpace(displayName) ? options.DisplayName : displayName.Trim();
        var pending = await identityStore.SaveEnrollmentAsync(central.Id.ToString("D"), central.Name, central.ApiBaseUrl, cancellationToken, effectiveDisplayName);
        var capabilities = await capabilityProbe.DiscoverAsync(cancellationToken);
        var request = new WorkerEnrollmentRequest(
            pending.Id,
            options.Name,
            options.Roles,
            options.MaxConcurrentTasks,
            capabilities,
            pending.WorkerToken!,
            CreateInventory(),
            DisplayName: effectiveDisplayName,
            Version: AppVersion.Current);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(central.ApiBaseUrl), "api/v1/workers/enroll"))
        {
            Content = JsonContent.Create(request, options: AgentJson.Options)
        };
        // Le secret du conteneur n'est présenté qu'au serveur local : jamais à un central distant.
        if (IsLocalCentral(central) && LocalEnrollment.Read(options.LocalEnrollmentSecretFile) is { } secret)
            message.Headers.Add(LocalEnrollment.HeaderName, secret);
        else if (IsSessionValue(centralSession))
        {
            // ADR 0034 : le compte administrateur du central admet le worker ; la session n'est présentée qu'à ce central.
            message.Headers.Add("Cookie", $"{Encoding.Contracts.Auth.SessionCookie.Name}={centralSession}");
            message.Headers.Add("X-Encoding-Csrf", "1");
        }
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            await identityStore.AbandonPendingAsync($"le central {central.Name} est injoignable", cancellationToken);
            throw new HttpRequestException($"Le central {central.Name} est injoignable.", exception);
        }
        using var httpResponse = response;
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("L’enrôlement auprès du central {ClusterId} a échoué avec HTTP {StatusCode}.", centralId, (int)response.StatusCode);
            await identityStore.AbandonPendingAsync($"le central {central.Name} a refusé l'enrôlement (HTTP {(int)response.StatusCode})", cancellationToken);
            // 409 : l'identifiant appartient à un worker retiré ou à un autre worker ; réessayer avec lui échouerait indéfiniment (0017).
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                await identityStore.ResetAsync(cancellationToken);
            throw new HttpRequestException(response.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "L’enrôlement direct est fermé sur ce central. Demandez à un administrateur de l’ouvrir depuis la page Workers, ou générez une clé de jumelage."
                : $"Le serveur du central a refusé l’enrôlement (HTTP {(int)response.StatusCode}).");
        }

        var worker = await response.Content.ReadFromJsonAsync<WorkerResponse>(jsonOptions.Value.SerializerOptions, cancellationToken)
            ?? throw new HttpRequestException("Le serveur n’a pas retourné la confirmation de l’enrôlement.");
        await identityStore.MarkEnrolledAsync(cancellationToken);
        logger.LogInformation("Le worker {NodeId} a rejoint le central {ClusterId}.", worker.Id, central.Id);
        return worker;
    }

    /// <summary>
    /// Quitte le central actuel et revient à l'état autonome (0039) : le worker retrouve son serveur local (donc son central à un worker)
    /// quand le lanceur le gère. Un départ refusé parce que le worker traite encore un média échoue sans rien changer.
    /// </summary>
    public async Task LeaveAsync(CancellationToken cancellationToken)
    {
        await ForgetCentralAsync(cancellationToken);
    }

    /// <summary>Le central a retiré cette identité : repart d'une identité neuve, sans prévenir un central qui ne la connaît plus. Ignoré si l'identité a déjà changé.</summary>
    public async Task ForgetRemovedIdentityAsync(Guid removedId, CancellationToken cancellationToken)
    {
        var identity = await identityStore.GetAsync(cancellationToken);
        if (identity.Id != removedId) return;
        await identityStore.ResetAsync(cancellationToken);
    }

    /// <summary>Prévient le central actuel puis efface l'identité locale. Un central injoignable n'empêche pas de partir : l'administrateur retirera la ligne hors ligne.</summary>
    private async Task ForgetCentralAsync(CancellationToken cancellationToken)
    {
        var identity = await identityStore.GetAsync(cancellationToken);
        if (identity.HasCredentials && await NotifyLeaveAsync(identity, cancellationToken) == System.Net.HttpStatusCode.Conflict)
            throw new InvalidOperationException("Ce worker traite encore un média ou une découverte. Attendez la fin du traitement avant de quitter le central.");
        await identityStore.ResetAsync(cancellationToken);
    }

    /// <summary>Retourne le statut HTTP du central, ou null s'il est injoignable.</summary>
    private async Task<System.Net.HttpStatusCode?> NotifyLeaveAsync(WorkerIdentityState identity, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(identity.ApiBaseUrl!), $"api/v1/workers/{identity.Id:D}/leave"));
            message.Headers.Add("X-Node-Token", identity.WorkerToken);
            using var response = await client.SendAsync(message, cancellationToken);
            return response.StatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Le central {ClusterName} est injoignable ; le worker le quitte localement.", identity.CentralName);
            return null;
        }
    }

    /// <summary>Abandonne une demande de jumelage dont l'approbation n'est pas arrivée à temps : la clé expirée est effacée.</summary>
    public async Task<bool> AbandonExpiredPairingAsync(CancellationToken cancellationToken)
    {
        var identity = await identityStore.GetAsync(cancellationToken);
        if (identity.IsEnrolled) return false;
        if (!(identity.HasCredentials && identity.PairingPublished && identity.PairingExpiresAt <= DateTimeOffset.UtcNow)) return false;
        var target = identity.CentralName ?? "le central";
        var abandoned = await identityStore.AbandonPendingAsync($"{target} n'a pas approuvé l'arrivée à temps", cancellationToken);
        if (abandoned) logger.LogWarning("Rattachement annulé : {Target} n'a pas approuvé l'arrivée à temps.", target);
        return abandoned;
    }

    /// <summary>Vrai pour le serveur du même conteneur : l'agent est configuré avec un secret local et le central est joignable en boucle locale.</summary>
    public bool IsLocalCentral(DiscoveredCentral central) => IsLocalUrl(central.ApiBaseUrl);

    public bool IsLocalUrl(string apiBaseUrl) =>
        options.LocalEnrollmentSecretFile is not null
        && options.CentralApiBaseUrl is not null
        && string.Equals(apiBaseUrl, options.CentralApiBaseUrl, StringComparison.OrdinalIgnoreCase)
        && LocalEnrollment.IsLoopback(apiBaseUrl);

    public async Task<WorkerPairingResponse> CreatePairingKeyAsync(Guid centralId, CancellationToken cancellationToken, string? displayName = null)
    {
        var central = discovery.List().FirstOrDefault(candidate => candidate.Id == centralId)
            ?? throw new InvalidOperationException("Ce central n’est plus visible sur le réseau. Actualisez la découverte et réessayez.");
        var identity = await identityStore.GetAsync(cancellationToken);
        if (identity.IsEnrolled)
            throw new InvalidOperationException("Ce worker est déjà rattaché à un central. Quittez-le d’abord.");

        var effectiveDisplayName = !string.IsNullOrWhiteSpace(displayName)
            ? displayName.Trim()
            : (identity.DisplayName ?? options.DisplayName);
        var pending = await identityStore.SavePairingAsync(central.Id.ToString("D"), central.Name, central.ApiBaseUrl, cancellationToken, effectiveDisplayName);
        var capabilities = await capabilityProbe.DiscoverAsync(cancellationToken);
        var request = new CreateWorkerEnrollmentRequest(
            new WorkerEnrollmentRequest(
                pending.Id,
                options.Name,
                options.Roles,
                options.MaxConcurrentTasks,
                capabilities,
                pending.WorkerToken!,
                CreateInventory(),
                DisplayName: effectiveDisplayName,
            Version: AppVersion.Current),
            pending.PairingKey!);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(
                new Uri(new Uri(central.ApiBaseUrl), "api/v1/workers/enrollment-requests"),
                request,
                AgentJson.Options,
                cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            await identityStore.AbandonPendingAsync($"le central {central.Name} est injoignable", cancellationToken);
            throw new HttpRequestException($"Le central {central.Name} est injoignable.", exception);
        }
        using var httpResponse = response;
        if (!response.IsSuccessStatusCode)
        {
            await identityStore.AbandonPendingAsync($"le central {central.Name} a refusé la demande de jumelage (HTTP {(int)response.StatusCode})", cancellationToken);
            logger.LogWarning("La demande de jumelage auprès du central {ClusterId} a échoué avec HTTP {StatusCode}.", centralId, (int)response.StatusCode);
            throw new HttpRequestException(response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                ? "Trop de demandes de jumelage sont en attente sur ce central. Réessayez après leur approbation ou leur expiration."
                : $"Le serveur du central a refusé la demande de jumelage (HTTP {(int)response.StatusCode}).");
        }

        await identityStore.MarkPairingPublishedAsync(cancellationToken);
        logger.LogInformation("Une clé de jumelage a été créée pour le worker {NodeId} vers le central {ClusterId}.", pending.Id, central.Id);
        var verificationCode = WorkerVerificationCode.FromToken(pending.Id, pending.WorkerToken!);
        return new WorkerPairingResponse(pending.PairingKey!, pending.PairingExpiresAt!.Value, verificationCode);
    }

    /// <summary>Valeur de cookie sûre à recopier dans un en-tête : jamais de séparateur ni de retour à la ligne.</summary>
    internal static bool IsSessionValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~' or '+' or '/' or '=' or '%');

    private static WorkerInventory CreateInventory() => HardwareInventory.Current();
}

public sealed record WorkerPairingResponse(string PairingKey, DateTimeOffset ExpiresAt, string? VerificationCode = null);
