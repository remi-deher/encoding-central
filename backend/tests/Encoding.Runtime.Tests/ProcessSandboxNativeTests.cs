using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Encoding.Api.Tools;
using Encoding.Core.Runtime;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>
/// Confinement réel (Landlock + seccomp) d'un processus relancé par le point d'entrée <c>--isolated-process</c>. Exécuté sur tout poste
/// compatible ; le job CI <c>ai-cli-native</c> définit <c>ENCODING_TEST_LANDLOCK=1</c> et exige alors un noyau compatible.
/// Le réseau utilisé est le bouclage local uniquement.
/// </summary>
public sealed class ProcessSandboxNativeTests
{
    // Le script rend compte de ce qui est possible depuis l'intérieur ($1 : port d'écoute en bouclage, $2 : PID d'un autre processus de même utilisateur ; exit 10 : /proc/self illisible).
    private const string Script = """
        read -r _ < /proc/$$/stat || exit 10
        read -r _ < /proc/self/stat || exit 10
        if ( true < "/proc/$2/cmdline" ) 2>/dev/null; then host_proc=1; else host_proc=0; fi
        if ( exec 3<>"/dev/tcp/127.0.0.1/$1" ) 2>/dev/null; then tcp=1; else tcp=0; fi
        if ( exec 3<>"/dev/udp/127.0.0.1/9" ) 2>/dev/null; then udp=1; else udp=0; fi
        echo "host_proc=$host_proc tcp=$tcp udp=$udp"
        """;

    private static bool Prepare(out string bash)
    {
        bash = new[] { "/usr/bin/bash", "/bin/bash" }.FirstOrDefault(File.Exists) ?? "";
        var required = Environment.GetEnvironmentVariable("ENCODING_TEST_LANDLOCK") == "1";
        var available = OperatingSystem.IsLinux() && ProcessSandbox.IsAvailable && bash.Length > 0;
        if (required) Assert.True(available, "Landlock, seccomp and bash are required by the native sandbox check.");
        return available;
    }

    private static async Task<(int Exit, string Output, bool Connected)> RunAsync(string bash, SandboxPolicy? policy, string? rawPolicy = null)
    {
        var work = Path.Combine(Path.GetTempPath(), "encoding-sandbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        // Processus témoin, hors du bac à sable : son environnement est lisible par le même utilisateur sauf confinement.
        using var other = Process.Start(new ProcessStartInfo(bash, ["-c", "sleep 60"]) { UseShellExecute = false })!;
        try
        {
            var launcher = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet"));
            var info = new ProcessStartInfo(launcher) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = work };
            // Le point d'entrée du service (Encoding.Api) est celui que les plugins relancent en production.
            foreach (var argument in new[] { typeof(AiCliRuntime).Assembly.Location, "--isolated-process", work, bash, "[]", "-c", Script, "sandbox", ((IPEndPoint)listener.LocalEndpoint).Port.ToString(), other.Id.ToString() }) info.ArgumentList.Add(argument);
            info.Environment[ProcessSandbox.MinimalEnvironmentVariable] = "minimal";
            info.Environment.Remove(SandboxPolicy.EnvironmentVariable);
            if (policy is not null) info.Environment[SandboxPolicy.EnvironmentVariable] = rawPolicy ?? policy.Serialize();
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output + await error, listener.Pending());
        }
        finally
        {
            listener.Stop();
            other.Kill(true);
            Directory.Delete(work, true);
        }
    }

    [Fact]
    public async Task PluginWithoutCapabilityHasNoNetworkAndNoForeignProcAccess()
    {
        if (!Prepare(out var bash)) return;
        var (exit, output, connected) = await RunAsync(bash, null);
        Assert.True(exit == 0, output);
        Assert.Contains("host_proc=0 tcp=0 udp=0", output);
        Assert.False(connected, "No TCP connection reaches the loopback listener.");
    }

    [Fact]
    public async Task PluginDeclaringNetworkConnectsButStillCannotReadForeignProc()
    {
        if (!Prepare(out var bash)) return;
        var (exit, output, connected) = await RunAsync(bash, SandboxPolicy.Network);
        Assert.True(exit == 0, output);
        Assert.Contains("host_proc=0 tcp=1 udp=1", output);
        Assert.True(connected, "The declared capability lets the plugin reach the loopback listener.");
    }

    [Fact]
    public async Task AiCliProfileKeepsNetworkAndTheWideProcAccess()
    {
        if (!Prepare(out var bash)) return;
        var (exit, output, connected) = await RunAsync(bash, SandboxPolicy.AiCli);
        Assert.True(exit == 0, output);
        Assert.Contains("host_proc=1 tcp=1", output);
        Assert.True(connected);
    }

    [Fact]
    public async Task UnknownPolicyFailsClosedWithoutStartingTheTool()
    {
        if (!Prepare(out var bash)) return;
        var (exit, output, connected) = await RunAsync(bash, new SandboxPolicy(true, true), "network,whatever");
        Assert.Equal(79, exit);
        Assert.DoesNotContain("tcp=", output);
        Assert.False(connected);
    }
}
