using System.Globalization;
using System.Text.Json;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;

namespace Encoding.WorkerAgent.Runtime.Media;

public sealed partial class MediaActions
{
    /// <summary>One codec and one ordered filter chain per output track, independent of block order.</summary>
    private async Task<string?> AppendAudioPlanAsync(IReadOnlyList<RuntimePlanStep> plan, string current, string directory,
        List<string> input, List<string> args, bool preserve, CancellationToken ct)
    {
        var tracks = plan.Where(step => step.ComponentId is "base-transcode-audio" or "audio-transcode" or "base-normalize-audio" or "audio-normalize")
            .GroupBy(step => Number(step.Configuration, "trackId")).ToArray();
        string? extension = null;
        for (var ordinal = 0; ordinal < tracks.Length; ordinal++)
        {
            var track = tracks[ordinal];
            var outputIndex = preserve ? track.Key : ordinal.ToString(CultureInfo.InvariantCulture);
            var conversion = track.LastOrDefault(step => step.ComponentId is "base-transcode-audio" or "audio-transcode");
            var codec = conversion is null ? "aac" : Text(conversion.Configuration, "audioCodec", "aac");
            var filters = new List<string>();
            args.AddRange(["-c:a:" + outputIndex, codec == "opus" ? "libopus" : codec]);
            if (conversion is not null)
            {
                var configuration = conversion.Configuration;
                if (codec != "flac") args.AddRange(["-b:a:" + outputIndex, Number(configuration, "bitrateKbps", 192) + "k"]);
                var channels = Text(configuration, "channels", "source");
                if (channels != "source") args.AddRange(["-ac:a:" + outputIndex, channels]);
                var inputLayout = Text(configuration, "inputLayout");
                if (inputLayout.Length > 0) input.AddRange(["-channel_layout:a:" + track.Key, inputLayout]);
                var outputLayout = Text(configuration, "outputLayout");
                if (outputLayout.Length > 0) args.AddRange(["-channel_layout:a:" + outputIndex, outputLayout]);
                var rate = Number(configuration, "sampleRate");
                if (rate != "0") { args.AddRange(["-ar:a:" + outputIndex, rate]); filters.Add("aresample=" + rate); }
                var filterLayout = outputLayout.Length > 0 ? outputLayout : channels switch { "2" => "stereo", "6" => "5.1", "8" => "7.1", _ => "" };
                if (filterLayout.Length > 0) filters.Add("aformat=channel_layouts='" + filterLayout + "'");
            }
            foreach (var normalization in track.Where(step => step.ComponentId is "base-normalize-audio" or "audio-normalize"))
            {
                var filter = AudioNormalizationPolicy.Filter(normalization.Configuration);
                if (Text(normalization.Configuration, "normalizationMethod", "single") == "twoPass")
                {
                    var analysisInput = new List<string> { "-nostdin", "-hide_banner" };
                    if (conversion is not null && Text(conversion.Configuration, "inputLayout") is { Length: > 0 } layout)
                        analysisInput.AddRange(["-channel_layout:a:" + track.Key, layout]);
                    analysisInput.AddRange(["-i", current, "-map", "0:a:" + track.Key, "-af",
                        string.Join(',', filters.Append(filter + ":print_format=json")), "-f", "null", "-"]);
                    var measurement = await tools.CheckedWithResultAsync("ffmpeg", analysisInput, directory, ct);
                    filter = AudioNormalizationPolicy.MeasuredFilter(filter, measurement.Error);
                }
                filters.Add(filter);
            }
            if (filters.Count > 0) args.AddRange(["-filter:a:" + outputIndex, string.Join(',', filters)]);
            if (AudioOnlyContainerOverride(Path.GetExtension(current), codec) is { } native)
                extension = extension is null || extension.Equals(native, StringComparison.OrdinalIgnoreCase) ? native : ".mka";
        }
        return extension;
    }

    /// <summary>Conteneur du plan : l'extension de la source, ou celle du dernier bloc de remux.</summary>
    internal static string PlannedContainerExtension(string current, IReadOnlyList<RuntimePlanStep> plan)
    {
        var remux = plan.LastOrDefault(step => step.ComponentId is "base-remux-media" or "video-remux-media");
        if (remux is not null)
        {
            var container = Text(remux.Configuration, "container", "mkv").Trim().TrimStart('.');
            if (container.Length == 0 || container.Any(ch => !char.IsAsciiLetterOrDigit(ch)))
                throw new IOException("Le conteneur demandé par le remux n'est pas un nom de fichier.");
            return "." + container;
        }
        var extension = Path.GetExtension(current);
        if (string.IsNullOrEmpty(extension))
            throw new IOException("Le fichier n'a pas d'extension : seul un bloc de remux choisit le conteneur.");
        return extension;
    }

    /// <summary>Encodeur CPU du même codec. Un filtre ne change pas le codec vidéo.</summary>
    internal static string SameCodecVideoEncoder(string? codec)
    {
        var name = (codec ?? "").Trim().ToLowerInvariant();
        var catalogCodec = name switch { "h264" or "avc" => "h264", "hevc" or "h265" => "hevc", "av1" => "av1", _ => null };
        if (catalogCodec is not null)
        {
            var cpu = VideoEncoderCatalog.All.FirstOrDefault(item => item.Codec == catalogCodec && item.Acceleration == "cpu");
            if (cpu is not null) return cpu.Id;
        }
        return name switch
        {
            "mpeg4" => "mpeg4",
            "mpeg2video" => "mpeg2video",
            "vp9" => "libvpx-vp9",
            "vp8" => "libvpx",
            "theora" => "libtheora",
            _ => throw new IOException($"Le filtre vidéo conserve le codec « {(name.Length == 0 ? "inconnu" : name)} » : aucun encodeur de ce codec n'est prévu.")
        };
    }

    private static string? FirstVideoCodec(JsonElement? metadata)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } meta || !meta.TryGetProperty("streams", out var streams)) return null;
        foreach (var stream in streams.EnumerateArray())
        {
            if (stream.TryGetProperty("codec_type", out var type) && type.GetString() == "video"
                && !(stream.TryGetProperty("disposition", out var disposition) && disposition.TryGetProperty("attached_pic", out var picture)
                    && picture.ValueKind == JsonValueKind.Number && picture.GetInt32() == 1)
                && stream.TryGetProperty("codec_name", out var codec))
                return codec.GetString();
        }
        return null;
    }

    private static readonly HashSet<string> AudioOnlyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".aac", ".m4a", ".opus", ".ogg", ".wav", ".ac3", ".eac3", ".mka"
    };

    /// <summary>Extension native quand le conteneur audio d'origine ne peut pas porter le codec. Null si l'extension reste celle de la source. Jamais un conteneur vidéo.</summary>
    internal static string? AudioOnlyContainerOverride(string sourceExtension, string codec)
    {
        if (!AudioOnlyExtensions.Contains(sourceExtension) || AudioCodecFits(sourceExtension, codec)) return null;
        return NativeAudioExtension(codec);
    }

    private static bool AudioCodecFits(string extension, string codec) => extension.ToLowerInvariant() switch
    {
        ".flac" => codec == "flac",
        ".mp3" => codec is "mp3" or "libmp3lame",
        ".aac" => codec == "aac",
        ".opus" => codec is "opus" or "libopus",
        ".ogg" => codec is "opus" or "libopus" or "vorbis" or "libvorbis",
        ".wav" => codec is "pcm_s16le" or "pcm_s24le" or "pcm_s32le",
        ".ac3" => codec == "ac3",
        ".eac3" => codec == "eac3",
        ".m4a" => codec is "aac" or "alac" or "ac3" or "eac3" or "flac" or "opus" or "libopus",
        ".mka" => true,
        _ => true
    };

    private static string NativeAudioExtension(string codec) => codec switch
    {
        "aac" => ".m4a",
        "alac" => ".m4a",
        "flac" => ".flac",
        "opus" or "libopus" => ".opus",
        "mp3" or "libmp3lame" => ".mp3",
        "vorbis" or "libvorbis" => ".ogg",
        "ac3" => ".ac3",
        "eac3" => ".eac3",
        "pcm_s16le" or "pcm_s24le" or "pcm_s32le" => ".wav",
        _ => ".mka"
    };
}
