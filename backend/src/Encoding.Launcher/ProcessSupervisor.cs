using Encoding.Core.Nodes;

namespace Encoding.Launcher;

internal interface IManagedProcess
{
    bool IsRunning { get; }
    void Start(IReadOnlyDictionary<string, string> environment);
    /// <summary>Demande un arrêt propre puis, passé le délai, un arrêt forcé.</summary>
    Task StopAsync(TimeSpan grace);
}

/// <summary>
/// Maintient les processus du nœud conformes à son mode : le serveur (API et interface) ne tourne que dans les modes
/// qui l’incluent ; l’agent tourne toujours (présence dans le cluster, découverte) et redémarre quand sa fonction worker change.
/// Un processus qui s’arrête de lui-même est relancé avec un délai croissant.
/// </summary>
internal sealed class ProcessSupervisor(
    IManagedProcess server,
    IManagedProcess agent,
    Func<NodeMode, IReadOnlyDictionary<string, string>> serverEnvironment,
    Func<NodeMode, IReadOnlyDictionary<string, string>> agentEnvironment,
    TimeSpan grace,
    Func<DateTimeOffset> clock)
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<IManagedProcess, (int Failures, DateTimeOffset NextStart, DateTimeOffset StartedAt)> history = [];
    private NodeMode mode = NodeMode.Both;
    private NodeMode? agentRunsAs;

    public NodeMode Mode => mode;

    /// <summary>Adopte un nouveau mode : arrête ce qui ne doit plus tourner, relance l’agent si ses rôles changent, démarre le reste.</summary>
    public async Task ApplyAsync(NodeMode desired, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            mode = NodeMode.Validate(desired);
            if (!mode.Server && server.IsRunning) await StopOnPurposeAsync(server);
            if (agent.IsRunning && agentRunsAs is { } running && running != mode)
                await StopOnPurposeAsync(agent);
            Reconcile();
        }
        finally { gate.Release(); }
    }

    // Un arrêt voulu n'est pas une panne : il ne compte pas dans le délai de relance.
    private async Task StopOnPurposeAsync(IManagedProcess process)
    {
        await process.StopAsync(grace);
        history.Remove(process);
    }

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
        if (mode.Server) EnsureRunning(server, serverEnvironment(mode));
        if (!agent.IsRunning) agentRunsAs = null;
        if (EnsureRunning(agent, agentEnvironment(mode))) agentRunsAs = mode;
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
