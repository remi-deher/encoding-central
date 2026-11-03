using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Encoding.Core.Runtime;

/// <summary>
/// Filtre seccomp-BPF minimal du bac à sable (liste de refus, hérité par execve et les descendants). Il complète Landlock, qui ne
/// couvre ni UDP ni les sockets hors TCP et n'a pas de règle réseau avant l'ABI 4 :
/// <list type="bullet">
/// <item><c>socket</c> et <c>socketpair</c> n'acceptent que <c>AF_UNIX</c> sans capacité réseau, et <c>AF_UNIX</c>, <c>AF_INET</c>,
/// <c>AF_INET6</c>, <c>AF_NETLINK</c> (résolution d'adresses de la libc) avec elle ; EPERM sinon (<c>AF_PACKET</c>, <c>AF_VSOCK</c>…) ;</item>
/// <item>toujours refusés : <c>ptrace</c>, <c>process_vm_readv/writev</c> (lecture de la mémoire d'un autre processus),
/// <c>io_uring_*</c> (permet de créer des sockets sans passer par <c>socket</c>), <c>bpf</c>, <c>perf_event_open</c>, <c>userfaultfd</c> ;</item>
/// <item>architecture inconnue : le processus est tué. Seules x86-64 et ARM64 sont prises en charge (<see cref="IsSupported"/>).</item>
/// </list>
/// </summary>
public static class SeccompFilter
{
    public readonly record struct Instruction(ushort Code, byte JumpTrue, byte JumpFalse, uint K);

    public const int AfUnix = 1, AfInet = 2, AfInet6 = 10, AfNetlink = 16;

    /// <summary>Familles de sockets acceptées.</summary>
    public static IReadOnlyList<int> AllowedDomains(bool allowNetwork) => allowNetwork ? [AfUnix, AfInet, AfInet6, AfNetlink] : [AfUnix];

    public const uint ReturnAllow = 0x7fff0000, ReturnKillProcess = 0x80000000, ReturnErrnoPermission = 0x00050000 | 1;
    private const ushort Load = 0x20, JumpEqual = 0x15, JumpGreaterOrEqual = 0x35, Return = 0x06;
    private const uint OffsetNumber = 0, OffsetArchitecture = 4, OffsetFirstArgument = 16, X32Flag = 0x40000000;

    private sealed record Table(uint Audit, uint Socket, uint SocketPair, uint[] Denied, bool Is64BitX86);

    // Numéros d'appels système : x86-64 (arch/x86/entry/syscalls) et ARM64 (table générique du noyau).
    private static readonly Table X64 = new(0xC000003E, 41, 53, [101, 310, 311, 425, 426, 427, 321, 298, 323], true);
    private static readonly Table Arm64 = new(0xC00000B7, 198, 199, [117, 270, 271, 425, 426, 427, 280, 241, 282], false);

    public static bool IsSupported(Architecture architecture) => architecture is Architecture.X64 or Architecture.Arm64;

    private static Table For(Architecture architecture) => architecture switch
    {
        Architecture.X64 => X64,
        Architecture.Arm64 => Arm64,
        _ => throw new PlatformNotSupportedException("Seccomp : architecture non prise en charge.")
    };

    public static IReadOnlyList<Instruction> Build(Architecture architecture, bool allowNetwork)
    {
        var table = For(architecture);
        var program = new List<Instruction>();
        var labels = new Dictionary<string, int>();
        var fixups = new List<(int Index, string Label)>();
        void Emit(ushort code, uint k) => program.Add(new(code, 0, 0, k));
        void JumpTo(ushort code, uint k, string label) { fixups.Add((program.Count, label)); Emit(code, k); }

        Emit(Load, OffsetArchitecture);
        program.Add(new(JumpEqual, 1, 0, table.Audit));
        Emit(Return, ReturnKillProcess);
        Emit(Load, OffsetNumber);
        // L'ABI x32 (numéros avec le bit 30) contournerait la table ci-dessous : refusée en bloc.
        if (table.Is64BitX86) JumpTo(JumpGreaterOrEqual, X32Flag, "deny");
        foreach (var number in table.Denied) JumpTo(JumpEqual, number, "deny");
        JumpTo(JumpEqual, table.Socket, "domain");
        JumpTo(JumpEqual, table.SocketPair, "domain");
        Emit(Return, ReturnAllow);
        labels["domain"] = program.Count;
        Emit(Load, OffsetFirstArgument); // 32 bits de poids faible de arg0 (little-endian) : le domaine est un int
        foreach (var domain in AllowedDomains(allowNetwork)) JumpTo(JumpEqual, (uint)domain, "allow");
        labels["deny"] = program.Count;
        Emit(Return, ReturnErrnoPermission);
        labels["allow"] = program.Count;
        Emit(Return, ReturnAllow);

        foreach (var (index, label) in fixups)
        {
            var distance = labels[label] - index - 1;
            if (distance is < 0 or > byte.MaxValue) throw new InvalidOperationException("Saut seccomp hors limites.");
            program[index] = program[index] with { JumpTrue = (byte)distance };
        }
        return program;
    }

    /// <summary>Représentation binaire <c>struct sock_filter[]</c> attendue par <c>prctl(PR_SET_SECCOMP)</c>.</summary>
    public static byte[] Encode(IReadOnlyList<Instruction> program)
    {
        var bytes = new byte[program.Count * 8];
        for (var i = 0; i < program.Count; i++)
        {
            var span = bytes.AsSpan(i * 8, 8);
            BinaryPrimitives.WriteUInt16LittleEndian(span, program[i].Code);
            span[2] = program[i].JumpTrue;
            span[3] = program[i].JumpFalse;
            BinaryPrimitives.WriteUInt32LittleEndian(span[4..], program[i].K);
        }
        return bytes;
    }
}
