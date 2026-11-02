using System.Text.Json;

namespace Encoding.Contracts.Flows;

/// <summary>Focused operations reuse the historical planners with explicit, enforced effects.</summary>
public static class FlowOperationConfiguration
{
    public static bool IsSubtitleOperation(string id) => id is "subtitles-convert-ass" or "subtitles-style-ass"
        or "subtitles-remove-duplicates" or "subtitles-detect-forced";
    public static bool IsNamingOperation(string id) => id is "subtitles-name-tracks" or "subtitles-default-audio"
        or "subtitles-snapshot-track-titles";

    public static IReadOnlyDictionary<string, JsonElement> Resolve(string id, IReadOnlyDictionary<string, JsonElement> configuration)
    {
        if (!IsSubtitleOperation(id) && !IsNamingOperation(id)) return configuration;
        var values = configuration.ToDictionary(item => item.Key, item => item.Value);
        void Set(string key, object value) => values[key] = JsonSerializer.SerializeToElement(value);
        if (IsSubtitleOperation(id))
        {
            Set("operation", id);
            foreach (var key in new[] { "convertSrt", "upgradeAss", "removeDuplicates", "inferForced", "setTitles", "frenchDefault" }) Set(key, false);
            Set("convertSrt", id == "subtitles-convert-ass");
            Set("upgradeAss", id == "subtitles-style-ass");
            Set("removeDuplicates", id == "subtitles-remove-duplicates");
            Set("inferForced", id == "subtitles-detect-forced");
            if (id is not ("subtitles-convert-ass" or "subtitles-style-ass")) Set("styleProfile", "none");
        }
        else
        {
            Set("mode", id == "subtitles-snapshot-track-titles" ? "snapshot" : "apply");
            Set("renameTracks", id == "subtitles-name-tracks");
            Set("singleDefaultAudio", id == "subtitles-default-audio");
        }
        return values;
    }
}
