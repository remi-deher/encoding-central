namespace Encoding.Api.Executions;

/// <summary>Bootstraps and projects bounded ownership batches without blocking the API or the mesh.</summary>
public sealed class TaskOwnershipService(TaskOwnershipRepository ownership,ILogger<TaskOwnershipService> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try {await ownership.PrepareClaimsAsync(ct);}
            catch(OperationCanceledException) when(ct.IsCancellationRequested) {return;}
            catch(Exception ex) {logger.LogWarning("Réconciliation de la propriété différée ({Type}).",ex.GetType().Name);}
            try {await Task.Delay(TimeSpan.FromSeconds(2),ct);} catch(OperationCanceledException) when(ct.IsCancellationRequested) {return;}
        }
    }
}
