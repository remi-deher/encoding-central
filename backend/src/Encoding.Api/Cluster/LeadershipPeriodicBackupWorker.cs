namespace Encoding.Api.Cluster;

/// <summary>
/// Service d'arrière-plan exécuté sur le Maître pour créer des sauvegardes miroir SQLite à intervalle régulier (Cas B).
/// </summary>
public sealed class LeadershipPeriodicBackupWorker(
    LeadershipRepository repository,
    LeadershipHandoffService handoffService,
    ILogger<LeadershipPeriodicBackupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        // Laisser le serveur démarrer complètement avant la première vérification
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var intervalMinutes = await repository.GetBackupIntervalMinutesAsync(stoppingToken);
                if (intervalMinutes is > 0)
                {
                    var interval = TimeSpan.FromMinutes(intervalMinutes.Value);
                    var latest = await repository.GetLatestBackupAsync(stoppingToken);
                    var due = (latest is not null && (DateTimeOffset.UtcNow - latest.CreatedAt) >= interval)
                           || (latest is null && (DateTimeOffset.UtcNow - startedAt) >= interval);
                    if (due)
                    {
                        logger.LogInformation("L'intervalle de sauvegarde miroir ({Minutes} min) est atteint. Lancement du snapshot...", intervalMinutes.Value);
                        await handoffService.CreatePeriodicBackupAsync(stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Erreur lors de la génération de la sauvegarde miroir périodique.");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
