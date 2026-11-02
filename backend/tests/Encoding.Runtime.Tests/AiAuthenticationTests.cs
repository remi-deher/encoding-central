using Encoding.Api.Tools;
using Encoding.Contracts.Tools;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class AiAuthenticationTests
{
    [Fact]
    public async Task ProductionRuntimeFailsClosedWithoutLandlockOrDeniesFilesOutsideProfile()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new AiCliTestProfile("printf 'not-isolated'");
        var production = new AiCliRuntime(fixture.Configuration, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        if (!Encoding.Core.Runtime.ProcessSandbox.IsAvailable)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => production.RunAsync("codex-cli", ["exec"], CancellationToken.None));
            return;
        }
        var sentinel = Path.Combine(fixture.DirectoryPath, "sentinel");
        File.WriteAllText(sentinel, "private-sentinel");
        var script = fixture.Configuration["AiTools:Executables:codex-cli"]!;
        File.WriteAllText(script, "#!/bin/sh\nif [ ! -r /proc/self/stat ]; then exit 2; fi\nif cat '" + sentinel + "' 2>/dev/null; then exit 1; else printf 'isolated'; fi");
        var result = await production.RunAsync("codex-cli", ["exec"], CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("isolated", result.Output);
    }

    [Fact]
    public async Task ProductionSandboxAllowsReadingSystemOpenSslConfiguration()
    {
        if (!OperatingSystem.IsLinux() || !Encoding.Core.Runtime.ProcessSandbox.IsAvailable) return;
        var config = new[] { "/usr/lib/ssl/openssl.cnf", "/etc/ssl/openssl.cnf" }.FirstOrDefault(File.Exists);
        if (config is null) return;
        using var fixture = new AiCliTestProfile($"if [ -r '{config}' ]; then printf readable; else exit 1; fi");
        var production = new AiCliRuntime(fixture.Configuration, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        var result = await production.RunAsync("codex-cli", ["openssl-config-probe"], CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("readable", result.Output);
    }

    [Fact]
    public async Task CodexUsesProviderCodeAndRealProfileStatus()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new AiCliTestProfile("""
            if [ "$1 $2" = "login --device-auth" ]; then
              printf '\033[32mOpen https://auth.openai.com/codex/device\033[0m\nABCD-23456\n'
              sleep 0.2
              touch "$CODEX_HOME/auth.valid"
              exit 0
            fi
            if [ "$1 $2" = "login status" ]; then test -f "$CODEX_HOME/auth.valid"; exit $?; fi
            if [ "$1" = "logout" ]; then rm -f "$CODEX_HOME/auth.valid"; exit 0; fi
            exit 1
            """);
        using var service = new DeviceCodeAuthService(fixture.Runtime);
        var prompt = await service.StartDeviceAuthAsync("codex-cli", CancellationToken.None);
        Assert.Equal("ABCD-23456", prompt.Prompt.UserCode);
        Assert.Equal("https://auth.openai.com/codex/device", prompt.Prompt.VerificationUri);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await service.GetStatusAsync("codex-cli", deadline.Token)).AttemptState == "pending") await Task.Delay(20, deadline.Token);
        Assert.Equal("authorized", (await service.GetStatusAsync("codex-cli")).State);
        Assert.Equal("authorized", (await new DeviceCodeAuthService(fixture.Runtime).GetStatusAsync("codex-cli")).State);
        await service.RevokeAuthAsync("codex-cli", CancellationToken.None);
        Assert.Equal("unconfigured", (await service.GetStatusAsync("codex-cli")).State);
    }

    [Fact]
    public async Task ClaudeLoginReturnsOfficialLinkAndAcceptsBrowserReturnCode()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new AiCliTestProfile("""
            if [ "$1 $2" = "auth login" ]; then
              printf 'If the browser did not open, visit: https://claude.com/oauth/authorize?flow=example\nPaste code here if prompted >'
              IFS= read -r code
              if [ "$code" = "sample-return-code" ]; then touch "$CLAUDE_CONFIG_DIR/auth.valid"; exit 0; fi
              exit 2
            fi
            if [ "$1 $2" = "auth status" ]; then
              if test -f "$CLAUDE_CONFIG_DIR/auth.valid"; then printf '{"loggedIn":true}'; exit 0; fi
              printf '{"loggedIn":false}'; exit 1
            fi
            exit 1
            """);
        var logger = new AiAuthenticationDiagnosticsTests.CaptureLogger();
        using var service = new DeviceCodeAuthService(fixture.Runtime, logger);

        var response = await service.StartDeviceAuthAsync("claude-cli", CancellationToken.None);

        Assert.Null(response.Prompt.UserCode);
        Assert.Equal("https://claude.com/oauth/authorize?flow=example", response.Prompt.VerificationUri);
        Assert.True(response.Prompt.RequiresInput);
        Assert.Equal("pending", (await service.GetStatusAsync("claude-cli")).AttemptState);
        await service.SubmitAuthInputAsync("claude-cli", "sample-return-code", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SubmitAuthInputAsync("claude-cli", "another-code", CancellationToken.None));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await service.GetStatusAsync("claude-cli", deadline.Token)).AttemptState == "pending") await Task.Delay(20, deadline.Token);
        Assert.Equal("authorized", (await service.GetStatusAsync("claude-cli")).State);
        Assert.DoesNotContain("sample-return-code", string.Join("\n", logger.Messages));
    }

    [Fact]
    public async Task ClaudeTokenIsProtectedAndNeverReturnedFromStatus()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new AiCliTestProfile("""
            if [ "$1 $2" = "auth status" ]; then
              if [ "$CLAUDE_CODE_OAUTH_TOKEN" = "test-private-token" ] && [ "$CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC" = "1" ]; then printf '{"loggedIn":true}'; exit 0; fi
              printf '{"loggedIn":false}'; exit 1
            fi
            exit 0
            """);
        using var service = new DeviceCodeAuthService(fixture.Runtime);
        Assert.Equal("unconfigured", (await service.GetStatusAsync("claude-cli")).State);
        await service.SaveClaudeTokenAsync("test-private-token", CancellationToken.None);
        var saved = File.ReadAllText(Path.Combine(fixture.Runtime.Profile("claude-cli"), "oauth-token.protected"));
        Assert.DoesNotContain("test-private-token", saved);
        var status = await service.GetStatusAsync("claude-cli");
        Assert.Equal("authorized", status.State);
        Assert.Null(status.AccountName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveClaudeTokenAsync("bad-token", CancellationToken.None));
        Assert.Equal("authorized", (await service.GetStatusAsync("claude-cli")).State);
        await service.RevokeAuthAsync("claude-cli", CancellationToken.None);
        Assert.False(fixture.Runtime.HasClaudeToken);
        Assert.Equal("unconfigured", (await service.GetStatusAsync("claude-cli")).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveClaudeTokenAsync("bad-token", CancellationToken.None));
        Assert.False(fixture.Runtime.HasClaudeToken);
    }

    [Fact]
    public async Task InferenceUsesSameIsolatedProfileAndOfficialArguments()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new AiCliTestProfile("""
            if [ "$1" != "exec" ]; then exit 1; fi
            if [ -z "$CODEX_HOME" ] || [ "$HOME" != "$CODEX_HOME" ] || [ "$XDG_CONFIG_HOME" != "$CODEX_HOME" ] || [ -n "$OPENAI_API_KEY" ]; then exit 2; fi
            cat >/dev/null
            case " $* " in *" --ephemeral "*) ;; *) exit 3 ;; esac
            case " $* " in *" --json "*) ;; *) exit 4 ;; esac
            printf '%s\n' '{"type":"item.completed","item":{"type":"agent_message","text":"Real CLI test result"}}' '{"type":"turn.completed","usage":{"input_tokens":10,"output_tokens":4}}'
            """);
        var result = await new CliAiTextClient("codex-cli", fixture.Runtime).GenerateTextAsync(new AiPrompt("Instructions", "Request"));
        Assert.True(result.Success);
        Assert.Equal("Real CLI test result", result.Content);
    }
}

public sealed class AiAuthenticationDiagnosticsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task ExitWithoutPromptIsNeverTreatedAsConnectedAndDoesNotLeakOutput(int exitCode)
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new AiCliTestProfile($"printf 'private-token-do-not-log' >&2; exit {exitCode}");
        var logger = new CaptureLogger();
        using var service = new DeviceCodeAuthService(fixture.Runtime, logger);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartDeviceAuthAsync("codex-cli", CancellationToken.None));
        Assert.Contains($"code {exitCode}", error.Message);
        Assert.Equal("failed", (await service.GetStatusAsync("codex-cli")).AttemptState);
        Assert.Contains(logger.Messages, item => item.Contains($"exit={exitCode}"));
        Assert.DoesNotContain("private-token", string.Join("\n", logger.Messages));
        Assert.DoesNotContain("private-token", error.Message);
    }

    [Fact]
    public async Task PromptCanArriveInChunksWithAnsiEscapesAndIsNotLogged()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new AiCliTestProfile("""
            printf '\033[32mhttps://auth.openai.com/codex/'
            sleep 0.1
            printf 'device\033[0m\nABCD-'
            sleep 0.1
            printf '23456\n'
            sleep 0.2
            """);
        var logger = new CaptureLogger();
        using var service = new DeviceCodeAuthService(fixture.Runtime, logger);
        var response = await service.StartDeviceAuthAsync("codex-cli", CancellationToken.None);
        Assert.Equal("ABCD-23456", response.Prompt.UserCode);
        Assert.Contains(logger.Messages, item => item.Contains("prompt recognized"));
        Assert.DoesNotContain("ABCD-23456", string.Join("\n", logger.Messages));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutDistinguishesSilentCliFromUnrecognizedOutput(bool hasOutput)
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new AiCliTestProfile(hasOutput ? "printf 'private-unrecognized-output'; sleep 20" : "sleep 20");
        var logger = new CaptureLogger();
        var clock = new ManualClock();
        using var service = new DeviceCodeAuthService(fixture.Runtime, logger, clock);
        var attempt = service.StartDeviceAuthAsync("codex-cli", CancellationToken.None);
        var timer = await clock.Created.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (hasOutput) await logger.FirstOutput.Task.WaitAsync(TimeSpan.FromSeconds(5));
        timer.Fire();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => attempt);
        Assert.Contains(hasOutput ? "a produit une sortie" : "aucune sortie", error.Message);
        Assert.Equal("failed", (await service.GetStatusAsync("codex-cli")).AttemptState);
        Assert.DoesNotContain("private-unrecognized-output", string.Join("\n", logger.Messages));
    }

    private sealed class ManualClock : TimeProvider
    {
        public readonly TaskCompletionSource<ManualTimer> Created = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            Created.TrySetResult(timer);
            return timer;
        }
    }
    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class CaptureLogger : Microsoft.Extensions.Logging.ILogger<DeviceCodeAuthService>
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Messages = new();
        public readonly TaskCompletionSource FirstOutput = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Messages.Enqueue(message);
            if (message.Contains("first output")) FirstOutput.TrySetResult();
        }
    }
}
