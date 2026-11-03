using System.Net.Http.Json;
using Encoding.Contracts.Workers;
using Encoding.Core.Workers;
using System.Runtime.InteropServices;

namespace Encoding.WorkerAgent.Services;

public sealed class WorkerHeartbeatService(
    WorkerAgentOptions options,
    WorkerIdentityStore identityStore,
    IToolCapabilityProbe capabilityProbe,
    WorkerEnrollmentService enrollment,
    ILogger<WorkerHeartbeatService> logger) : BackgroundService
{
    private readonly HttpClient httpClient = WorkerCentralHttp.Create(TimeSpan.FromSeconds(10));
    // Propre à ce processus : deux processus qui partagent la même identité sont reconnus par leurs nonces qui se relaient (0041).
    private readonly string sessionNonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
    private WorkerLifecycle? lastLifecycle;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var identity = await identityStore.GetAsync(stoppingToken);
        logger.LogInformation("Agent worker {NodeName} démarré avec l’identifiant local {NodeId}.", options.Name, identity.Id);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await enrollment.AbandonExpiredPairingAsync(stoppingToken);
                identity = await identityStore.GetAsync(stoppingToken);
                if (identity.HasCredentials)
                {
                    var capabilities = await capabilityProbe.DiscoverAsync(stoppingToken);
                    var inventory = HardwareInventory.Current();
                    var heartbeat = new WorkerHeartbeatRequest(options.Name, options.Roles, options.MaxConcurrentTasks, capabilities, inventory, AppVersion.Current, sessionNonce);
                    var candidates = identity.IsEnrolled
                        ? identity.GetCandidateServerUrls(options)
                        : [identity.ApiBaseUrl!];
                    foreach (var serverUrl in candidates)
                    {
                        try
                        {
                            using var request = new HttpRequestMessage(HttpMethod.Put,
                                new Uri(new Uri(serverUrl), $"api/v1/workers/{identity.Id:D}/heartbeat"))
                            {
                                Content = JsonContent.Create(heartbeat, options: AgentJson.Options)
                            };
                            request.Headers.Add("X-Node-Token", identity.WorkerToken);
                            if (enrollment.IsLocalUrl(serverUrl) && LocalEnrollment.Read(options.LocalEnrollmentSecretFile) is { } secret)
                                request.Headers.Add(LocalEnrollment.HeaderName, secret);
                            using var response = await httpClient.SendAsync(request, stoppingToken);
                            if (response.StatusCode == System.Net.HttpStatusCode.Gone && string.Equals(serverUrl.TrimEnd('/'), identity.ApiBaseUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                            {
                                // Le central a retiré cette identité (0017) et ne la réactivera jamais : sans nouvelle identité, le worker resterait
                                // « enrôlé » sans jamais être revu. Le worker local se réenrôle seul ; un worker distant redevient autonome.
                                logger.LogWarning("Le central a retiré le worker {NodeId} ; l'identité locale est abandonnée.", identity.Id);
                                await enrollment.ForgetRemovedIdentityAsync(identity.Id, stoppingToken);
                                break;
                            }
                            else if (!response.IsSuccessStatusCode && !(response.StatusCode == System.Net.HttpStatusCode.Unauthorized && !identity.IsEnrolled))
                                logger.LogWarning("Le heartbeat du worker {NodeId} vers {ServerUrl} a reçu HTTP {StatusCode}.", identity.Id, serverUrl, (int)response.StatusCode);
                            else if (response.IsSuccessStatusCode)
                            {
                                var worker = await response.Content.ReadFromJsonAsync<WorkerResponse>(AgentJson.Options, stoppingToken);
                                if (!identity.IsEnrolled)
                                {
                                    await identityStore.MarkEnrolledAsync(stoppingToken);
                                    logger.LogInformation("L’enrôlement en attente du worker {NodeId} a été confirmé par le central.", identity.Id);
                                }

                                logger.LogDebug("Heartbeat envoyé pour le worker {NodeId} vers {ServerUrl}.", identity.Id, serverUrl);
                                ReportLifecycle(worker);
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
                logger.LogWarning(exception, "Le central est momentanément indisponible ; nouvel essai au prochain heartbeat.");
            }
            catch (TaskCanceledException exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Le heartbeat a dépassé son délai ; nouvel essai au prochain cycle.");
            }

            await Task.Delay(WorkerPacing.Jitter(options.HeartbeatInterval, WorkerPacing.HeartbeatJitter), stoppingToken);
        }
    }

    // Le central décide de l'éligibilité ; le worker se contente de le dire clairement dans ses journaux, une fois par changement.
    private void ReportLifecycle(WorkerResponse? worker)
    {
        if (worker?.Lifecycle is not { } lifecycle || lifecycle == lastLifecycle) return;
        lastLifecycle = lifecycle;
        if (lifecycle is WorkerLifecycle.Online or WorkerLifecycle.Offline)
            logger.LogInformation("Le central considère ce worker comme {Lifecycle}.", lifecycle);
        else
            logger.LogWarning("Le central ne confie plus de tâche à ce worker ({Lifecycle}) : {Reason}", lifecycle, worker.LifecycleReason);
    }

    public override void Dispose()
    {
        httpClient.Dispose();
        base.Dispose();
    }
}
