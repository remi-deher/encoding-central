using System.Globalization;
using System.Text;
using System.Text.Json;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

/// <summary>Read-only account protocols. No user message, model turn, login or credit purchase is sent.</summary>
public static class AiQuotaProtocol
{
    public static async Task<JsonElement> ReadCodexAsync(StreamReader output, StreamWriter input, CancellationToken ct)
    {
        var reader = new Frames(output);
        await SendAsync(input, new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "encoding_quota", version = "1.0.0" } } }, ct);
        await CodexResponseAsync(reader, 1, ct);
        await SendAsync(input, new { method = "initialized" }, ct);
        await SendAsync(input, new { id = 2, method = "account/rateLimits/read" }, ct);
        return await CodexResponseAsync(reader, 2, ct);
    }

    public static async Task<JsonElement> ReadClaudeAsync(StreamReader output, StreamWriter input, CancellationToken ct)
    {
        var reader = new Frames(output);
        await SendAsync(input, new { type = "control_request", request_id = "quota-init", request = new { subtype = "initialize" } }, ct);
        await ClaudeResponseAsync(reader, "quota-init", ct);
        await SendAsync(input, new { type = "control_request", request_id = "quota-read", request = new { subtype = "get_usage", skip_behaviors = true } }, ct);
        return await ClaudeResponseAsync(reader, "quota-read", ct);
    }

    internal static async Task<JsonElement> CodexResponseAsync(Frames reader, int id, CancellationToken ct)
    {
        while (true)
        {
            var frame = await reader.ReadAsync(ct);
            if (frame.TryGetProperty("method", out _) && frame.TryGetProperty("id", out _))
                throw new InvalidOperationException("Interaction inattendue pendant la lecture du quota.");
            if (!frame.TryGetProperty("id", out var actual) || actual.ValueKind != JsonValueKind.Number || actual.GetInt32() != id) continue;
            if (frame.TryGetProperty("error", out _) || !frame.TryGetProperty("result", out var result))
                throw new InvalidOperationException("Lecture du quota non prise en charge.");
            return result.Clone();
        }
    }

    internal static async Task<JsonElement> ClaudeResponseAsync(Frames reader, string id, CancellationToken ct)
    {
        while (true)
        {
            var frame = await reader.ReadAsync(ct);
            if (Text(frame, "type") == "control_request") throw new InvalidOperationException("Interaction inattendue pendant la lecture du quota.");
            if (Text(frame, "type") != "control_response" || !frame.TryGetProperty("response", out var response)
                || Text(response, "request_id") != id) continue;
            if (Text(response, "subtype") != "success" || !response.TryGetProperty("response", out var result))
                throw new InvalidOperationException("Lecture expérimentale du quota non prise en charge.");
            return result.Clone();
        }
    }

    internal static async Task SendAsync(StreamWriter input, object value, CancellationToken ct)
    {
        await input.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), ct);
        await input.FlushAsync(ct);
    }

    public static IReadOnlyList<AiQuotaWindow> ParseCodex(JsonElement root)
    {
        var windows = new List<AiQuotaWindow>();
        if (root.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind == JsonValueKind.Object
            && buckets.EnumerateObject().Any())
        {
            foreach (var bucket in buckets.EnumerateObject().Take(32)) AddCodex(bucket.Name, bucket.Value, windows);
        }
        else if (root.TryGetProperty("rateLimits", out var single) && single.ValueKind == JsonValueKind.Object)
            AddCodex(Text(single, "limitId") ?? "codex", single, windows);
        return windows;
    }

    private static void AddCodex(string id, JsonElement bucket, List<AiQuotaWindow> windows)
    {
        var label = Text(bucket, "limitName") ?? (id == "codex" ? "Codex" : id);
        foreach (var key in new[] { "primary", "secondary" })
        {
            if (!bucket.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object) continue;
            var minutes = window.TryGetProperty("windowDurationMins", out var duration) && duration.TryGetInt64(out var number) && number > 0 ? number : (long?)null;
            var name = label + " · " + (minutes is { } count
                ? count % 1440 == 0 ? $"{count / 1440} jours" : count % 60 == 0 ? $"{count / 60} heures" : $"{count} min"
                : key == "primary" ? "Fenêtre principale" : "Fenêtre secondaire");
            DateTimeOffset? reset = window.TryGetProperty("resetsAt", out var value) && value.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(value.GetInt64()) : null;
            windows.Add(new(id + "/" + key, name, Percent(window, "usedPercent"), reset));
        }
    }

    public static IReadOnlyList<AiQuotaWindow> ParseClaude(JsonElement root)
    {
        if (!root.TryGetProperty("rate_limits_available", out var available) || available.ValueKind != JsonValueKind.True
            || !root.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object) return [];
        var windows = new List<AiQuotaWindow>();
        foreach (var (key, name) in new[] { ("five_hour", "5 heures"), ("seven_day", "7 jours"),
            ("seven_day_opus", "7 jours · Opus"), ("seven_day_sonnet", "7 jours · Sonnet"), ("seven_day_oauth_apps", "7 jours · applications OAuth") })
            if (limits.TryGetProperty(key, out var window) && window.ValueKind == JsonValueKind.Object)
                windows.Add(new(key, name, Percent(window, "utilization"), Timestamp(window, "resets_at")));
        if (limits.TryGetProperty("model_scoped", out var scoped) && scoped.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var window in scoped.EnumerateArray().Take(32))
                windows.Add(new("model/" + index++, "7 jours · " + (Text(window, "display_name") ?? "Modèle"),
                    Percent(window, "utilization"), Timestamp(window, "resets_at")));
        }
        return windows;
    }

    private static double? Percent(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return null;
        var number = field.GetDouble();
        if (!double.IsFinite(number) || number is < 0 or > 100) throw new FormatException("Pourcentage de quota invalide.");
        return number;
    }
    private static DateTimeOffset? Timestamp(JsonElement value, string name) => Text(value, name) is { } text
        ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null;
    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString()?[..Math.Min(field.GetString()!.Length, 160)] : null;

    internal sealed class Frames(StreamReader output)
    {
        private int total;
        public async Task<JsonElement> ReadAsync(CancellationToken ct)
        {
            var line = new StringBuilder();
            var character = new char[1];
            while (await output.ReadAsync(character.AsMemory(), ct) > 0)
            {
                if (++total > 1024 * 1024 || line.Length > 256 * 1024) throw new InvalidOperationException("Réponse de quota trop volumineuse.");
                if (character[0] == '\n')
                {
                    if (line.Length == 0) continue;
                    using var document = JsonDocument.Parse(line.ToString());
                    if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                    return document.RootElement.Clone();
                }
                line.Append(character[0]);
            }
            throw new InvalidOperationException("Réponse de quota incomplète.");
        }
    }
}
