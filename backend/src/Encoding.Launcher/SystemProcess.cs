using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Encoding.Launcher;

/// <summary>Programme .NET publié, lancé comme processus enfant ; ses journaux vont sur la sortie du conteneur.</summary>
internal sealed class SystemProcess(string name, string directory, string assembly) : IManagedProcess
{
    private Process? process;

    public bool IsRunning => process is { HasExited: false };

    public void Start(IReadOnlyDictionary<string, string> environment)
    {
        var info = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, UseShellExecute = false };
        info.ArgumentList.Add(Path.Combine(directory, assembly));
        foreach (var (key, value) in environment) info.Environment[key] = value;
        process = Process.Start(info) ?? throw new InvalidOperationException($"Le processus {name} n’a pas démarré.");
        Console.WriteLine($"[launcher] {name} démarré (pid {process.Id}).");
    }

    public async Task StopAsync(TimeSpan grace)
    {
        if (process is not { HasExited: false } running) return;
        Console.WriteLine($"[launcher] arrêt de {name} (pid {running.Id}).");
        if (OperatingSystem.IsWindows() || SendTerminate(running.Id) != 0) running.Kill(true);
        using var cancellation = new CancellationTokenSource(grace);
        try { await running.WaitForExitAsync(cancellation.Token); }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[launcher] {name} ne s’est pas arrêté en {grace.TotalSeconds:0} s : arrêt forcé.");
            running.Kill(true);
            await running.WaitForExitAsync();
        }
    }

    private static int SendTerminate(int pid) => Kill(pid, 15);

    [DllImport("libc", EntryPoint = "kill")]
    private static extern int Kill(int pid, int signal);
}
