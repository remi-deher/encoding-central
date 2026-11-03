using System.Runtime.InteropServices;
using System.Text.Json;
using Encoding.Contracts.Plugins;
using Encoding.Core.Runtime;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Construction des règles du bac à sable, sans noyau Landlock : rapide et déterministe.</summary>
public sealed class ProcessSandboxRulesTests
{
    [Fact]
    public void PolicyDefaultsToStrictAndRoundTrips()
    {
        Assert.Equal(SandboxPolicy.Strict, SandboxPolicy.Parse(null));
        Assert.Equal(SandboxPolicy.Strict, SandboxPolicy.Parse(" "));
        foreach (var policy in new[] { SandboxPolicy.Strict, SandboxPolicy.Network, SandboxPolicy.AiCli })
            Assert.Equal(policy, SandboxPolicy.Parse(policy.Serialize()));
        Assert.Equal("network,host-proc", SandboxPolicy.AiCli.Serialize());
        Assert.Equal(SandboxPolicy.Network, SandboxPolicy.ForPlugin(true));
        Assert.Equal(SandboxPolicy.Strict, SandboxPolicy.ForPlugin(false));
    }

    [Fact]
    public void UnknownPolicyTokenIsRefusedInsteadOfIgnored() => Assert.Throws<FormatException>(() => SandboxPolicy.Parse("network,everything"));

    [Theory]
    [InlineData(1, 0b1_1111_1111_1111UL)]
    [InlineData(2, 0b1_1111_1111_1111UL | (1UL << 13))]
    [InlineData(3, 0b1_1111_1111_1111UL | (1UL << 13) | (1UL << 14))]
    [InlineData(4, 0b1_1111_1111_1111UL | (1UL << 13) | (1UL << 14))]
    public void FilesystemAccessGrowsWithTheAbi(int abi, ulong expected) => Assert.Equal(expected, SandboxRules.FilesystemAccess(abi));

    [Theory]
    [InlineData(1, false, 0UL)]
    [InlineData(3, false, 0UL)]
    [InlineData(4, false, 3UL)]
    [InlineData(6, false, 3UL)]
    [InlineData(4, true, 0UL)]
    [InlineData(6, true, 0UL)]
    public void TcpIsHandledOnlyWhenTheKernelCanAndTheNetworkIsNotGranted(int abi, bool network, ulong expected)
        => Assert.Equal(expected, SandboxRules.NetworkAccess(abi, SandboxPolicy.ForPlugin(network)));

    [Fact]
    public void RulesetSizeCoversTheNetworkFieldOnlyFromAbi4()
    {
        Assert.Equal(8, SandboxRules.RulesetSize(3));
        Assert.Equal(16, SandboxRules.RulesetSize(4));
    }

    [Fact]
    public void PluginsSeeOnlyTheirOwnProcessDirectoryUnderProc()
    {
        foreach (var policy in new[] { SandboxPolicy.Strict, SandboxPolicy.Network })
        {
            var paths = SandboxRules.ReadOnlyPaths(policy);
            Assert.Contains("/proc/self", paths);
            Assert.DoesNotContain("/proc", paths);
            Assert.DoesNotContain("/sys", paths);
            Assert.All(paths.Where(path => path.StartsWith("/proc/", StringComparison.Ordinal)), path => Assert.DoesNotMatch(@"^/proc/(\d+|sys|\*)", path));
            Assert.All(paths.Where(path => path.StartsWith("/sys/", StringComparison.Ordinal)), path => Assert.Contains(path, new[] { "/sys/devices/system/cpu", "/sys/fs/cgroup" }));
        }
    }

    [Fact]
    public void AiCliKeepsTheWideProcAndSysAccessItHadBefore()
    {
        var paths = SandboxRules.ReadOnlyPaths(SandboxPolicy.AiCli);
        Assert.Contains("/proc", paths);
        Assert.Contains("/sys", paths);
        Assert.Contains("/usr", paths);
    }

    [Theory]
    [InlineData(Architecture.X64, false)]
    [InlineData(Architecture.X64, true)]
    [InlineData(Architecture.Arm64, false)]
    [InlineData(Architecture.Arm64, true)]
    public void SeccompProgramIsWellFormed(Architecture architecture, bool network)
    {
        var program = SeccompFilter.Build(architecture, network);
        Assert.Equal(0x20, program[0].Code);
        Assert.Equal(4u, program[0].K);
        Assert.Equal(SeccompFilter.ReturnKillProcess, program[2].K);
        Assert.Equal(SeccompFilter.ReturnAllow, program[^1].K);
        for (var index = 0; index < program.Count; index++)
        {
            if (program[index].Code is not (0x15 or 0x35)) continue;
            Assert.True(index + 1 + program[index].JumpTrue < program.Count, "Jumps stay inside the program.");
            Assert.Equal(0, program[index].JumpFalse);
        }
        Assert.Equal(program.Count * 8, SeccompFilter.Encode(program).Length);
    }

    [Theory]
    [InlineData(Architecture.X64, 41u, 53u, 101u, 310u, 425u)]
    [InlineData(Architecture.Arm64, 198u, 199u, 117u, 270u, 425u)]
    public void SeccompDeniesProcessMemoryAccessAndIoUringAlways(Architecture architecture, uint socket, uint socketPair, uint ptrace, uint processVm, uint ioUring)
    {
        foreach (var network in new[] { false, true })
        {
            var numbers = SeccompFilter.Build(architecture, network).Where(item => item.Code == 0x15).Select(item => item.K).ToHashSet();
            Assert.Contains(ptrace, numbers);
            Assert.Contains(processVm, numbers);
            Assert.Contains(ioUring, numbers);
            // Les sockets ne sont filtrées que sans capacité réseau ; avec elle, l'appel système n'est pas examiné.
            Assert.Equal(!network, numbers.Contains(socket) && numbers.Contains(socketPair));
        }
    }

    [Fact]
    public void SeccompRestrictsSocketsToUnixDomainWithoutNetwork()
    {
        var strict = SeccompFilter.Build(Architecture.X64, false);
        Assert.Contains(strict, item => item.Code == 0x15 && item.K == SeccompFilter.AfUnix);
        Assert.DoesNotContain(SeccompFilter.Build(Architecture.X64, true), item => item.Code == 0x15 && item.K == SeccompFilter.AfUnix);
    }

    [Fact]
    public void SeccompRefusesTheX32AbiAndUnknownArchitectures()
    {
        Assert.Contains(SeccompFilter.Build(Architecture.X64, true), item => item.Code == 0x35 && item.K == 0x40000000);
        Assert.DoesNotContain(SeccompFilter.Build(Architecture.Arm64, true), item => item.Code == 0x35);
        Assert.False(SeccompFilter.IsSupported(Architecture.Arm));
        Assert.Throws<PlatformNotSupportedException>(() => SeccompFilter.Build(Architecture.Arm, false));
    }

    [Fact]
    public void NetworkIsGrantedOnlyByTheDeclaredManifestCapability()
    {
        var json = (string? capabilities) => $$"""{"contractVersion":1,"id":"p","version":"1.0.0","name":"P","supportedHostContractVersions":[1],"components":[]{{capabilities}}}""";
        var read = (string text) => JsonSerializer.Deserialize<PluginManifest>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.False(PluginCapabilities.AllowsNetwork(read(json(null))));
        Assert.False(PluginCapabilities.AllowsNetwork(read(json(",\"capabilities\":[]"))));
        Assert.True(PluginCapabilities.AllowsNetwork(read(json(",\"capabilities\":[\"network\"]"))));
        Assert.DoesNotContain("filesystem", PluginCapabilities.Ids);
    }
}
