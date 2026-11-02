using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Encoding.Core.Runtime;

/// <summary>Linux filesystem boundary for untrusted child processes. No insecure fallback.</summary>
public static class ProcessSandbox
{
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long Syscall(long number, nint first, nint second, nint third, nint fourth);
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc")] private static extern int close(int fd);
    [DllImport("libc")] private static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);
    [StructLayout(LayoutKind.Sequential)] private struct Ruleset { public ulong Access; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct PathRule { public ulong Access; public int Parent; }
    /// <summary>
    /// Variable posée par l'hôte sur le processus relais pour demander un environnement minimal à l'enfant isolé
    /// (plugins et scripts). Sans elle l'enfant hérite de l'environnement du relais (CLI IA, qui préparent le leur).
    /// </summary>
    public const string MinimalEnvironmentVariable = "ENCODING_SANDBOX_ENV";

    private static readonly string[] InheritedVariables = ["PATH", "LANG", "LC_ALL", "LC_CTYPE", "TZ"];

    /// <summary>
    /// Environnement d'un processus isolé : liste blanche, jamais l'environnement du worker (chaînes de connexion, clés,
    /// variables de configuration). HOME et TMPDIR pointent vers l'espace de travail, seul dossier inscriptible.
    /// </summary>
    public static IReadOnlyDictionary<string, string> MinimalEnvironment(IReadOnlyDictionary<string, string?> current, string workingDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in InheritedVariables)
            if (current.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value)) result[name] = value;
        result.TryAdd("PATH", "/usr/local/bin:/usr/bin:/bin");
        result.TryAdd("LC_ALL", "C.UTF-8");
        result["HOME"] = workingDirectory;
        result["TMPDIR"] = workingDirectory;
        result["PYTHONDONTWRITEBYTECODE"] = "1";
        return result;
    }

    public static bool IsAvailable => OperatingSystem.IsLinux() && Syscall(444, 0, 0, 1, 0) >= 1;

    public static int Run(string[] args)
    {
        if (args.Length < 4 || !IsAvailable) return 79;
        var descriptors = new List<int>();
        nint buffer = 0;
        var rulesFd = -1;
        try
        {
            var abi = Syscall(444, 0, 0, 1, 0);
            ulong all = (1UL << 13) - 1;
            if (abi >= 2) all |= 1UL << 13;
            if (abi >= 3) all |= 1UL << 14;
            buffer = Marshal.AllocHGlobal(16);
            Marshal.StructureToPtr(new Ruleset { Access = all }, buffer, false);
            rulesFd = (int)Syscall(444, buffer, 8, 0, 0);
            if (rulesFd < 0) return 79;
            const ulong read = 1UL | (1UL << 2) | (1UL << 3);
            void Allow(string path, ulong access)
            {
                if (!File.Exists(path) && !Directory.Exists(path)) return;
                var fd = open(path, 0x200000 | 0x80000); // O_PATH | O_CLOEXEC
                if (fd < 0) throw new IOException("Sandbox path unavailable.");
                descriptors.Add(fd);
                if (!Directory.Exists(path)) access &= 1UL | (1UL << 1) | (1UL << 2) | (1UL << 14);
                Marshal.StructureToPtr(new PathRule { Access = access, Parent = fd }, buffer, false);
                if (Syscall(445, rulesFd, 1, buffer, 0) != 0) throw new IOException("Sandbox rule rejected.");
            }
            Allow(Path.GetFullPath(args[1]), all);
            foreach (var path in new[] { "/usr", "/bin", "/lib", "/lib64", "/proc", "/sys", "/etc/passwd", "/etc/ssl/certs", "/etc/ssl/openssl.cnf",
                "/usr/lib/ssl/openssl.cnf", "/etc/resolv.conf", "/etc/hosts", "/etc/nsswitch.conf", "/etc/ld.so.cache", "/dev/urandom",
                "/dev/random" }) Allow(path, read);
            Allow("/dev/null", (1UL << 1) | (1UL << 2));
            foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory).Where(path => path.EndsWith(".dll") || path.EndsWith(".deps.json")
                || path.EndsWith(".runtimeconfig.json"))) Allow(path, read);
            Allow(args[2], read);
            foreach (var path in JsonSerializer.Deserialize<string[]>(args[3]) ?? []) Allow(Path.GetFullPath(path), read);
            if (prctl(38, 1, 0, 0, 0) != 0 || Syscall(446, rulesFd, 0, 0, 0) != 0) return 79;
            var info = new ProcessStartInfo(args[2]) { WorkingDirectory = args[1], UseShellExecute = false };
            foreach (var arg in args.Skip(4)) info.ArgumentList.Add(arg);
            if (Environment.GetEnvironmentVariable(MinimalEnvironmentVariable) == "minimal")
            {
                var current = info.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                info.Environment.Clear();
                foreach (var (name, value) in MinimalEnvironment(current, Path.GetFullPath(args[1]))) info.Environment[name] = value;
            }
            using var child = Process.Start(info) ?? throw new IOException("Child unavailable.");
            child.WaitForExit();
            return child.ExitCode;
        }
        catch { return 79; }
        finally
        {
            foreach (var fd in descriptors) close(fd);
            if (rulesFd >= 0) close(rulesFd);
            if (buffer != 0) Marshal.FreeHGlobal(buffer);
        }
    }
}
