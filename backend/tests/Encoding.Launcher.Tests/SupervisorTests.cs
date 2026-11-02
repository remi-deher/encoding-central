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
            () => new Dictionary<string, string>(),
            () => new Dictionary<string, string> { ["Node__Roles"] = "server,worker" },
            TimeSpan.FromSeconds(1), () => now);
        var token = CancellationToken.None;

        await supervisor.ReconcileAsync(token);
        Check.That(server.IsRunning && agent.IsRunning, "Le central démarre toujours le serveur et l’agent.");
        Check.That(agent.LastEnvironment["Node__Roles"] == "server,worker", "L’agent du central déclare les deux fonctions.");

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

        agent.Crash();
        await supervisor.ReconcileAsync(token);
        Check.That(agent.IsRunning, "L’agent arrêté sans demande est relancé.");

        await supervisor.StopAllAsync();
        Check.That(!agent.IsRunning && !server.IsRunning, "L’arrêt du conteneur arrête les processus.");
    }
}
