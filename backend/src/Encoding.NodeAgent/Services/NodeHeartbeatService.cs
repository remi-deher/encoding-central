using System.Net.Http.Json;
using Encoding.Contracts.Nodes;
using Encoding.Core.Nodes;
using System.Runtime.InteropServices;

namespace Encoding.NodeAgent.Services;

public sealed class NodeHeartbeatService(
    NodeAgentOptions options,
    WorkerIdentityStore identityStore,
    IToolCapabilityProbe capabilityProbe,
    WorkerEnrollmentService enrollment,
    ILogger<NodeHeartbeatService> logger,
    LeadershipReceiverService? leadershipReceiver = null,
    LauncherControlClient? launcher = null) : BackgroundService
{
    private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    // Propre à ce processus : deux processus qui partagent la même identité sont reconnus par leurs nonces qui se relaient (0041).
    private readonly string sessionNonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
    private NodeLifecycle? lastLifecycle;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var identity = await identityStore.GetAsync(stoppingToken);
        logger.LogInformation("Agent worker {NodeName} démarré avec l’identifiant local {NodeId}.", options.Name, identity.Id);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await enrollment.RollbackExpiredSwitchAsync(stoppingToken);
                identity = await identityStore.GetAsync(stoppingToken);
                // Pendant un changement de cluster, l'ancien rattachement reste vivant : le nœud y garde ses baux et sa présence.
                if (identity.Previous is { HasCredentials: true } previous) await BeatPreviousAsync(previous, stoppingToken);
                if (identity.HasCredentials)
                {
                    var capabilities = await capabilityProbe.DiscoverAsync(stoppingToken);
                    var inventory = HardwareInventory.Current();
                    var heartbeat = new NodeHeartbeatRequest(options.Name, options.Roles, options.MaxConcurrentTasks, capabilities, inventory, ClusterProtocol.Current, sessionNonce);
                    var candidates = identity.IsEnrolled
                        ? identity.GetCandidateServerUrls(options)
                        : [identity.ApiBaseUrl!];
                    foreach (var serverUrl in candidates)
                    {
                        try
                        {
                            using var request = new HttpRequestMessage(HttpMethod.Put,
                                new Uri(new Uri(serverUrl), $"api/v1/nodes/{identity.Id:D}/heartbeat"))
                            {
                                Content = JsonContent.Create(heartbeat, options: AgentJson.Options)
                            };
                            request.Headers.Add("X-Node-Token", identity.NodeToken);
                            if (enrollment.IsLocalUrl(serverUrl) && LocalEnrollment.Read(options.LocalEnrollmentSecretFile) is { } secret)
                                request.Headers.Add(LocalEnrollment.HeaderName, secret);
                            using var response = await httpClient.SendAsync(request, stoppingToken);
                            if (response.StatusCode == System.Net.HttpStatusCode.Gone && string.Equals(serverUrl.TrimEnd('/'), identity.ApiBaseUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                            {
                                // Le cluster a retiré cette identité (0017) et ne la réactivera jamais : sans nouvelle identité, le nœud resterait
                                // « enrôlé » sans jamais être revu. Le nœud local se réenrôle seul ; un nœud distant redevient autonome.
                                logger.LogWarning("Le cluster a retiré le nœud {NodeId} ; l'identité locale est abandonnée.", identity.Id);
                                await enrollment.ForgetRemovedIdentityAsync(identity.Id, stoppingToken);
                                break;
                            }
                            else if (!response.IsSuccessStatusCode && !(response.StatusCode == System.Net.HttpStatusCode.Unauthorized && !identity.IsEnrolled))
                                logger.LogWarning("Le heartbeat du nœud {NodeId} vers {ServerUrl} a reçu HTTP {StatusCode}.", identity.Id, serverUrl, (int)response.StatusCode);
                            else if (response.IsSuccessStatusCode)
                            {
                                var node = await response.Content.ReadFromJsonAsync<NodeResponse>(AgentJson.Options, stoppingToken);
                                if (!identity.IsEnrolled)
                                {
                                    await enrollment.AdoptApprovedClusterAsync(identity, identity.Id, stoppingToken);
                                    await identityStore.MarkEnrolledAsync(stoppingToken);
                                    logger.LogInformation("L’enrôlement en attente du worker {NodeId} a été confirmé par le cluster.", identity.Id);
                                    await enrollment.CompleteSwitchAsync(stoppingToken);
                                }
                                if (node?.ClusterServerUrls is { Count: > 0 })
                                    await identityStore.UpdateKnownServersAsync(node.ClusterServerUrls, stoppingToken);
                                if (!string.IsNullOrWhiteSpace(node?.MasterUrl))
                                    await identityStore.UpdateMasterUrlAsync(node.MasterUrl, stoppingToken);

                                if (node?.LeadershipHandoff is not null && leadershipReceiver is not null && !string.IsNullOrWhiteSpace(identity.NodeToken))
                                    _ = Task.Run(async () => await leadershipReceiver.HandleHandoffAsync(node.LeadershipHandoff, serverUrl, identity.NodeToken, stoppingToken));
                                else if (node?.LatestBackup is not null && leadershipReceiver is not null && !string.IsNullOrWhiteSpace(identity.NodeToken))
                                    _ = Task.Run(async () => await leadershipReceiver.HandleBackupNotificationAsync(node.LatestBackup, serverUrl, identity.NodeToken, stoppingToken));

                                logger.LogDebug("Heartbeat envoyé pour le nœud {NodeId} vers {ServerUrl}.", identity.Id, serverUrl);
                                ReportLifecycle(node);
                                if (node?.LeadershipHandoff is null)
                                    await ApplyDesiredModeAsync(node?.DesiredRoles, stoppingToken);
                            }
                        }
                        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !stoppingToken.IsCancellationRequested)
                        {
                            logger.LogDebug(exception, "Le serveur {ServerUrl} est momentanément indisponible pour le heartbeat.", serverUrl);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Le cluster est momentanément indisponible ; nouvel essai au prochain heartbeat.");
            }
            catch (TaskCanceledException exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Le heartbeat a dépassé son délai ; nouvel essai au prochain cycle.");
            }

            await Task.Delay(options.HeartbeatInterval, stoppingToken);
        }
    }

    // Le cluster décide de l'éligibilité ; le nœud se contente de le dire clairement dans ses journaux, une fois par changement.
    private void ReportLifecycle(NodeResponse? node)
    {
        if (node?.Lifecycle is not { } lifecycle || lifecycle == lastLifecycle) return;
        lastLifecycle = lifecycle;
        if (lifecycle is NodeLifecycle.Online or NodeLifecycle.Offline)
            logger.LogInformation("Le cluster considère ce nœud comme {Lifecycle}.", lifecycle);
        else
            logger.LogWarning("Le cluster ne confie plus de tâche à ce nœud ({Lifecycle}) : {Reason}", lifecycle, node.LifecycleReason);
    }

    private async Task BeatPreviousAsync(WorkerIdentityState previous, CancellationToken cancellationToken)
    {
        try
        {
            var capabilities = await capabilityProbe.DiscoverAsync(cancellationToken);
            var inventory = HardwareInventory.Current();
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(new Uri(previous.ApiBaseUrl!), $"api/v1/nodes/{previous.Id:D}/heartbeat"))
            {
                Content = JsonContent.Create(new NodeHeartbeatRequest(options.Name, options.Roles, options.MaxConcurrentTasks, capabilities, inventory), options: AgentJson.Options)
            };
            request.Headers.Add("X-Node-Token", previous.NodeToken);
            using var response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "L'ancien cluster ne répond pas pendant le changement de cluster.");
        }
    }

    // Mode demandé par un administrateur du cluster (0036) : appliqué par le lanceur local, qui relance l'agent avec ses nouveaux rôles.
    private async Task ApplyDesiredModeAsync(IReadOnlyList<NodeRole>? desiredRoles, CancellationToken cancellationToken)
    {
        if (desiredRoles is not { Count: > 0 }) return;
        var desired = new NodeMode(desiredRoles.Contains(NodeRole.Server), desiredRoles.Contains(NodeRole.Worker));
        if (desired == new NodeMode(options.Roles.Contains(NodeRole.Server), options.Roles.Contains(NodeRole.Worker))) return;
        if (launcher is null)
        {
            logger.LogWarning("Le cluster demande le mode {Mode}, mais ce nœud n'est pas géré par un lanceur (image unique) ; demande ignorée.", desired.ToRoles());
            return;
        }
        try
        {
            await launcher.SetAsync(desired, cancellationToken);
            logger.LogInformation("Mode du nœud changé à la demande du cluster : {Mode}.", desired.ToRoles());
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Le lanceur a refusé le changement de mode demandé par le cluster.");
        }
    }

    public override void Dispose()
    {
        httpClient.Dispose();
        base.Dispose();
    }
}
