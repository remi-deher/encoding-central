using Encoding.Core.Nodes;

namespace Encoding.Launcher;

/// <summary>Applique le mode au démarrage, surveille les processus et les arrête proprement avec le conteneur.</summary>
internal sealed class LauncherService(ProcessSupervisor supervisor, NodeModeStore store, ILogger<LauncherService> logger) : BackgroundService
{
    private static readonly TimeSpan ApplyDelay = TimeSpan.FromSeconds(1);
    private CancellationToken stopping;

    /// <summary>Le changement est appliqué après la réponse HTTP : l’appelant (le serveur lui-même) la reçoit avant son éventuel arrêt.</summary>
    public void ScheduleApply(NodeMode mode) => _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(ApplyDelay, stopping);
            await supervisor.ApplyAsync(mode, stopping);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { logger.LogError(exception, "Le changement de mode a échoué."); }
    });

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        stopping = cancellationToken;
        await supervisor.ApplyAsync(store.Load(), cancellationToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await supervisor.ReconcileAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await supervisor.StopAllAsync();
    }
}
