using System.Globalization;
using System.Text.Json;

namespace Encoding.Core;

/// <summary>Maps MediaInfo JSON into the metadata shape consumed by the shared flow conditions.</summary>
public static class MediaInfoMetadata
{
    public static JsonElement Normalize(JsonElement report)
    {
        if (!report.TryGetProperty("media", out var media) || !media.TryGetProperty("track", out var tracks)
            || tracks.ValueKind != JsonValueKind.Array || tracks.GetArrayLength() == 0)
            throw new InvalidDataException("MediaInfo n’a pas reconnu de métadonnées média.");
        var format = new Dictionary<string, object>(); var streams = new List<Dictionary<string, object>>();
        foreach (var track in tracks.EnumerateArray())
        {
            var type = Text(track, "@type");
            if (type == "General")
            {
                Number(track, "FileSize", format, "size"); Number(track, "Duration", format, "duration");
                continue;
            }
            if (type is not ("Video" or "Image" or "Audio" or "Text")) continue;
            var stream = new Dictionary<string, object> { ["codec_type"] = type is "Video" or "Image" ? "video" : type == "Audio" ? "audio" : "subtitle" };
            if (Text(track, "Format") is { } codec) stream["codec_name"] = Codec(codec, Text(track, "Format_Profile"));
            Number(track, "Width", stream, "width"); Number(track, "Height", stream, "height"); Number(track, "Channels", stream, "channels");
            if (Text(track, "Language") is { } language) stream["tags"] = new { language = Language(language) };
            streams.Add(stream);
        }
        if (streams.Count == 0) throw new InvalidDataException("MediaInfo n’a pas reconnu de piste média.");
        return JsonSerializer.SerializeToElement(new { format, streams });
    }
    private static string? Text(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static void Number(JsonElement item, string key, Dictionary<string, object> target, string destination)
    {
        if (item.TryGetProperty(key, out var value) && decimal.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number >= 0)
            target[destination] = number;
    }
    private static string Codec(string value, string? profile) => value.ToLowerInvariant() switch
    {
        "hevc" => "hevc", "avc" => "h264", "mpeg video" => "mpeg2video", "av1" => "av1", "vp9" => "vp9",
        "aac" => "aac", "ac-3" => "ac3", "e-ac-3" => "eac3", "flac" => "flac", "jpeg" => "mjpeg",
        "mpeg audio" when profile?.Contains("Layer 3", StringComparison.OrdinalIgnoreCase) == true => "mp3",
        "mpeg audio" when profile?.Contains("Layer 2", StringComparison.OrdinalIgnoreCase) == true => "mp2",
        _ => value.ToLowerInvariant()
    };
    private static string Language(string value) => value.ToLowerInvariant() switch
    {
        "fr" or "fre" => "fra", "en" => "eng", "de" or "ger" => "deu", "es" => "spa", "it" => "ita",
        "pt" => "por", "ja" => "jpn", "zh" or "chi" => "zho", "ru" => "rus", "nl" or "dut" => "nld",
        "ko" => "kor", "ar" => "ara", _ => value.ToLowerInvariant()
    };
}
