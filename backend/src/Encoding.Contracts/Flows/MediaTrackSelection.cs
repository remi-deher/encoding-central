using System.Text.Json;
using Encoding.Contracts.Executions;

namespace Encoding.Contracts.Flows;

/// <summary>Shared track matching. Indices remain local to the selected stream type.</summary>
public static class MediaTrackSelection
{
    public static string Text(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
    public static int Number(JsonElement row, string key, int fallback = 0) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number) ? number : fallback;
    public static string Language(string value) => value.ToLowerInvariant().Split('-')[0] switch
    {
        "" => "und", "fr" or "fre" or "fra" => "fra", "en" or "eng" => "eng", "ja" or "jpn" => "jpn",
        "de" or "ger" or "deu" => "deu", "es" or "spa" => "spa", "it" or "ita" => "ita", var code => code
    };
    private static string Tag(JsonElement stream, string key) => stream.TryGetProperty("tags", out var tags) ? Text(tags, key) : "";

    public static bool Matches(JsonElement stream, JsonElement rule)
    {
        bool List(string key, string value, bool language = false)
        {
            var choices = Text(rule, key).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return choices.Length == 0 || choices.Any(choice => language ? Language(choice) == Language(value) : choice.Equals(value, StringComparison.OrdinalIgnoreCase));
        }
        var channels = Number(stream, "channels");
        return List("languages", Tag(stream, "language"), true) && List("sourceCodecs", Text(stream, "codec_name"))
            && channels >= Number(rule, "minChannels") && (Number(rule, "maxChannels") == 0 || channels <= Number(rule, "maxChannels"))
            && Tag(stream, "title").Contains(Text(rule, "titleContains"), StringComparison.OrdinalIgnoreCase)
            && (Text(rule, "excludeCommentary") != "yes" || !Tag(stream, "title").Contains("comment", StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<RuntimePlanStep> Expand(IReadOnlyList<RuntimePlanStep> plan, JsonElement metadata)
    {
        var audio = metadata.GetProperty("streams").EnumerateArray().Where(stream => Text(stream, "codec_type") == "audio").ToArray();
        var result = new List<RuntimePlanStep>();
        foreach (var step in plan)
        {
            if (!step.Configuration.TryGetValue("trackSelection", out var selection) || selection.ValueKind != JsonValueKind.Array || selection.GetArrayLength() == 0)
            { result.Add(step); continue; }
            var criterion = selection[0];
            for (var index = 0; index < audio.Length; index++)
            {
                if (!Matches(audio[index], criterion)) continue;
                var configuration = step.Configuration.Where(item => item.Key != "trackSelection").ToDictionary(item => item.Key, item => item.Value);
                configuration["trackId"] = JsonSerializer.SerializeToElement(index);
                result.Add(step with { Configuration = configuration });
            }
        }
        return result;
    }
}
