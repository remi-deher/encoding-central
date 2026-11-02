namespace Encoding.Api.Resources;

/// <summary>
/// Précharge toutes les versions de chaque ressource dès que ses métadonnées sont arrivées par réconciliation :
/// un worker qui en a besoin la trouve déjà dans le magasin local.
/// </summary>
public sealed class ResourcePrefetchService(ResourceRepository repository, ResourceBlobFetcher fetcher, ILogger<ResourcePrefetchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var file in await repository.MissingFilesAsync(fetcher.IsPresent, stoppingToken))
                    await fetcher.EnsureAsync(file.Sha256, file.Bytes, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Préchargement des ressources différé.");
            }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
