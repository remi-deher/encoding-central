namespace Encoding.Launcher;

/// <summary>Démarre l’API et l’agent local, les surveille et les arrête proprement avec le conteneur.</summary>
internal sealed class LauncherService(ProcessSupervisor supervisor) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await supervisor.ReconcileAsync(cancellationToken);
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
