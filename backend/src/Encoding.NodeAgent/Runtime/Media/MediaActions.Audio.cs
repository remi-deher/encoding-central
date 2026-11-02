using System.Globalization;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;

namespace Encoding.NodeAgent.Runtime.Media;

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
            if (Path.GetExtension(current) == ".flac" && codec != "flac") extension = conversion is null ? ".mka" : ".mkv";
            if (Path.GetExtension(current) == ".mp4" && codec == "flac") extension = ".mkv";
        }
        return extension;
    }
}
