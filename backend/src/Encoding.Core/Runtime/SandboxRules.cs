namespace Encoding.Core.Runtime;

/// <summary>Construction déterministe des règles Landlock (aucun appel système) : testable sans noyau compatible.</summary>
public static class SandboxRules
{
    public const ulong Execute = 1UL, WriteFile = 1UL << 1, ReadFile = 1UL << 2, ReadDirectory = 1UL << 3, Refer = 1UL << 13, Truncate = 1UL << 14;
    public const ulong Read = Execute | ReadFile | ReadDirectory;
    /// <summary>Landlock ABI 4 : <c>LANDLOCK_ACCESS_NET_BIND_TCP</c> et <c>LANDLOCK_ACCESS_NET_CONNECT_TCP</c>.</summary>
    public const ulong BindTcp = 1UL, ConnectTcp = 1UL << 1;
    public const int NetworkAbi = 4;

    /// <summary>Droits système de fichiers gérés par la version d'ABI : tout ce qui n'est pas autorisé par une règle est refusé.</summary>
    public static ulong FilesystemAccess(int abi)
    {
        var all = (1UL << 13) - 1;
        if (abi >= 2) all |= Refer;
        if (abi >= 3) all |= Truncate;
        return all;
    }

    /// <summary>
    /// Droits réseau gérés. Sans règle réseau, gérer un droit revient à le refuser : sans capacité, le bind et le connect TCP sont
    /// refusés (ABI ≥ 4). Avant l'ABI 4 le noyau ne sait pas le faire : le filtre seccomp (<see cref="SeccompFilter"/>) refuse alors les sockets.
    /// </summary>
    public static ulong NetworkAccess(int abi, SandboxPolicy policy) => abi >= NetworkAbi && !policy.AllowNetwork ? BindTcp | ConnectTcp : 0;

    /// <summary>Landlock ABI 6 : <c>LANDLOCK_SCOPE_ABSTRACT_UNIX_SOCKET</c> (pas de connexion à un socket abstrait créé hors du domaine).</summary>
    public const ulong ScopeAbstractUnixSocket = 1UL;
    public const int ScopeAbi = 6;

    /// <summary>Portées gérées : les plugins ne joignent pas les sockets abstraits de l'hôte (D-Bus, X11). Les CLI IA sont laissés tels quels.</summary>
    public static ulong Scopes(int abi, SandboxPolicy policy) => abi >= ScopeAbi && !policy.HostProcessAccess ? ScopeAbstractUnixSocket : 0;

    /// <summary>Taille de <c>landlock_ruleset_attr</c> : champ réseau à partir de l'ABI 4, portées à partir de l'ABI 6.</summary>
    public static int RulesetSize(int abi) => abi >= ScopeAbi ? 24 : abi >= NetworkAbi ? 16 : 8;

    private static readonly string[] SystemPaths =
    [
        "/usr", "/bin", "/lib", "/lib64", "/etc/passwd", "/etc/ssl/certs", "/etc/ssl/openssl.cnf", "/usr/lib/ssl/openssl.cnf",
        "/etc/resolv.conf", "/etc/hosts", "/etc/nsswitch.conf", "/etc/ld.so.cache", "/dev/urandom", "/dev/random",
    ];

    /// <summary>
    /// Pseudo-systèmes de fichiers en lecture seule. <c>/proc/self</c> est résolu en <c>/proc/&lt;pid&gt;</c> par l'appelant, qui
    /// remplace son processus par l'outil (execve) : le PID est celui du plugin. Les autres processus (dont le worker et son
    /// <c>environ</c>) ne sont pas lisibles. Les fichiers globaux listés servent au runtime (nombre de CPU, mémoire, limites cgroup).
    /// </summary>
    private static readonly string[] StrictProcessPaths =
    [
        "/proc/self", "/proc/cpuinfo", "/proc/meminfo", "/proc/stat", "/proc/uptime", "/proc/loadavg",
        "/sys/devices/system/cpu", "/sys/fs/cgroup",
    ];

    private static readonly string[] HostProcessPaths = ["/proc", "/sys"];

    /// <summary>Chemins système lisibles selon la politique (le dossier de travail et les chemins du plugin s'y ajoutent).</summary>
    public static IReadOnlyList<string> ReadOnlyPaths(SandboxPolicy policy)
        => SystemPaths.Concat(policy.HostProcessAccess ? HostProcessPaths : StrictProcessPaths).ToArray();
}
