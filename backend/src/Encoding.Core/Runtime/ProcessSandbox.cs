using System.Runtime.InteropServices;
using System.Text.Json;

namespace Encoding.Core.Runtime;

/// <summary>
/// Linux boundary for untrusted child processes: Landlock (filesystem, TCP from ABI 4) plus a minimal seccomp filter. No insecure
/// fallback: any rule that cannot be applied ends with exit code 79 before the tool starts. The relay replaces itself with the tool
/// (execve), so <c>/proc/self</c> opened here is the tool's own process directory.
/// </summary>
public static class ProcessSandbox
{
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long Syscall(long number, nint first, nint second, nint third, nint fourth);
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc")] private static extern int close(int fd);
    [DllImport("libc")] private static extern int prctl(int option, ulong arg2, ulong arg3, ulong arg4, ulong arg5);
    [DllImport("libc", SetLastError = true)] private static extern int execvpe(string file, string?[] argv, string?[] envp);
    [DllImport("libc")] private static extern int sigprocmask(int how, byte[] set, byte[]? previous);
    [DllImport("libc")] private static extern nint signal(int number, nint handler);
    [StructLayout(LayoutKind.Sequential)] private struct Ruleset { public ulong Access; public ulong Network; public ulong Scoped; }
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

    public static bool IsAvailable => OperatingSystem.IsLinux() && SeccompFilter.IsSupported(RuntimeInformation.ProcessArchitecture)
        && prctl(21, 0, 0, 0, 0) >= 0 && Syscall(444, 0, 0, 1, 0) >= 1;

    public static int Run(string[] args)
    {
        if (args.Length < 4 || !IsAvailable) return 79;
        var descriptors = new List<int>();
        nint buffer = 0, program = 0;
        var rulesFd = -1;
        try
        {
            var policy = SandboxPolicy.Parse(Environment.GetEnvironmentVariable(SandboxPolicy.EnvironmentVariable));
            var abi = (int)Syscall(444, 0, 0, 1, 0);
            var all = SandboxRules.FilesystemAccess(abi);
            buffer = Marshal.AllocHGlobal(24);
            Marshal.StructureToPtr(new Ruleset { Access = all, Network = SandboxRules.NetworkAccess(abi, policy), Scoped = SandboxRules.Scopes(abi, policy) }, buffer, false);
            rulesFd = (int)Syscall(444, buffer, SandboxRules.RulesetSize(abi), 0, 0);
            if (rulesFd < 0) return 79;
            void Allow(string path, ulong access)
            {
                if (!File.Exists(path) && !Directory.Exists(path)) return;
                var fd = open(path, 0x200000 | 0x80000); // O_PATH | O_CLOEXEC
                if (fd < 0) throw new IOException("Sandbox path unavailable.");
                descriptors.Add(fd);
                if (!Directory.Exists(path)) access &= SandboxRules.Execute | SandboxRules.WriteFile | SandboxRules.ReadFile | SandboxRules.Truncate;
                Marshal.StructureToPtr(new PathRule { Access = access, Parent = fd }, buffer, false);
                if (Syscall(445, rulesFd, 1, buffer, 0) != 0) throw new IOException("Sandbox rule rejected.");
            }
            Allow(Path.GetFullPath(args[1]), all);
            foreach (var path in SandboxRules.ReadOnlyPaths(policy)) Allow(path, SandboxRules.Read);
            Allow("/dev/null", SandboxRules.WriteFile | SandboxRules.ReadFile);
            Allow(args[2], SandboxRules.Read);
            foreach (var path in JsonSerializer.Deserialize<string[]>(args[3]) ?? []) Allow(Path.GetFullPath(path), SandboxRules.Read);

            // Prepared before the restriction (managed code may still load assemblies): afterwards only execve remains.
            var current = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value, StringComparer.Ordinal);
            var minimal = current.GetValueOrDefault(MinimalEnvironmentVariable) == "minimal";
            current.Remove(SandboxPolicy.EnvironmentVariable);
            var environment = minimal ? MinimalEnvironment(current, Path.GetFullPath(args[1])) : current.Where(pair => pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);
            string?[] argv = [args[2], .. args.Skip(4), null];
            string?[] envp = [.. environment.Select(pair => pair.Key + "=" + pair.Value), null];
            Directory.SetCurrentDirectory(args[1]);
            sigprocmask(2, new byte[128], null); // SIG_SETMASK with an empty set: the runtime's blocked signals must not reach the tool
            signal(13, 0); // SIGPIPE: the runtime ignores it, the tool must get the default disposition back through execve
            execvpe("", argv, envp); // always fails (ENOENT): binds the native entry point now, before the restriction

            var filter = SeccompFilter.Encode(SeccompFilter.Build(RuntimeInformation.ProcessArchitecture, policy.AllowNetwork));
            program = Marshal.AllocHGlobal(16 + filter.Length);
            Marshal.Copy(filter, 0, program + 16, filter.Length);
            Marshal.WriteInt64(program, 0);
            Marshal.WriteInt16(program, (short)(filter.Length / 8)); // struct sock_fprog { u16 len; sock_filter* filter; }
            Marshal.WriteIntPtr(program, 8, program + 16);
            if (prctl(38, 1, 0, 0, 0) != 0 || Syscall(446, rulesFd, 0, 0, 0) != 0 || prctl(22, 2, (ulong)program, 0, 0) != 0) return 79;

            // Replaces this process: same PID, so the /proc/self rule above stays the tool's own directory. Returns only on failure.
            execvpe(args[2], argv, envp);
            return 79;
        }
        catch { return 79; }
        finally
        {
            foreach (var fd in descriptors) close(fd);
            if (rulesFd >= 0) close(rulesFd);
            if (buffer != 0) Marshal.FreeHGlobal(buffer);
            if (program != 0) Marshal.FreeHGlobal(program);
        }
    }
}
