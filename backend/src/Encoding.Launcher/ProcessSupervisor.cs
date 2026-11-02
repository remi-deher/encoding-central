namespace Encoding.Launcher;

internal interface IManagedProcess
{
    bool IsRunning { get; }
    void Start(IReadOnlyDictionary<string, string> environment);
    /// <summary>Demande un arrêt propre puis, passé le délai, un arrêt forcé.</summary>
    Task StopAsync(TimeSpan grace);
}

/// <summary>
/// Maintient les deux processus du central : le serveur (API et interface) et l’agent (worker intégré, identité, présence).
/// Les deux tournent toujours. Un processus qui s’arrête de lui-même est relancé avec un délai croissant.
/// </summary>
internal sealed class ProcessSupervisor(
    IManagedProcess server,
    IManagedProcess agent,
    Func<IReadOnlyDictionary<string, string>> serverEnvironment,
    Func<IReadOnlyDictionary<string, string>> agentEnvironment,
    TimeSpan grace,
    Func<DateTimeOffset> clock)
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<IManagedProcess, (int Failures, DateTimeOffset NextStart, DateTimeOffset StartedAt)> history = [];

    /// <summary>Relance un processus attendu mais arrêté, en respectant le délai de relance ; appelé périodiquement.</summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { Reconcile(); }
        finally { gate.Release(); }
    }

    public async Task StopAllAsync()
    {
        await gate.WaitAsync();
        try
        {
            await Task.WhenAll(server.IsRunning ? server.StopAsync(grace) : Task.CompletedTask,
                agent.IsRunning ? agent.StopAsync(grace) : Task.CompletedTask);
        }
        finally { gate.Release(); }
    }

    private void Reconcile()
    {
        EnsureRunning(server, serverEnvironment());
        EnsureRunning(agent, agentEnvironment());
    }

    private bool EnsureRunning(IManagedProcess process, IReadOnlyDictionary<string, string> environment)
    {
        var now = clock();
        history.TryGetValue(process, out var state);
        if (process.IsRunning)
        {
            // Un processus resté vingt secondes en marche est considéré comme sain : le délai de relance repart de zéro.
            if (state.Failures > 0 && now - state.StartedAt > TimeSpan.FromSeconds(20)) history[process] = (0, now, state.StartedAt);
            return false;
        }
        if (now < state.NextStart) return false;
        var failures = state.StartedAt == default ? 0 : state.Failures + 1;
        process.Start(environment);
        history[process] = (failures, now + Backoff[Math.Min(failures, Backoff.Length - 1)], now);
        return true;
    }
}
