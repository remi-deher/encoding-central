using System.Net.Http.Json;
using Encoding.Contracts.Nodes;
using Encoding.Core.Nodes;
using System.Runtime.InteropServices;
using Encoding.Contracts.Cluster;

namespace Encoding.NodeAgent.Services;

public sealed class WorkerEnrollmentService(
    NodeAgentOptions options,
    WorkerIdentityStore identityStore,
    WorkerClusterDiscovery discovery,
    IToolCapabilityProbe capabilityProbe,
    ILogger<WorkerEnrollmentService> logger,
    Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
    LauncherControlClient? launcher = null)
{
    /// <summary>Délai laissé au cluster d'accueil pour approuver un changement de cluster : la clé de jumelage vit 10 minutes, avec une marge.</summary>
    public static readonly TimeSpan SwitchTimeout = TimeSpan.FromMinutes(12);

    public async Task<NodeResponse> EnrollAsync(Guid clusterId, CancellationToken cancellationToken, bool leaveCurrent = false, string? displayName = null)
    {
        var cluster = discovery.List().FirstOrDefault(candidate => candidate.Id == clusterId)
            ?? throw new InvalidOperationException("Ce cluster n’est plus visible sur le réseau. Actualisez la découverte et réessayez.");
        var identity = await identityStore.GetAsync(cancellationToken);
        if (identity.IsEnrolled && leaveCurrent && identity.ClusterId != cluster.Id.ToString("D"))
        {
            // Changement en deux phases : l'ancien rattachement reste intact tant que le nouveau cluster n'a pas répondu.
            await identityStore.BeginSwitchAsync(SwitchTimeout, cancellationToken);
            identity = await identityStore.GetAsync(cancellationToken);
        }
        if (identity.IsEnrolled)
            throw new InvalidOperationException("Ce worker est déjà rattaché à un cluster. Quittez-le d’abord, ou demandez à changer de cluster.");

        var effectiveDisplayName = string.IsNullOrWhiteSpace(displayName) ? options.DisplayName : displayName.Trim();
        var pending = await identityStore.SaveEnrollmentAsync(cluster.Id.ToString("D"), cluster.Name, cluster.ApiBaseUrl, cancellationToken, effectiveDisplayName);
        var capabilities = await capabilityProbe.DiscoverAsync(cancellationToken);
        var peerIdentity = await GetLocalPeerIdentityAsync(cancellationToken);
        var request = new NodeEnrollmentRequest(
            pending.Id,
            options.Name,
            options.Roles,
            options.MaxConcurrentTasks,
            capabilities,
            pending.NodeToken!,
            CreateInventory(),
            peerIdentity?.PeerId,
            peerIdentity?.PublicKey,
            DisplayName: effectiveDisplayName,
            ClusterPeerReconciliationUrl: peerIdentity?.AdvertisedUrl);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(cluster.ApiBaseUrl), "api/v1/nodes/enroll"))
        {
            Content = JsonContent.Create(request, options: AgentJson.Options)
        };
        // Le secret du conteneur n'est présenté qu'au serveur local : jamais à un cluster distant.
        if (IsLocalCluster(cluster) && LocalEnrollment.Read(options.LocalEnrollmentSecretFile) is { } secret)
            message.Headers.Add(LocalEnrollment.HeaderName, secret);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            await identityStore.AbandonPendingAsync($"le cluster {cluster.Name} est injoignable", cancellationToken);
            throw new HttpRequestException($"Le cluster {cluster.Name} est injoignable.", exception);
        }
        using var httpResponse = response;
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("L’enrôlement auprès du cluster {ClusterId} a échoué avec HTTP {StatusCode}.", clusterId, (int)response.StatusCode);
            await identityStore.AbandonPendingAsync($"le cluster {cluster.Name} a refusé l'enrôlement (HTTP {(int)response.StatusCode})", cancellationToken);
            // 409 : l'identifiant appartient à un nœud retiré ou à un autre nœud ; réessayer avec lui échouerait indéfiniment (0017).
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict && !(await identityStore.GetAsync(cancellationToken)).IsSwitching)
                await identityStore.ResetAsync(cancellationToken);
            throw new HttpRequestException(response.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "L’enrôlement direct est fermé sur ce cluster. Demandez à un administrateur de l’ouvrir depuis la page Nœuds, ou générez une clé de jumelage."
                : $"Le serveur du cluster a refusé l’enrôlement (HTTP {(int)response.StatusCode}).");
        }

        var node = await response.Content.ReadFromJsonAsync<NodeResponse>(jsonOptions.Value.SerializerOptions, cancellationToken)
            ?? throw new HttpRequestException("Le serveur n’a pas retourné la confirmation de l’enrôlement.");
        if (!IsLocalCluster(cluster) && options.LocalEnrollmentSecretFile is not null)
            await AdoptLocalClusterIdentityAsync(node.Id, cluster.Id, node.ClusterBootstrap, cluster.ApiBaseUrl, cancellationToken);
        await identityStore.MarkEnrolledAsync(cancellationToken);
        logger.LogInformation("Le worker {NodeId} a rejoint le cluster {ClusterId}.", node.Id, cluster.Id);
        await CompleteSwitchAsync(cancellationToken);
        return node with { ClusterBootstrap = null };
    }

    /// <summary>
    /// Quitte le cluster actuel et revient à l'état autonome (0039) : le nœud retrouve son serveur local (donc son cluster à un nœud)
    /// quand le lanceur le gère. Un départ refusé parce que le nœud traite encore un média échoue sans rien changer.
    /// </summary>
    public async Task LeaveAsync(CancellationToken cancellationToken)
    {
        // Pendant un changement de cluster, « quitter » annule le changement : l'ancien rattachement est rétabli tel quel (0040).
        if ((await identityStore.GetAsync(cancellationToken)).IsSwitching)
        {
            await identityStore.RollbackSwitchAsync("changement annulé à la demande de l'utilisateur", cancellationToken);
            return;
        }
        await ForgetClusterAsync(cancellationToken);
        if (launcher is null) return;
        var current = await launcher.GetAsync(cancellationToken);
        if (current is { Server: false })
            await launcher.SetAsync(new NodeMode(true, current.Worker || options.Roles.Contains(NodeRole.Worker)), cancellationToken);
    }

    /// <summary>Le cluster a retiré cette identité : repart d'une identité neuve, sans prévenir un cluster qui ne la connaît plus. Ignoré si l'identité a déjà changé.</summary>
    public async Task ForgetRemovedIdentityAsync(Guid removedId, CancellationToken cancellationToken)
    {
        var identity = await identityStore.GetAsync(cancellationToken);
        if (identity.Id != removedId) return;
        if (identity.Previous is { } previous)
        {
            // Changement en cours : seule l'identité de rattachement précédente peut avoir été retirée ; l'attente du nouveau cluster est conservée.
            return;
        }
        await identityStore.ResetAsync(cancellationToken);
    }

    /// <summary>Prévient le cluster actuel puis efface l'identité locale. Un cluster injoignable n'empêche pas de partir : l'administrateur retirera la ligne hors ligne.</summary>
    private async Task ForgetClusterAsync(CancellationToken cancellationToken)
    {
        var identity = await identityStore.GetAsync(cancellationToken);
        if (identity.HasCredentials && await NotifyLeaveAsync(identity, cancellationToken) == System.Net.HttpStatusCode.Conflict)
            throw new InvalidOperationException("Ce nœud traite encore un média ou une découverte. Attendez la fin du traitement avant de quitter le cluster.");
        await identityStore.ResetAsync(cancellationToken);
    }

    /// <summary>Retourne le statut HTTP du cluster, ou null s'il est injoignable.</summary>
    private async Task<System.Net.HttpStatusCode?> NotifyLeaveAsync(WorkerIdentityState identity, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(identity.ApiBaseUrl!), $"api/v1/nodes/{identity.Id:D}/leave"));
            message.Headers.Add("X-Node-Token", identity.NodeToken);
            using var response = await client.SendAsync(message, cancellationToken);
            return response.StatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Le cluster {ClusterName} est injoignable ; le nœud le quitte localement.", identity.ClusterName);
            return null;
        }
    }

    /// <summary>
    /// Seconde phase d'un changement de cluster (0040), une fois le nouveau cluster confirmé : l'ancien est prévenu (au mieux) et son identité oubliée.
    /// Le serveur reste actif sur chaque nœud ; sa base SQLite locale réconcilie les données du cluster avec les pairs (0049).
    /// </summary>
    public async Task CompleteSwitchAsync(CancellationToken cancellationToken)
    {
        var previous = await identityStore.CommitSwitchAsync(cancellationToken);
        if (previous is { HasCredentials: true } && await NotifyLeaveAsync(previous, cancellationToken) == System.Net.HttpStatusCode.Conflict)
            logger.LogWarning("L'ancien cluster {ClusterName} n'a pas retiré ce nœud (traitement en cours) ; ses tâches seront reprises à l'expiration des baux.", previous.ClusterName);
        await KeepLocalServerActiveAsync(cancellationToken);
    }

    /// <summary>
    /// Abandonne une demande dont l'approbation n'est pas arrivée à temps : pendant un changement de cluster, l'ancien rattachement est rétabli ;
    /// pour un nœud autonome, la clé de jumelage expirée est effacée et le nœud redevient autonome.
    /// </summary>
    public async Task<bool> RollbackExpiredSwitchAsync(CancellationToken cancellationToken)
    {
        var identity = await identityStore.GetAsync(cancellationToken);
        if (identity.IsEnrolled) return false;
        var expired = identity.IsSwitching
            ? identity.SwitchDeadline <= DateTimeOffset.UtcNow
            : identity.HasCredentials && identity.PairingPublished && identity.PairingExpiresAt <= DateTimeOffset.UtcNow;
        if (!expired) return false;
        var target = identity.ClusterName ?? "le cluster d'accueil";
        var abandoned = await identityStore.AbandonPendingAsync($"{target} n'a pas approuvé l'arrivée à temps", cancellationToken);
        if (abandoned) logger.LogWarning("Rattachement annulé : {Target} n'a pas approuvé l'arrivée à temps ; retour à {Previous}.", target, identity.Previous?.ClusterName ?? "l'état autonome");
        return abandoned;
    }

    /// <summary>Un nœud rattaché conserve son serveur local actif ; le maillage réconcilie sa copie SQLite du cluster (0049).</summary>
    public async Task KeepLocalServerActiveAsync(CancellationToken cancellationToken)
    {
        if (launcher is null) return;
        var current = await launcher.GetAsync(cancellationToken);
        if (current is null)
        {
            logger.LogWarning("Le lanceur local ne répond pas ; le mode serveur n'a pas pu être vérifié après le rattachement.");
            return;
        }
        if (current.Server) return;
        await launcher.SetAsync(new NodeMode(true, current.Worker), cancellationToken);
        logger.LogInformation("Le serveur local reste actif après le rattachement ; vérifiez que sa base est celle du cluster rejoint.");
    }

    /// <summary>Vrai pour le serveur du même conteneur : l'agent est configuré avec un secret local et le cluster est joignable en boucle locale.</summary>
    public bool IsLocalCluster(DiscoveredCluster cluster) => IsLocalUrl(cluster.ApiBaseUrl);

    public bool IsLocalUrl(string apiBaseUrl) =>
        options.LocalEnrollmentSecretFile is not null
        && options.ClusterApiBaseUrl is not null
        && string.Equals(apiBaseUrl, options.ClusterApiBaseUrl, StringComparison.OrdinalIgnoreCase)
        && LocalEnrollment.IsLoopback(apiBaseUrl);

    /// <summary>Après approbation distante, aligne l'identité annoncée par le serveur local pour que les pairs mDNS partagent le même clusterId.</summary>
    public async Task<ClusterPeerPublicIdentity?> GetLocalPeerIdentityAsync(CancellationToken cancellationToken)
    {
        if (options.LocalEnrollmentSecretFile is null || options.ClusterApiBaseUrl is not { } localApiUrl
            || !LocalEnrollment.IsLoopback(localApiUrl)) return null;
        var secret = LocalEnrollment.Read(options.LocalEnrollmentSecretFile);
        if (string.IsNullOrEmpty(secret)) throw new HttpRequestException("Le secret local est indisponible pour lire l’identité du serveur.");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(localApiUrl), "api/v1/cluster/reconciliation/local-identity"));
        message.Headers.Add(LocalEnrollment.HeaderName, secret);
        using var response = await client.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"L’identité du serveur local n’a pas pu être lue (HTTP {(int)response.StatusCode}).");
        return await response.Content.ReadFromJsonAsync<ClusterPeerPublicIdentity>(AgentJson.Options, cancellationToken)
            ?? throw new HttpRequestException("Le serveur local n’a pas retourné son identité de pair.");
    }

    /// <summary>Après l'approbation, récupère la preuve d'admission depuis le cluster choisi puis fait adopter sa clé au serveur local.</summary>
    public async Task AdoptApprovedClusterAsync(WorkerIdentityState identity, Guid nodeId, CancellationToken cancellationToken)
    {
        if (options.LocalEnrollmentSecretFile is null || options.ClusterApiBaseUrl is not { } localApiUrl
            || !LocalEnrollment.IsLoopback(localApiUrl) || string.IsNullOrWhiteSpace(identity.ApiBaseUrl)
            || IsLocalUrl(identity.ApiBaseUrl)) return;
        if (!Guid.TryParse(identity.ClusterId, out var clusterId) || string.IsNullOrWhiteSpace(identity.NodeToken))
            throw new HttpRequestException("L’identité locale du rattachement en attente est incomplète.");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(new Uri(identity.ApiBaseUrl), $"api/v1/nodes/{nodeId:D}/cluster-bootstrap"));
        request.Headers.Add("X-Node-Token", identity.NodeToken);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(response.StatusCode == System.Net.HttpStatusCode.NotFound
                ? "Le nœud est approuvé, mais l’admission signée du serveur n’a pas encore été répliquée."
                : $"La preuve d’admission du serveur n’a pas pu être récupérée (HTTP {(int)response.StatusCode}).");
        var bootstrap = await response.Content.ReadFromJsonAsync<ClusterJoinBootstrap>(AgentJson.Options, cancellationToken)
            ?? throw new HttpRequestException("Le cluster n’a pas retourné sa preuve d’admission.");
        if (bootstrap.ClusterId != clusterId)
            throw new HttpRequestException("La preuve reçue appartient à un autre cluster que celui choisi.");
        await AdoptLocalClusterIdentityAsync(nodeId, clusterId, bootstrap, identity.ApiBaseUrl, cancellationToken);
    }

    private async Task AdoptLocalClusterIdentityAsync(Guid nodeId, Guid expectedClusterId, ClusterJoinBootstrap? bootstrap, string? peerUrl, CancellationToken cancellationToken)
    {
        if (options.LocalEnrollmentSecretFile is null || options.ClusterApiBaseUrl is not { } localApiUrl || !LocalEnrollment.IsLoopback(localApiUrl)) return;
        var secret = LocalEnrollment.Read(options.LocalEnrollmentSecretFile);
        if (string.IsNullOrEmpty(secret)) throw new HttpRequestException("Le secret local est indisponible pour synchroniser l'identité du serveur.");
        if (bootstrap is null) throw new HttpRequestException("Le cluster n’a pas retourné la preuve d’admission et la clé partagée du serveur local.");
        if (bootstrap.ClusterId != expectedClusterId) throw new HttpRequestException("La preuve reçue appartient à un autre cluster que celui choisi.");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(localApiUrl), "api/v1/cluster/join"))
        {
            Content = JsonContent.Create(new { clusterId = bootstrap.ClusterId, clusterName = bootstrap.ClusterName, nodeId, bootstrap, peerUrl })
        };
        message.Headers.Add(LocalEnrollment.HeaderName, secret);
        using var response = await client.SendAsync(message, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            throw new HttpRequestException("Rattachement suspendu : validez le renommage des comptes entrants dans Paramètres > Utilisateurs, puis réessayez le rattachement. Si aucune collision n’est affichée, mettez à jour le cluster d’accueil pour obtenir sa liste signée des comptes.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Le nœud a rejoint {bootstrap.ClusterName}, mais le serveur local n'a pas adopté son identité de cluster (HTTP {(int)response.StatusCode}).");
    }

    public async Task<WorkerPairingResponse> CreatePairingKeyAsync(Guid clusterId, CancellationToken cancellationToken, bool leaveCurrent = false, string? displayName = null)
    {
        var cluster = discovery.List().FirstOrDefault(candidate => candidate.Id == clusterId)
            ?? throw new InvalidOperationException("Ce cluster n’est plus visible sur le réseau. Actualisez la découverte et réessayez.");
        var identity = await identityStore.GetAsync(cancellationToken);
        if (identity.IsEnrolled && leaveCurrent && identity.ClusterId != cluster.Id.ToString("D"))
        {
            await identityStore.BeginSwitchAsync(SwitchTimeout, cancellationToken);
            identity = await identityStore.GetAsync(cancellationToken);
        }
        if (identity.IsEnrolled)
            throw new InvalidOperationException("Ce worker est déjà rattaché à un cluster. Quittez-le d’abord, ou demandez à changer de cluster.");

        var effectiveDisplayName = !string.IsNullOrWhiteSpace(displayName)
            ? displayName.Trim()
            : (identity.DisplayName ?? options.DisplayName);
        var pending = await identityStore.SavePairingAsync(cluster.Id.ToString("D"), cluster.Name, cluster.ApiBaseUrl, cancellationToken, effectiveDisplayName);
        var capabilities = await capabilityProbe.DiscoverAsync(cancellationToken);
        var peerIdentity = await GetLocalPeerIdentityAsync(cancellationToken);
        var request = new CreateNodeEnrollmentRequest(
            new NodeEnrollmentRequest(
                pending.Id,
                options.Name,
                options.Roles,
                options.MaxConcurrentTasks,
                capabilities,
                pending.NodeToken!,
                CreateInventory(),
                peerIdentity?.PeerId,
                peerIdentity?.PublicKey,
                DisplayName: effectiveDisplayName,
                ClusterPeerReconciliationUrl: peerIdentity?.AdvertisedUrl),
            pending.PairingKey!);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(
                new Uri(new Uri(cluster.ApiBaseUrl), "api/v1/nodes/enrollment-requests"),
                request,
                AgentJson.Options,
                cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            await identityStore.AbandonPendingAsync($"le cluster {cluster.Name} est injoignable", cancellationToken);
            throw new HttpRequestException($"Le cluster {cluster.Name} est injoignable.", exception);
        }
        using var httpResponse = response;
        if (!response.IsSuccessStatusCode)
        {
            await identityStore.AbandonPendingAsync($"le cluster {cluster.Name} a refusé la demande de jumelage (HTTP {(int)response.StatusCode})", cancellationToken);
            logger.LogWarning("La demande de jumelage auprès du cluster {ClusterId} a échoué avec HTTP {StatusCode}.", clusterId, (int)response.StatusCode);
            throw new HttpRequestException(response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                ? "Trop de demandes de jumelage sont en attente sur ce cluster. Réessayez après leur approbation ou leur expiration."
                : $"Le serveur du cluster a refusé la demande de jumelage (HTTP {(int)response.StatusCode}).");
        }

        await identityStore.MarkPairingPublishedAsync(cancellationToken);
        logger.LogInformation("Une clé de jumelage a été créée pour le worker {NodeId} vers le cluster {ClusterId}.", pending.Id, cluster.Id);
        var verificationCode = NodeVerificationCode.FromToken(pending.Id, pending.NodeToken!, peerIdentity?.PublicKey);
        return new WorkerPairingResponse(pending.PairingKey!, pending.PairingExpiresAt!.Value, verificationCode);
    }

    private static NodeInventory CreateInventory() => HardwareInventory.Current();
}

public sealed record WorkerPairingResponse(string PairingKey, DateTimeOffset ExpiresAt, string? VerificationCode = null);
