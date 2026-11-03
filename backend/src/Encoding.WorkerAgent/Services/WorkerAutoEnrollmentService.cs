namespace Encoding.WorkerAgent.Services;

/// <summary>
/// Enrôle le worker sans intervention : le worker local de l'image unique rejoint toujours le serveur de son conteneur
/// (architecture 0037) ; sinon, l'acceptation automatique optionnelle accepte un central unique détecté.
/// </summary>
public sealed class WorkerAutoEnrollmentService(
    WorkerAgentOptions options,
    WorkerIdentityStore identityStore,
    WorkerCentralDiscovery discovery,
    WorkerEnrollmentService enrollment,
    ILogger<WorkerAutoEnrollmentService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var localEnabled = options.LocalEnrollmentSecretFile is not null;
        if (!options.AutomaticEnrollmentEnabled && !localEnabled)
        {
            logger.LogInformation("L’acceptation automatique des enrôlements est désactivée.");
            return;
        }

        if (localEnabled) logger.LogInformation("Le worker local rejoint automatiquement le serveur de ce conteneur.");
        if (options.AutomaticEnrollmentEnabled) logger.LogInformation("L’acceptation automatique est activée ; un seul central détecté sera accepté automatiquement.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var identity = await identityStore.GetAsync(stoppingToken);
                var hasActivePairing = identity.PairingExpiresAt > DateTimeOffset.UtcNow
                    && !string.IsNullOrWhiteSpace(identity.PairingKey);
                if (hasActivePairing)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }
                var clusters = discovery.List();
                var local = clusters.FirstOrDefault(enrollment.IsLocalCluster);
                // Un worker qui a déjà choisi un autre central (jumelage en cours ou enrôlé) n'est pas ramené au serveur local.
                var choseElsewhere = identity.HasCredentials && local is not null && identity.CentralId != local.Id.ToString("D");
                if (!identity.IsEnrolled && local is not null && !choseElsewhere)
                {
                    await enrollment.EnrollAsync(local.Id, stoppingToken);
                    logger.LogInformation("Le worker local a rejoint le central {ClusterId}.", local.Id);
                }
                else if (options.AutomaticEnrollmentEnabled && !identity.IsEnrolled
                    && GetAutomaticEnrollmentTarget(clusters, options.CentralApiBaseUrl is not null) is { } automaticTarget)
                {
                    await enrollment.EnrollAsync(automaticTarget.Id, stoppingToken);
                    logger.LogInformation("L’enrôlement automatique auprès du central {ClusterId} est terminé.", automaticTarget.Id);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "L’enrôlement automatique n’a pas abouti ; une nouvelle tentative sera faite.");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    internal static DiscoveredCentral? GetAutomaticEnrollmentTarget(IReadOnlyList<DiscoveredCentral> clusters, bool hasConfiguredApiBaseUrl) =>
        clusters.FirstOrDefault(cluster => cluster.Source == "configured")
        ?? (hasConfiguredApiBaseUrl || clusters.Count != 1 ? null : clusters[0]);
}
