using Encoding.Api.Tools;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class AiCliNativeCompatibilityTests
{
    [Fact]
    public async Task PinnedOfficialCliPackagesRunVersionAndHelpInsideProductionSandbox()
    {
        if (Environment.GetEnvironmentVariable("ENCODING_TEST_AI_CLI_NATIVE") != "1") return;
        Assert.True(OperatingSystem.IsLinux(), "The native CLI compatibility check requires Linux.");
        Assert.True(Encoding.Core.Runtime.ProcessSandbox.IsAvailable, "Landlock is required for the native CLI compatibility check.");

        var directory = Path.Combine(Path.GetTempPath(), "encoding-ai-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var runtimeRoot = Environment.GetEnvironmentVariable("ENCODING_TEST_AI_CLI_RUNTIME")
                ?? throw new InvalidOperationException("The npm package runtime directory is not configured.");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiTools:CredentialsDirectory"] = Path.Combine(directory, "credentials"),
                ["AiTools:RuntimeDirectory"] = runtimeRoot
            }).Build();
            var runtime = new AiCliRuntime(configuration, new EphemeralDataProtectionProvider());

            var codexVersion = await runtime.RunAsync("codex-cli", ["--version"], CancellationToken.None);
            Assert.Equal(0, codexVersion.ExitCode);
            Assert.Contains("0.163.0", codexVersion.Output);
            var codexHelp = await runtime.RunAsync("codex-cli", ["exec", "--help"], CancellationToken.None);
            Assert.Equal(0, codexHelp.ExitCode);
            Assert.Contains("--ignore-user-config", codexHelp.Output);
            Assert.Contains("--ignore-rules", codexHelp.Output);

            var claudeVersion = await runtime.RunAsync("claude-cli", ["--version"], CancellationToken.None);
            Assert.Equal(0, claudeVersion.ExitCode);
            Assert.Contains("2.1.302", claudeVersion.Output);
            var claudeHelp = await runtime.RunAsync("claude-cli", ["--help"], CancellationToken.None);
            Assert.Equal(0, claudeHelp.ExitCode);
            Assert.Contains("--system-prompt", claudeHelp.Output);

            var root = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory();
            using var descriptor = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,
                "backend/src/Encoding.Api/Flows/Catalog/Tools/claude-cli.json")));
            var versionRegex = descriptor.RootElement.GetProperty("probe").GetProperty("versionRegex").GetString()!;
            var versionMatch = Regex.Match(claudeVersion.Output.Trim(), versionRegex);
            Assert.True(versionMatch.Success);
            Assert.Equal("2.1.302", versionMatch.Groups["version"].Value);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExcessiveCliOutputStopsTheEntireProcessTree(bool stderr)
    {
        if (Environment.GetEnvironmentVariable("ENCODING_TEST_AI_CLI_NATIVE") != "1" || !OperatingSystem.IsLinux()) return;
        Assert.True(Encoding.Core.Runtime.ProcessSandbox.IsAvailable);

        using var fixture = new AiCliTestProfile();
        var executable = fixture.Configuration["AiTools:Executables:codex-cli"]!;
        File.WriteAllText(executable, "#!/bin/sh\n(sleep 30) &\necho $! > \"$HOME/child.pid\"\n" + (stderr ? "yes >&2\n" : "yes\n"));
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var runtime = new AiCliRuntime(fixture.Configuration, new EphemeralDataProtectionProvider());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RunAsync("codex-cli", ["overflow-probe"], timeout.Token));

        var childPidPath = Path.Combine(runtime.Profile("codex-cli"), "child.pid");
        Assert.True(File.Exists(childPidPath));
        var childPid = int.Parse(await File.ReadAllTextAsync(childPidPath));
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            try
            {
                using var child = Process.GetProcessById(childPid);
                if (child.HasExited) return;
            }
            catch (ArgumentException) { return; }
            await Task.Delay(25, timeout.Token);
        }
        Assert.Fail($"CLI descendant {childPid} remained alive after oversized output.");
    }

    [Fact]
    public async Task ProductionRuntimeDoesNotForwardProviderSecretsFromTheParentEnvironment()
    {
        if (Environment.GetEnvironmentVariable("ENCODING_TEST_AI_CLI_NATIVE") != "1") return;
        Assert.True(OperatingSystem.IsLinux());
        Assert.True(Encoding.Core.Runtime.ProcessSandbox.IsAvailable);

        using var fixture = new AiCliTestProfile("""
            if [ -z "$CODEX_HOME" ] || [ "$HOME" != "$CODEX_HOME" ] || [ "$XDG_CONFIG_HOME" != "$CODEX_HOME" ]; then exit 2; fi
            if [ -n "$OPENAI_API_KEY" ] || [ -n "$CLAUDE_CODE_OAUTH_TOKEN" ] || [ -n "$ENCODING_PARENT_SECRET" ]; then exit 3; fi
            printf 'environment-isolated'
            """);
        var previous = Environment.GetEnvironmentVariable("ENCODING_PARENT_SECRET");
        Environment.SetEnvironmentVariable("ENCODING_PARENT_SECRET", "synthetic-parent-secret");
        try
        {
            var production = new AiCliRuntime(fixture.Configuration, new EphemeralDataProtectionProvider());
            var result = await production.RunAsync("codex-cli", ["environment-probe"], CancellationToken.None);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("environment-isolated", result.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ENCODING_PARENT_SECRET", previous);
        }
    }
}
