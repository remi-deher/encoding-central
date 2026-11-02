using System.Globalization;
using System.Text.Json;

namespace Encoding.Contracts.Flows;

public static class VideoResizePolicy
{
    public static (int Width, int Height) Bounds(IReadOnlyDictionary<string, JsonElement> values)
    {
        string Text(string key) => values.TryGetValue(key, out var value) ? value.GetString() ?? "" : "";
        int Number(string key, int fallback) => values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : fallback;
        return Text("resolution") switch { "720p" => (1280, 720), "1080p" => (1920, 1080), "1440p" => (2560, 1440), "2160p" => (3840, 2160), _ => (Number("width", 1920), Number("height", 1080)) };
    }
    public static bool OnlyReduce(IReadOnlyDictionary<string, JsonElement> values) => values.TryGetValue("onlyIfLarger", out var flag) && flag.ValueKind == JsonValueKind.True;
    public static bool ShouldResize(IReadOnlyDictionary<string, JsonElement> values, int width, int height)
    {
        var target = Bounds(values);
        return !OnlyReduce(values) || width <= 0 || height <= 0 || width > target.Width || height > target.Height;
    }
    public static string Filter(IReadOnlyDictionary<string, JsonElement> values)
    {
        var (width, height) = Bounds(values);
        return OnlyReduce(values)
            ? $"scale=w='min(iw,{width})':h='min(ih,{height})':force_original_aspect_ratio=decrease:force_divisible_by=2"
            : $"scale={width}:{height}:force_original_aspect_ratio=decrease";
    }
}

public static class AudioNormalizationPolicy
{
    public static string Filter(IReadOnlyDictionary<string, JsonElement> values)
    {
        string Number(string key, decimal fallback) => (values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDecimal(out var number) ? number : fallback).ToString(CultureInfo.InvariantCulture);
        var profile = values.TryGetValue("normalizationProfile", out var selected) ? selected.GetString() : "custom";
        var target = profile switch { "broadcast" => "-23", "streaming" => "-16", "night" => "-18", _ => Number("targetLoudness", -16) };
        var range = profile == "night" ? "4" : Number("loudnessRange", 7);
        return "loudnorm=I=" + target + ":TP=" + Number("truePeak", -1) + ":LRA=" + range;
    }
    public static string MeasuredFilter(string filter, string error)
    {
        var start = error.LastIndexOf('{'); var end = error.LastIndexOf('}');
        if (start < 0 || end <= start) throw new IOException("Analyse de normalisation audio incomplète.");
        using var document = JsonDocument.Parse(error[start..(end + 1)]);
        string Measured(string key)
        {
            var raw = document.RootElement.GetProperty(key).GetString();
            if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                throw new IOException("Mesure audio indisponible ou non finie : " + key);
            return number.ToString(CultureInfo.InvariantCulture);
        }
        return filter + ":measured_I=" + Measured("input_i") + ":measured_TP=" + Measured("input_tp")
            + ":measured_LRA=" + Measured("input_lra") + ":measured_thresh=" + Measured("input_thresh")
            + ":offset=" + Measured("target_offset") + ":linear=true";
    }
}
