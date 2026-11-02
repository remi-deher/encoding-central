using System.Text.Json;

namespace Encoding.Contracts.Flows;

/// <summary>A builder is a sequential preparation scope, not a second flow entry or a media result.</summary>
public static class FfmpegBuilderPolicy
{
    public const string Start = "base-ffmpeg-builder-start";
    public const string Execute = "base-ffmpeg-builder-execute";
    public const string StateKey = "runtime.ffmpegBuilder";
    public static bool Active(IReadOnlyDictionary<string, JsonElement> variables) => variables.ContainsKey(StateKey);
    public static void Close(IDictionary<string,JsonElement> variables)
    {
        variables.Remove(StateKey); variables.Remove("plan.tool"); variables.Remove("plan.preserveUnchangedTracks");
    }
    public static bool IsSetting(string id) => FlowMediaValues.IsFfmpegPlan(id)
        || id is "base-audio-rules" or "audio-rules" or "audio-convert-by-rules";
    public static bool CanPass(string plugin, string id) =>
        plugin == "conditions" || plugin == "nodes"
        || plugin is "base" or "video" or "audio" && IsSetting(id)
        || plugin == "base" && id == "base-variable"
        || plugin is "base" or "probe" && (id.StartsWith("base-probe-", StringComparison.Ordinal)
            || id is "probe-media" or "probe-tracks" or "probe-video" or "probe-audio" or "probe-hdr");
    public static bool IsPlanned(string id, IReadOnlyDictionary<string, JsonElement> configuration,
        IReadOnlyDictionary<string, JsonElement> variables) => Active(variables) && IsSetting(id)
        || configuration.TryGetValue("processingMode", out var mode) && mode.ValueKind == JsonValueKind.String && mode.GetString() == "plan";
}
