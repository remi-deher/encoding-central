using System.Text.Json;
using Encoding.Api.Tools;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class AiQuotaTests
{
    [Fact]
    public void CodexWindowsKeepSeparateLimitsAndConvertUnixResets()
    {
        using var document = JsonDocument.Parse("""
            {"rateLimits":{"primary":{"usedPercent":99}},"rateLimitsByLimitId":{
              "codex":{"primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":1800000000},"secondary":{"usedPercent":80,"windowDurationMins":10080}},
              "review":{"limitName":"Reviews","primary":{"usedPercent":100,"windowDurationMins":300}}
            }}
            """);
        var windows = AiQuotaProtocol.ParseCodex(document.RootElement);
        Assert.Equal(3, windows.Count);
        Assert.Equal(75, windows[0].RemainingPercent);
        Assert.Contains("5 heures", windows[0].Name);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1800000000), windows[0].ResetsAt);
        Assert.Equal(20, windows[1].RemainingPercent);
        Assert.Null(windows[1].ResetsAt);
        Assert.Equal(0, windows[2].RemainingPercent);
        Assert.Contains("Reviews", windows[2].Name);
    }

    [Fact]
    public void ClaudeWindowsUsePercentUnitsAndHandleMissingDataAndModelLimits()
    {
        using var document = JsonDocument.Parse("""
            {"rate_limits_available":true,"subscription_type":"max","rate_limits":{
              "five_hour":{"utilization":23.5,"resets_at":"2030-01-01T00:00:00Z"},
              "seven_day":{"utilization":null,"resets_at":null},
              "model_scoped":[{"display_name":"Example","utilization":90,"resets_at":null}],
              "extra_usage":{"is_enabled":true,"utilization":50}
            }}
            """);
        var windows = AiQuotaProtocol.ParseClaude(document.RootElement);
        Assert.Equal(3, windows.Count);
        Assert.Equal(76.5, windows[0].RemainingPercent);
        Assert.Null(windows[1].RemainingPercent);
        Assert.Equal(10, windows[2].RemainingPercent);
        using var unavailable = JsonDocument.Parse("""{"rate_limits_available":false,"rate_limits":null}""");
        Assert.Empty(AiQuotaProtocol.ParseClaude(unavailable.RootElement));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void InvalidPercentagesCannotBecomeQuotaGauges(int percent)
    {
        var document = JsonSerializer.SerializeToElement(new { rateLimits = new { primary = new { usedPercent = percent } } });
        Assert.Throws<FormatException>(() => AiQuotaProtocol.ParseCodex(document));
    }

    [Fact]
    public async Task CodexQuotaProtocolNeverStartsAThreadAndCachesConcurrentReads()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new AiCliTestProfile("""
            [ "$1" = "app-server" ] || exit 1
            read -r init
            case "$init" in *'"method":"initialize"'*) ;; *) exit 2 ;; esac
            printf '%s\n' '{"id":1,"result":{}}'
            read -r initialized
            case "$initialized" in *'"method":"initialized"'*) ;; *) exit 3 ;; esac
            read -r quota
            case "$quota" in *'"method":"account/rateLimits/read"'*) ;; *) exit 4 ;; esac
            printf 'probe\n' >> "$HOME/probes"
            cat "$HOME/reply"
            sleep 30
            """);
        var profile = fixture.Runtime.Profile("codex-cli");
        await File.WriteAllTextAsync(Path.Combine(profile, "reply"), """
            {"id":2,"result":{"rateLimits":{"primary":{"usedPercent":25,"resetsAt":1893456000,"windowDurationMins":300}}}}

            """);
        var clock = new Clock();
        var service = new AiQuotaService(fixture.Runtime, clock);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => service.ReadAsync("codex-cli", CancellationToken.None)));
        Assert.All(results, snapshot => Assert.Equal("available", snapshot.State));
        Assert.Equal(75, results[0].Windows[0].RemainingPercent);
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(profile, "probes")));
        // Failure after the cache expires keeps a dated observation, never a synthetic full quota.
        clock.Now = clock.Now.AddMinutes(2);
        await File.WriteAllTextAsync(Path.Combine(profile, "reply"), "{\"id\":2,\"error\":{\"message\":\"private-token\"}}\n");
        var stale = await service.ReadAsync("codex-cli", CancellationToken.None);
        Assert.Equal("stale", stale.State);
        Assert.Equal(results[0].ObservedAt, stale.ObservedAt);
        Assert.Equal(75, stale.Windows[0].RemainingPercent);
        Assert.DoesNotContain("private-token", JsonSerializer.Serialize(stale));
        service.Invalidate("codex-cli");
        var cleared = await service.ReadAsync("codex-cli", CancellationToken.None);
        Assert.Equal("unavailable", cleared.State);
        Assert.Empty(cleared.Windows);
    }

    [Fact]
    public async Task ClaudeUsesOnlyInitializeAndExperimentalGetUsageWithBehaviorScanDisabled()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new AiCliTestProfile("""
            case " $* " in *' --no-session-persistence '*) ;; *) exit 1 ;; esac
            case " $* " in *' --input-format stream-json '*) ;; *) exit 2 ;; esac
            read -r init
            case "$init" in *'"subtype":"initialize"'*) ;; *) exit 3 ;; esac
            printf '%s\n' '{"type":"control_response","response":{"subtype":"success","request_id":"quota-init","response":{}}}'
            read -r quota
            case "$quota" in *'"subtype":"get_usage","skip_behaviors":true'*) ;; *) exit 4 ;; esac
            printf '%s\n' '{"type":"control_response","response":{"subtype":"success","request_id":"quota-read","response":{"rate_limits_available":true,"rate_limits":{"five_hour":{"utilization":90,"resets_at":"2030-01-01T00:00:00Z"}}}}}'
            sleep 30
            """);
        var clock = new Clock();
        var service = new AiQuotaService(fixture.Runtime, clock);
        var result = await service.ReadAsync("claude-cli", CancellationToken.None);
        Assert.Equal("available", result.State);
        Assert.True(result.Experimental);
        Assert.Equal(10, Assert.Single(result.Windows).RemainingPercent);
        clock.Now = DateTimeOffset.Parse("2030-01-01T00:00:01Z");
        var expired = await service.ReadAsync("claude-cli", CancellationToken.None);
        Assert.Equal("stale", expired.State);
        Assert.Equal(10, Assert.Single(expired.Windows).RemainingPercent);
    }

    [Fact]
    public async Task UnsupportedProtocolIsUnavailableAndCancellationIsPropagated()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new AiCliTestProfile("""
            read -r init
            printf '%s\n' '{"type":"control_response","response":{"subtype":"error","request_id":"quota-init","error":"private-token"}}'
            sleep 30
            """);
        var service = new AiQuotaService(fixture.Runtime);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await service.ReadAsync("claude-cli", watchdog.Token);
        Assert.Equal("unavailable", result.State);
        Assert.DoesNotContain("private-token", JsonSerializer.Serialize(result));
        using var silent = new AiCliTestProfile("sleep 30");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AiQuotaService(silent.Runtime).ReadAsync("codex-cli", cancellation.Token));
    }

    [Fact]
    public async Task ConnectionChangeDiscardsAnInflightQuotaAndDoesNotCacheIt()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new AiCliTestProfile("""
            read -r init
            printf '%s\n' '{"id":1,"result":{}}'
            read -r initialized
            read -r quota
            printf 'probe\n' >> "$HOME/probes"
            touch "$HOME/ready"
            while [ ! -f "$HOME/release" ]; do sleep 0.01; done
            printf '%s\n' '{"id":2,"result":{"rateLimits":{"primary":{"usedPercent":90}}}}'
            sleep 30
            """);
        var directory = fixture.Runtime.Profile("codex-cli");
        var service = new AiQuotaService(fixture.Runtime);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = service.ReadAsync("codex-cli", timeout.Token);
        while (!File.Exists(Path.Combine(directory, "ready"))) await Task.Delay(10, timeout.Token);
        service.Invalidate("codex-cli");
        await File.WriteAllTextAsync(Path.Combine(directory, "release"), "ready", timeout.Token);
        Assert.Equal("unavailable", (await first).State);
        var second = await service.ReadAsync("codex-cli", timeout.Token);
        Assert.Equal("available", second.State);
        Assert.Equal(2, (await File.ReadAllLinesAsync(Path.Combine(directory, "probes"))).Length);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-19T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
