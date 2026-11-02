using System.Globalization;
using System.Text.Json;

namespace Encoding.Contracts.Flows.Conditions;

/// <summary>Évalue les blocs de condition média spécialisés (codec vidéo, codec audio, résolution, canaux, langue) directement sur les métadonnées scannées.</summary>
public static class MediaConditionEvaluator
{
    public const string VideoCodec = "conditions-video-codec";
    public const string AudioCodec = "conditions-audio-codec";
    public const string VideoResolution = "conditions-video-resolution";
    public const string AudioChannels = "conditions-audio-channels";
    public const string AudioLanguage = "conditions-audio-language";
    public const string VideoHdr = "conditions-video-hdr";
    public const string VideoBitDepth = "conditions-video-bitdepth";

    public static bool IsMediaCondition(string componentId) =>
        componentId is VideoCodec or AudioCodec or VideoResolution or AudioChannels or AudioLanguage or VideoHdr or VideoBitDepth;

    public static string Evaluate(string componentId, IReadOnlyDictionary<string, JsonElement> config, JsonElement? metadata) =>
        componentId switch
        {
            VideoCodec => EvaluateVideoCodec(config, metadata),
            AudioCodec => EvaluateAudioCodec(config, metadata),
            VideoResolution => EvaluateVideoResolution(config, metadata),
            AudioChannels => EvaluateAudioChannels(config, metadata),
            AudioLanguage => EvaluateAudioLanguage(config, metadata),
            VideoHdr => EvaluateVideoHdr(config, metadata),
            VideoBitDepth => EvaluateVideoBitDepth(config, metadata),
            _ => throw new ConditionEvaluationException($"Composant de condition média inconnu : « {componentId} ».")
        };

    private static string EvaluateVideoCodec(IReadOnlyDictionary<string, JsonElement> config, JsonElement? metadata)
    {
        var streams = GetStreams(metadata);
        var video = streams.FirstOrDefault(s => StreamType(s) == "video");
        if (video.ValueKind != JsonValueKind.Object)
            throw new ConditionEvaluationException("Aucune piste vidéo n'est présente dans le média.");

        var actual = GetString(video, "codec_name");
        if (string.IsNullOrWhiteSpace(actual))
            throw new ConditionEvaluationException("Le codec vidéo est absent des métadonnées.");

        var target = GetConfigString(config, "codec", "hevc");
        var invert = GetConfigBool(config, "invert");

        var matches = MatchesVideoCodec(actual, target);
        if (invert) matches = !matches;
        return matches ? "yes" : "no";
    }

    private static string EvaluateAudioCodec(IReadOnlyDictionary<string, JsonElement> config, JsonElement? metadata)
    {
        var streams = GetStreams(metadata);
        var audioTracks = streams.Where(s => StreamType(s) == "audio").ToList();
        if (audioTracks.Count == 0)
            throw new ConditionEvaluationException("Aucune piste audio n'est présente dans le média.");

        var target = GetConfigString(config, "codec", "eac3");
        var scope = GetConfigString(config, "scope", "any");
        var invert = GetConfigBool(config, "invert");

        bool matches;
        if (scope == "first")
        {
            var codec = GetString(audioTracks[0], "codec_name");
            matches = !string.IsNullOrWhiteSpace(codec) && MatchesAudioCodec(codec, target);
        }
        else if (scope == "all")
        {
            matches = audioTracks.All(track =>
            {
                var codec = GetString(track, "codec_name");
                return !string.IsNullOrWhiteSpace(codec) && MatchesAudioCodec(codec, target);
            });
        }
        else // "any"
        {
            matches = audioTracks.Any(track =>
            {
                var codec = GetString(track, "codec_name");
                return !string.IsNullOrWhiteSpace(codec) && MatchesAudioCodec(codec, target);
            });
        }

        if (invert) matches = !matches;
        return matches ? "yes" : "no";
    }

    private static string EvaluateVideoResolution(IReadOnlyDictionary<string, JsonElement> config, JsonElement? metadata)
    {
        var streams = GetStreams(metadata);
        var video = streams.FirstOrDefault(s => StreamType(s) == "video");
        if (video.ValueKind != JsonValueKind.Object)
            throw new ConditionEvaluationException("Aucune piste vidéo n'est présente dans le média.");

        var width = GetNumber(video, "width");
        var height = GetNumber(video, "height");
        if (height <= 0 && width <= 0)
            throw new ConditionEvaluationException("La résolution vidéo est absente des métadonnées.");

        var target = GetConfigString(config, "resolution", "1080p");
        var op = GetConfigString(config, "operator", "gte");

        var actualRank = ResolutionRank(width, height);
        var targetRank = TargetRank(target);

        var matches = op switch
        {
            "gte" => actualRank >= targetRank,
            "eq" => actualRank == targetRank,
            "lte" => actualRank <= targetRank,
            _ => throw new ConditionEvaluationException($"Opérateur de résolution « {op} » non reconnu.")
        };

        return matches ? "yes" : "no";
    }

    private static string EvaluateAudioChannels(IReadOnlyDictionary<string, JsonElement> config, JsonElement? metadata)
    {
        var streams = GetStreams(metadata);
        var audioTracks = streams.Where(s => StreamType(s) == "audio").ToList();
        if (audioTracks.Count == 0)
            throw new ConditionEvaluationException("Aucune piste audio n'est présente dans le média.");

        var targetString = GetConfigString(config, "channels", "6");
        if (!int.TryParse(targetString, out var targetChannels)) targetChannels = 6;
        var op = GetConfigString(config, "operator", "gte");
        var scope = GetConfigString(config, "scope", "first");

        bool MatchChannels(JsonElement track)
        {
            var channels = (int)GetNumber(track, "channels");
            return op switch
            {
                "gte" => channels >= targetChannels,
                "eq" => channels == targetChannels,
                "lte" => channels <= targetChannels,
                _ => throw new ConditionEvaluationException($"Opérateur « {op} » non reconnu.")
            };
        }

        var matches = scope == "any"
            ? audioTracks.Any(MatchChannels)
            : MatchChannels(audioTracks[0]);

        return matches ? "yes" : "no";
    }

    private static string EvaluateAudioLanguage(IReadOnlyDictionary<string, JsonElement> config, JsonElement? metadata)
    {
        var streams = GetStreams(metadata);
        var audioTracks = streams.Where(s => StreamType(s) == "audio").ToList();
        if (audioTracks.Count == 0)
            throw new ConditionEvaluationException("Aucune piste audio n'est présente dans le média.");

        var target = NormalizeLanguage(GetConfigString(config, "language", "fra"));
        var scope = GetConfigString(config, "scope", "any");
        var invert = GetConfigBool(config, "invert");

        bool MatchLanguage(JsonElement track)
        {
            string? lang = null;
            if (track.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object
                && tags.TryGetProperty("language", out var langVal) && langVal.ValueKind == JsonValueKind.String)
            {
                lang = langVal.GetString();
            }
            var normalized = NormalizeLanguage(lang ?? "und");
            return string.Equals(normalized, target, StringComparison.OrdinalIgnoreCase);
        }

        var matches = scope == "first"
            ? MatchLanguage(audioTracks[0])
            : audioTracks.Any(MatchLanguage);

        if (invert) matches = !matches;
        return matches ? "yes" : "no";
    }

    private static bool MatchesVideoCodec(string actual, string target)
    {
        actual = NormalizeVideoCodec(actual);
        target = NormalizeVideoCodec(target);
        return string.Equals(actual, target, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVideoCodec(string codec) => codec.Trim().ToLowerInvariant() switch
    {
        "h265" or "hevc" => "hevc",
        "h264" or "avc" or "x264" => "h264",
        "mpeg2" or "mpeg2video" => "mpeg2video",
        "vc1" or "vc-1" => "vc1",
        var other => other
    };

    private static bool MatchesAudioCodec(string actual, string target)
    {
        actual = NormalizeAudioCodec(actual);
        target = NormalizeAudioCodec(target);
        return string.Equals(actual, target, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeAudioCodec(string codec) => codec.Trim().ToLowerInvariant() switch
    {
        "eac3" or "e-ac-3" or "ec-3" or "ec3" => "eac3",
        "ac3" or "ac-3" => "ac3",
        "dts" or "dca" => "dts",
        "dtshd" or "dts-hd" => "dtshd",
        "truehd" => "truehd",
        "mp3" => "mp3",
        var other => other
    };

    private static int TargetRank(string resolution) => resolution.Trim().ToLowerInvariant() switch
    {
        "4k" or "2160p" or "uhd" => 2160,
        "1080p" or "fhd" => 1080,
        "720p" or "hd" => 720,
        "sd" or "480p" or "576p" => 480,
        _ => int.TryParse(resolution.Replace("p", "", StringComparison.OrdinalIgnoreCase), out var p) ? p : 1080
    };

    private static int ResolutionRank(decimal width, decimal height)
    {
        if (width >= 3200 || height >= 1800) return 2160;
        if (width >= 1600 || height >= 900) return 1080;
        if (width >= 1000 || height >= 600) return 720;
        return 480;
    }

    private static string NormalizeLanguage(string lang) => lang.Trim().ToLowerInvariant() switch
    {
        "fr" or "fre" or "fra" => "fra",
        "en" or "eng" => "eng",
        "de" or "ger" or "deu" => "deu",
        "es" or "spa" => "spa",
        "it" or "ita" => "ita",
        "pt" or "por" => "por",
        "ru" or "rus" => "rus",
        "ja" or "jpn" => "jpn",
        "zh" or "chi" or "zho" => "zho",
        "ko" or "kor" => "kor",
        "und" or "" => "und",
        var other => other
    };

    [Flags]
    public enum HdrFormat
    {
        Sdr = 0,
        Hdr10 = 1,
        Hdr10Plus = 2,
        DolbyVision = 4,
        Hlg = 8
    }

    private static string EvaluateVideoHdr(IReadOnlyDictionary<string, JsonElement> config, JsonElement? metadata)
    {
        var streams = GetStreams(metadata);
        var video = streams.FirstOrDefault(s => StreamType(s) == "video");
        if (video.ValueKind != JsonValueKind.Object)
            throw new ConditionEvaluationException("Aucune piste vidéo n'est présente dans le média.");

        var target = GetConfigString(config, "format", "any-hdr");
        var invert = GetConfigBool(config, "invert");

        var detected = DetectHdrFormat(video);
        var matches = target switch
        {
            "dolby-vision" => detected.HasFlag(HdrFormat.DolbyVision),
            "hdr10" => detected.HasFlag(HdrFormat.Hdr10),
            "hdr10plus" => detected.HasFlag(HdrFormat.Hdr10Plus),
            "hlg" => detected.HasFlag(HdrFormat.Hlg),
            "any-hdr" => detected != HdrFormat.Sdr,
            "sdr" => detected == HdrFormat.Sdr,
            _ => throw new ConditionEvaluationException($"Format HDR cible « {target} » non reconnu.")
        };

        if (invert) matches = !matches;
        return matches ? "yes" : "no";
    }

    public static HdrFormat DetectHdrFormat(JsonElement videoStream)
    {
        var result = HdrFormat.Sdr;
        if (videoStream.ValueKind != JsonValueKind.Object) return result;

        if (videoStream.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sideData.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var sideDataType = GetString(item, "side_data_type") ?? "";
                if (sideDataType.Contains("DOVI", StringComparison.OrdinalIgnoreCase)
                    || sideDataType.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase))
                {
                    result |= HdrFormat.DolbyVision;
                }
                if (sideDataType.Contains("SMPTE 2094-40", StringComparison.OrdinalIgnoreCase)
                    || sideDataType.Contains("HDR10+", StringComparison.OrdinalIgnoreCase))
                {
                    result |= HdrFormat.Hdr10Plus;
                }
                if (sideDataType.Contains("Mastering display", StringComparison.OrdinalIgnoreCase)
                    || sideDataType.Contains("Content light level", StringComparison.OrdinalIgnoreCase))
                {
                    result |= HdrFormat.Hdr10;
                }
            }
        }

        var transfer = GetString(videoStream, "color_transfer") ?? "";
        if (transfer.Equals("smpte2084", StringComparison.OrdinalIgnoreCase))
        {
            result |= HdrFormat.Hdr10;
        }
        else if (transfer.Contains("arib-std-b67", StringComparison.OrdinalIgnoreCase) || transfer.Equals("hlg", StringComparison.OrdinalIgnoreCase))
        {
            result |= HdrFormat.Hlg;
        }

        if (videoStream.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in tags.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString()!.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase))
                    result |= HdrFormat.DolbyVision;
            }
        }

        return result;
    }

    private static string EvaluateVideoBitDepth(IReadOnlyDictionary<string, JsonElement> config, JsonElement? metadata)
    {
        var streams = GetStreams(metadata);
        var video = streams.FirstOrDefault(s => StreamType(s) == "video");
        if (video.ValueKind != JsonValueKind.Object)
            throw new ConditionEvaluationException("Aucune piste vidéo n'est présente dans le média.");

        var target = GetConfigString(config, "depth", "10-bit");
        var invert = GetConfigBool(config, "invert");

        var actualDepth = DetectBitDepth(video);
        var matches = target switch
        {
            "10-bit" => actualDepth >= 10,
            "8-bit" => actualDepth <= 8,
            "12-bit" => actualDepth >= 12,
            _ => throw new ConditionEvaluationException($"Profondeur de couleur cible « {target} » non reconnue.")
        };

        if (invert) matches = !matches;
        return matches ? "yes" : "no";
    }

    public static int DetectBitDepth(JsonElement videoStream)
    {
        if (videoStream.ValueKind != JsonValueKind.Object) return 8;

        if (videoStream.TryGetProperty("bits_per_raw_sample", out var bprs))
        {
            if (bprs.ValueKind == JsonValueKind.Number && bprs.TryGetInt32(out var num) && num > 0)
                return num;
            if (bprs.ValueKind == JsonValueKind.String && int.TryParse(bprs.GetString(), out var strNum) && strNum > 0)
                return strNum;
        }

        var pixFmt = GetString(videoStream, "pix_fmt") ?? "";
        if (pixFmt.Contains("12", StringComparison.Ordinal)) return 12;
        if (pixFmt.Contains("10", StringComparison.Ordinal)) return 10;
        if (pixFmt.Contains("16", StringComparison.Ordinal)) return 16;

        var profile = GetString(videoStream, "profile") ?? "";
        if (profile.Contains("10", StringComparison.Ordinal)) return 10;

        return 8;
    }

    private static IEnumerable<JsonElement> GetStreams(JsonElement? metadata)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } meta)
            throw new ConditionEvaluationException("Aucune métadonnée média disponible pour évaluer la condition.");
        if (!meta.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            throw new ConditionEvaluationException("Aucune piste média détectée.");
        return streams.EnumerateArray();
    }

    private static string? StreamType(JsonElement stream) =>
        stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("codec_type", out var type) && type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString()
            : null;

    private static decimal GetNumber(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var val))
            return 0;
        if (val.ValueKind == JsonValueKind.Number && val.TryGetDecimal(out var d))
            return d;
        if (val.ValueKind == JsonValueKind.String && decimal.TryParse(val.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return 0;
    }

    private static string GetConfigString(IReadOnlyDictionary<string, JsonElement> config, string key, string fallback) =>
        config.TryGetValue(key, out var val) && val.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(val.GetString())
            ? val.GetString()!
            : fallback;

    private static bool GetConfigBool(IReadOnlyDictionary<string, JsonElement> config, string key) =>
        config.TryGetValue(key, out var val) && val.ValueKind is JsonValueKind.True or JsonValueKind.False && val.GetBoolean();
}
