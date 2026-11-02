using System.Text.Json;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

/// <summary>Provider introspection only: no user turn is sent or generated.</summary>
public static class AiModelProtocol
{
    public static async Task<IReadOnlyList<AiDiscoveredModel>> ReadCodexAsync(StreamReader output, StreamWriter input, CancellationToken ct)
    {
        var reader = new AiQuotaProtocol.Frames(output);
        await AiQuotaProtocol.SendAsync(input, new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "encoding_models", version = "1.0.0" } } }, ct);
        await AiQuotaProtocol.CodexResponseAsync(reader, 1, ct);
        await AiQuotaProtocol.SendAsync(input, new { method = "initialized" }, ct);
        var models = new List<AiDiscoveredModel>();
        string? cursor = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < 16; page++)
        {
            await AiQuotaProtocol.SendAsync(input, new { id = page + 2, method = "model/list", @params = new { cursor, limit = 100, includeHidden = false } }, ct);
            var result = await AiQuotaProtocol.CodexResponseAsync(reader, page + 2, ct);
            models.AddRange(ParseCodex(result));
            cursor = Text(result, "nextCursor");
            if (cursor is null) return models.DistinctBy(m => m.Id).ToArray();
            if (!cursors.Add(cursor)) throw new InvalidOperationException("Pagination du catalogue invalide.");
        }
        throw new InvalidOperationException("Catalogue trop volumineux.");
    }

    public static async Task<IReadOnlyList<AiDiscoveredModel>> ReadClaudeAsync(StreamReader output, StreamWriter input, CancellationToken ct)
    {
        var reader = new AiQuotaProtocol.Frames(output);
        await AiQuotaProtocol.SendAsync(input, new { type = "control_request", request_id = "models-init", request = new { subtype = "initialize" } }, ct);
        return ParseClaude(await AiQuotaProtocol.ClaudeResponseAsync(reader, "models-init", ct));
    }

    public static IReadOnlyList<AiDiscoveredModel> ParseCodex(JsonElement root)
    {
        var result = new List<AiDiscoveredModel>();
        foreach (var row in Rows(root, "data"))
        {
            if (Flag(row, "hidden")) continue;
            var id = Text(row, "model") ?? Text(row, "id");
            if (id is null) continue;
            var efforts = Rows(row, "supportedReasoningEfforts").Select(e => Text(e, "reasoningEffort")).OfType<string>().Distinct().ToArray();
            var defaultEffort = Text(row, "defaultReasoningEffort");
            result.Add(new(id, Text(row, "displayName") ?? id, efforts,
                efforts.Contains(defaultEffort) ? defaultEffort : null, Flag(row, "isDefault"), row.TryGetProperty("supportedReasoningEfforts", out _)));
        }
        return result.DistinctBy(m => m.Id).ToArray();
    }

    /// <summary>`default` is a CLI alias that duplicates another entry; the resolved model is shown beside the alias instead.</summary>
    public static IReadOnlyList<AiDiscoveredModel> ParseClaude(JsonElement root) => Rows(root, "models")
        .Select(row => (Row: row, Id: Text(row, "value"))).Where(item => item.Id is not null and not "default")
        .Select(item => new AiDiscoveredModel(item.Id!, ClaudeLabel(item.Row, item.Id!),
            Strings(item.Row, "supportedEffortLevels"), ReasoningKnown: item.Row.TryGetProperty("supportedEffortLevels", out _)
                || item.Row.TryGetProperty("supportsEffort", out var support) && support.ValueKind == JsonValueKind.False))
        .DistinctBy(m => m.Id).ToArray();

    private static string ClaudeLabel(JsonElement row, string id)
    {
        var name = Text(row, "displayName") ?? id;
        return Text(row, "resolvedModel") is { } resolved && resolved != id ? name + " · " + resolved : name;
    }

    /// <summary>Models API of Anthropic: `id` and `display_name`, newest first.</summary>
    public static IReadOnlyList<AiDiscoveredModel> ParseAnthropic(JsonElement root) => Rows(root, "data")
        .Select(row => (Id: Text(row, "id"), Name: Text(row, "display_name"))).Where(item => item.Id is not null)
        .Select(item => new AiDiscoveredModel(item.Id!, item.Name is null ? item.Id! : item.Name + " · " + item.Id,
            [], ReasoningKnown: false))
        .DistinctBy(m => m.Id).ToArray();

    public static IReadOnlyList<AiDiscoveredModel> ParseHttp(JsonElement root) => Rows(root, "data")
        .Select(row => Text(row, "id")).OfType<string>().Distinct()
        .Select(id => new AiDiscoveredModel(id, id, [])).ToArray();

    private static IEnumerable<JsonElement> Rows(JsonElement root, string field) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(field, out var rows) && rows.ValueKind == JsonValueKind.Array
        ? rows.EnumerateArray().Take(1000) : [];
    private static IReadOnlyList<string> Strings(JsonElement row, string field) => Rows(row, field)
        .Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)
        .Where(e => e.Length is > 0 and <= 64 && !e.Any(char.IsControl)).Distinct().ToArray();
    private static bool Flag(JsonElement row, string field) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.True;
    private static string? Text(JsonElement row, string field) => row.ValueKind == JsonValueKind.Object
        && row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 and <= 256 } text && !text.Any(char.IsControl) ? text : null;
}
