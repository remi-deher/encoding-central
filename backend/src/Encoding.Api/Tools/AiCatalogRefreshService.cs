using Microsoft.Extensions.Hosting;

namespace Encoding.Api.Tools;

/// <summary>Keeps local provider status and observed model catalogs warm without blocking API reads.</summary>
public sealed class AiCatalogRefreshService(AiClusterService cluster, ILogger<AiCatalogRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await cluster.CachedViewAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning("AI catalog refresh failed: {Failure}", exception.GetType().Name); }

            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
