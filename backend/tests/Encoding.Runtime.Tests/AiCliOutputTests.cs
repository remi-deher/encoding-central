using Encoding.Api.Tools;
using Encoding.Contracts.Tools;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class AiCliOutputTests
{
    [Fact]
    public void CodexExtractsFinalAnswerAndUsageWithoutReturningToolEvents()
    {
        var result = AiCliOutputParser.Parse("codex-cli", """
            {"type":"thread.started","thread_id":"native-session"}
            {"type":"item.completed","item":{"type":"command_execution","aggregated_output":"private diagnostics"}}
            {"type":"item.completed","item":{"type":"agent_message","text":"Traduction : rate limit signifie limite de débit."}}
            {"type":"turn.completed","usage":{"input_tokens":120,"cached_input_tokens":40,"output_tokens":15}}
            """);
        Assert.True(result.Success);
        Assert.StartsWith("Traduction", result.Content);
        Assert.DoesNotContain("private diagnostics", result.Content);
        Assert.False(result.IsRateLimited);
        Assert.Equal(new AiUsage(120, 15, 40), result.Usage);
        Assert.Null(result.SessionId);
    }

    [Fact]
    public void ClaudeExtractsConfirmedResultAndObservedModelAndCost()
    {
        var result = AiCliOutputParser.Parse("claude-cli", """
            {"type":"system","subtype":"init","model":"fixture-model","session_id":"native-id"}
            {"type":"assistant","message":{"content":[{"type":"text","text":"partial"}]}}
            {"type":"result","subtype":"success","is_error":false,"result":"Bonjour","total_cost_usd":0.012,"usage":{"input_tokens":100,"output_tokens":12,"cache_read_input_tokens":20}}
            """);
        Assert.True(result.Success);
        Assert.Equal("Bonjour", result.Content);
        Assert.Equal(new AiUsage(100, 12, 20, 0.012m, "fixture-model"), result.Usage);
        Assert.Null(result.SessionId);
    }

    [Theory]
    [InlineData("codex-cli", "unrecognized plain text")]
    [InlineData("codex-cli", "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"partial\"}}")]
    [InlineData("codex-cli", "{\"type\":\"turn.failed\",\"error\":{\"message\":\"private diagnostic\"}}")]
    [InlineData("claude-cli", "{\"type\":\"result\",\"subtype\":\"error_max_turns\",\"result\":\"partial\"}")]
    public void MissingTerminalConfirmationNeverProducesSuccess(string provider, string output)
    {
        var result = AiCliOutputParser.Parse(provider, output);
        Assert.False(result.Success);
        Assert.Empty(result.Content);
        Assert.DoesNotContain("private diagnostic", result.ErrorMessage);
    }

    [Theory]
    [InlineData("codex-cli", "{\"type\":\"error\",\"message\":\"HTTP 429 Too Many Requests\"}")]
    [InlineData("claude-cli", "{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true,\"errors\":[\"rate limit reached\"]}")]
    public void StructuredErrorsPreserveQuotaRoutingWithoutReturningDiagnostics(string provider, string output)
    {
        var result = AiCliOutputParser.Parse(provider, output);
        Assert.False(result.Success);
        Assert.True(result.IsRateLimited);
        Assert.Empty(result.Content);
    }

    [Fact]
    public void UnknownUsageRemainsUnavailable()
    {
        var result = AiCliOutputParser.Parse("claude-cli", """
            {"type":"result","subtype":"success","result":"Bonjour"}
            """);
        Assert.True(result.Success);
        Assert.Null(result.Usage!.InputTokens);
        Assert.Null(result.Usage.EstimatedCostUsd);
    }

    [Fact]
    public async Task ClaudeRunsWithoutNativeHistoryOrInheritedTools()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new AiCliTestProfile("""
            case " $* " in *" --no-session-persistence "*) ;; *) exit 2 ;; esac
            case " $* " in *" --output-format stream-json "*) ;; *) exit 3 ;; esac
            case " $* " in *" --strict-mcp-config "*) ;; *) exit 4 ;; esac
            case " $* " in *" --model fixture-model "*) ;; *) exit 5 ;; esac
            cat >/dev/null
            printf '%s\n' '{"type":"result","subtype":"success","result":"Bonjour"}'
            """);
        var result = await new CliAiTextClient("claude-cli", fixture.Runtime)
            .GenerateTextAsync(new("Consignes", "Demande", Model: "fixture-model"));
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("Bonjour", result.Content);
    }
}
