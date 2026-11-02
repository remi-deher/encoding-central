using Encoding.Core.Nodes;

namespace Encoding.Launcher.Tests;

internal sealed class FakeProcess : IManagedProcess
{
    public bool IsRunning { get; private set; }
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public IReadOnlyDictionary<string, string> LastEnvironment { get; private set; } = new Dictionary<string, string>();
    public void Start(IReadOnlyDictionary<string, string> environment) { IsRunning = true; Starts++; LastEnvironment = environment; }
    public Task StopAsync(TimeSpan grace) { IsRunning = false; Stops++; return Task.CompletedTask; }
    public void Crash() => IsRunning = false;
}

internal static class SupervisorTests
{
    public static async Task RunAsync()
    {
        var now = DateTimeOffset.UnixEpoch;
        var server = new FakeProcess();
        var agent = new FakeProcess();
        var supervisor = new ProcessSupervisor(
            server, agent,
            _ => new Dictionary<string, string>(),
            mode => new Dictionary<string, string> { ["Node__Roles"] = mode.ToRoles() },
            TimeSpan.FromSeconds(1), () => now);
        var token = CancellationToken.None;

        await supervisor.ApplyAsync(NodeMode.Both, token);
        Check.That(server.IsRunning && agent.IsRunning, "Le mode serveur et worker démarre les deux processus.");
        Check.That(agent.LastEnvironment["Node__Roles"] == "server,worker", "L’agent reçoit les rôles du mode.");

        await supervisor.ApplyAsync(new NodeMode(false, true), token);
        Check.That(!server.IsRunning && server.Stops == 1, "Passer en worker seul arrête le serveur.");
        Check.That(agent.Stops == 1 && agent.Starts == 2 && agent.LastEnvironment["Node__Roles"] == "worker", "L’agent est relancé avec ses nouveaux rôles quand la fonction serveur change.");

        await supervisor.ApplyAsync(new NodeMode(true, false), token);
        Check.That(server.IsRunning, "Repasser en serveur démarre le serveur.");
        Check.That(agent.Stops == 2 && agent.Starts == 3 && agent.LastEnvironment["Node__Roles"] == "server",
            "L’agent est relancé, après un arrêt propre, quand il perd la fonction worker.");

        await supervisor.ApplyAsync(NodeMode.Both, token);
        Check.That(agent.Starts == 4 && agent.LastEnvironment["Node__Roles"] == "server,worker", "L’agent est relancé quand il gagne la fonction worker.");
        Check.That(agent.Starts == 4, "Un arrêt voulu ne compte pas comme une panne : la relance est immédiate.");

        // Panne : relance après un délai croissant, jamais en boucle serrée.
        server.Crash();
        await supervisor.ReconcileAsync(token);
        Check.That(server.IsRunning, "Un serveur arrêté sans demande est relancé.");
        var starts = server.Starts;
        server.Crash();
        await supervisor.ReconcileAsync(token);
        Check.That(server.Starts == starts, "Une seconde panne immédiate n’est pas relancée avant le délai.");
        now += TimeSpan.FromSeconds(3);
        await supervisor.ReconcileAsync(token);
        Check.That(server.Starts == starts + 1, "Le serveur est relancé une fois le délai écoulé.");

        await supervisor.ApplyAsync(new NodeMode(false, true), token);
        server.Crash();
        await supervisor.ReconcileAsync(token);
        Check.That(!server.IsRunning, "Un serveur retiré du mode n’est pas relancé.");

        await supervisor.StopAllAsync();
        Check.That(!agent.IsRunning, "L’arrêt du conteneur arrête les processus.");
        await Task.CompletedTask;
    }
}
