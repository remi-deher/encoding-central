using Encoding.Contracts.Tools;
using System.Text.Json;
using System.Diagnostics;
using System.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Encoding.Api.Tools;

public sealed class CliAiTextClient(string toolId, AiCliRuntime runtime, int timeoutSeconds = 120,
    ILogger<CliAiTextClient>? logger = null) : IAiTextClient
{
    public async Task<AiCompletionResult> GenerateTextAsync(AiPrompt prompt, CancellationToken ct = default)
    {
        var attemptId = prompt.RequestId?.ToString("N") ?? Guid.NewGuid().ToString("N");
        var timer = Stopwatch.StartNew();
        if (runtime.Find(toolId) is null)
        {
            await LogDiagnosticAsync("cli_missing", attemptId, timer.ElapsedMilliseconds);
            return new(false, "", "Outil CLI non installé.");
        }
        if (!runtime.CanRun)
        {
            await LogDiagnosticAsync("sandbox_unavailable", attemptId, timer.ElapsedMilliseconds);
            return new(false, "", "Landlock est requis pour exécuter les outils IA sur ce nœud.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var text = prompt.UserPrompt;
            string[] args = toolId == "claude-cli"
                ? ["-p", "--system-prompt", prompt.SystemPrompt, "--output-format", "stream-json", "--verbose", "--no-session-persistence", "--tools", "",
                    "--strict-mcp-config", "--setting-sources", "", "--disable-slash-commands"]
                : ["exec", "--json", "--ephemeral", "--ignore-user-config", "--ignore-rules", "--disable", "shell_tool", "--disable", "unified_exec",
                    "--skip-git-repo-check", "--sandbox", "read-only", "--color", "never", "-c", "approval_policy=\"never\"", "-c",
                    $"developer_instructions={JsonSerializer.Serialize(prompt.SystemPrompt)}", "-"];
            if (prompt.Model is { Length: > 0 }) args = [.. args[..^ (toolId == "codex-cli" ? 1 : 0)], "--model", prompt.Model, .. (toolId == "codex-cli" ? new[] { "-" } : Array.Empty<string>())];
            if (prompt.ReasoningEffort is { Length: > 0 } effort)
            {
                // Model-specific capabilities are validated against the local observed catalogue before execution.
                if (effort.Length > 64 || effort.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                    return new(false, "", "Niveau de raisonnement invalide.");
                if (toolId == "claude-cli") args = [.. args, "--effort", effort];
                else args = [.. args[..^1], "-c", $"model_reasoning_effort=\"{effort}\"", "-"];
            }
            var result = await runtime.RunAsync(toolId, args, timeout.Token, input: text);
            var completion = AiCliOutputParser.Parse(toolId, result.Output);
            if (result.ExitCode != 0)
            {
                var category = completion.IsRateLimited || AnsiSanitizer.DetectRateLimit(result.Error)
                    ? "quota"
                    : IsIncompatibleVersion(result.Error) ? "version_incompatible"
                    : IsNetworkFailure(result.Error) ? "network_tls"
                    : IsAuthenticationFailure(result.Error) ? "auth_missing"
                    : "provider_refusal";
                await LogDiagnosticAsync(category, attemptId, timer.ElapsedMilliseconds);
                return new(false, "", "L’outil IA a refusé la requête. Vérifiez la connexion et les quotas.",
                    completion.IsRateLimited || AnsiSanitizer.DetectRateLimit(result.Error));
            }
            if (!completion.Success)
            {
                await LogDiagnosticAsync("invalid_output", attemptId, timer.ElapsedMilliseconds);
                return completion;
            }
            logger?.LogDebug("AI CLI attempt provider={Provider}, category=success, version={Version}, durationMs={DurationMs}, attemptId={AttemptId}",
                toolId, await runtime.VersionForDiagnosticsAsync(toolId), timer.ElapsedMilliseconds, attemptId);
            return completion;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await LogDiagnosticAsync("timeout", attemptId, timer.ElapsedMilliseconds);
            return new(false, "", "Délai maximal de la requête IA dépassé.");
        }
        catch (OperationCanceledException)
        {
            await LogDiagnosticAsync("cancelled", attemptId, timer.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            var category = ex is Win32Exception or FileNotFoundException ? "cli_missing"
                : ex is InvalidOperationException && ex.Message.Contains("Sortie CLI trop volumineuse", StringComparison.Ordinal)
                    ? "invalid_output" : "runtime_failure";
            await LogDiagnosticAsync(category, attemptId, timer.ElapsedMilliseconds);
            return new(false, "", category == "invalid_output"
                ? "La sortie de l’outil IA dépasse la limite autorisée."
                : "L’exécution de l’outil IA a échoué.");
        }
    }

    private async Task LogDiagnosticAsync(string category, string attemptId, long durationMs)
    {
        if (logger is null) return;
        logger.LogWarning("AI CLI attempt provider={Provider}, category={Category}, version={Version}, durationMs={DurationMs}, attemptId={AttemptId}",
            toolId, category, await runtime.VersionForDiagnosticsAsync(toolId), durationMs, attemptId);
    }

    private static bool IsNetworkFailure(string error) =>
        new[] { "ECONNRESET", "ENOTFOUND", "ETIMEDOUT", "TLS", "certificate", "network", "fetch failed", "SSL handshake", "CERT_" }
            .Any(marker => error.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsIncompatibleVersion(string error) =>
        new[] { "unknown option", "unrecognized option", "unsupported option", "invalid option" }
            .Any(marker => error.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsAuthenticationFailure(string error) =>
        new[] { "unauthorized", "not logged in", "authentication", "invalid token", "login required" }
            .Any(marker => error.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
