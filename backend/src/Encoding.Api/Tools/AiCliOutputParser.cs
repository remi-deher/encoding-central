using System.Text.Json;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

/// <summary>Only confirmed terminal events produce a successful completion. Raw diagnostics never reach clients.</summary>
public static class AiCliOutputParser
{
    public static AiCompletionResult Parse(string provider, string output)
    {
        string? content = null;
        string? model = null;
        AiUsage? usage = null;
        var completed = false;
        try
        {
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = Text(root, "type");
                if (provider == "claude-cli")
                {
                    if (type == "system" && Text(root, "subtype") == "init") model = Text(root, "model");
                    if (type != "result") continue;
                    if (root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True
                        || Text(root, "subtype") != "success")
                        return Failure(IsRateLimited(root));
                    content = Text(root, "result");
                    root.TryGetProperty("usage", out var tokens);
                    usage = new(Number(tokens, "input_tokens"), Number(tokens, "output_tokens"),
                        Number(tokens, "cache_read_input_tokens"), Cost(root), model);
                    completed = true;
                }
                else
                {
                    if (type is "turn.failed" or "error") return Failure(IsRateLimited(root));
                    if (type == "item.completed" && root.TryGetProperty("item", out var item)
                        && Text(item, "type") == "agent_message") content = Text(item, "text");
                    if (type != "turn.completed") continue;
                    root.TryGetProperty("usage", out var tokens);
                    usage = new(Number(tokens, "input_tokens"), Number(tokens, "output_tokens"), Number(tokens, "cached_input_tokens"));
                    completed = true;
                }
            }
            return completed && !string.IsNullOrWhiteSpace(content)
                ? new(true, AnsiSanitizer.Sanitize(content), Usage: usage)
                : Failure();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return Failure();
        }
    }

    private static bool IsRateLimited(JsonElement root) =>
        Text(root, "subtype")?.Contains("rate_limit", StringComparison.OrdinalIgnoreCase) == true
        || AnsiSanitizer.DetectRateLimit(Text(root, "message") ?? Text(root, "result") ?? "")
        || root.TryGetProperty("error", out var error) && AnsiSanitizer.DetectRateLimit(error.GetRawText())
        || root.TryGetProperty("errors", out var errors) && AnsiSanitizer.DetectRateLimit(errors.GetRawText());

    private static AiCompletionResult Failure(bool rateLimited = false) => new(false, "",
        rateLimited ? "Quota temporairement atteint." : "Réponse structurée de l’outil IA invalide ou incomplète.", rateLimited);
    private static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static long? Number(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var field) && field.TryGetInt64(out var number) && number >= 0 ? number : null;
    private static decimal? Cost(JsonElement value) => value.TryGetProperty("total_cost_usd", out var field)
        && field.TryGetDecimal(out var cost) && cost >= 0 ? cost : null;
}
