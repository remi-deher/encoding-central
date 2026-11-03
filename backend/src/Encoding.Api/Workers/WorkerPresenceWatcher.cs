using Encoding.Api.Infrastructure;

namespace Encoding.Api.Workers;

/// <summary>
/// Annonce aux interfaces (sujet « workers ») tout changement de présence ou d'état des workers.
/// Le statut « en ligne » se calcule à la lecture à partir du dernier signal de vie : un worker qui s'éteint ou qui revient
/// ne passe par aucune requête de mutation, donc aucune route ne peut l'annoncer. Cet observateur compare la liste à intervalle court.
/// </summary>
public sealed class WorkerPresenceWatcher(
    WorkerManagementService service,
    WorkerPresenceOptions options,
    InterfaceLiveBroadcaster live,
    ILogger<WorkerPresenceWatcher> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? previous = null;
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var workers = await service.ListAsync(options.OfflineAfter, stoppingToken);
                    var signature = string.Join('|', workers
                        .OrderBy(worker => worker.Id)
                        .Select(worker => $"{worker.Id:N}:{worker.Status}:{worker.Lifecycle}:{worker.MaxConcurrentTasks}"));
                    if (previous is not null && !string.Equals(previous, signature, StringComparison.Ordinal))
                        live.Publish("workers");
                    previous = signature;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogDebug(exception, "La présence des workers n'a pas pu être relevée.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
