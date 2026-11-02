using Encoding.Contracts.Flows;
using Encoding.NodeAgent.Services;

namespace Encoding.NodeAgent.Runtime.Media.Handlers;

public sealed class MediaExtractionActionHandler(IProcessToolRunner tools, MediaScanner scanner) : IMediaActionHandler
{
    public bool CanHandle(string actionId) => actionId is
        "base-verify-decoding" or "video-verify-decoding" or
        "base-video-thumbnail" or "video-thumbnail" or
        "base-extract-audio" or "audio-extract" or
        "base-extract-subtitles" or "subtitles-extract";

    public async Task<MediaActionOutcome> ExecuteAsync(MediaActionContext context, CancellationToken ct)
    {
        var id = context.Id;
        var config = context.Config;
        var current = context.Current;
        var directory = context.Directory;
        var node = context.Node;

        if (id is "base-verify-decoding" or "video-verify-decoding")
            return await VerifyDecodingAsync(config, current, directory, ct);

        if (id is "base-video-thumbnail" or "video-thumbnail")
        {
            var file = MediaActions.Output(directory, node, ".jpg");
            await tools.CheckedAsync("ffmpeg", ["-nostdin", "-y", "-v", "error", "-ss", MediaActions.Number(config, "positionSeconds"),
                "-i", current, "-frames:v", "1", "-vf",
                "scale=" + MediaActions.Number(config, "width", 1280) + ":" + MediaActions.Number(config, "height", 720) + ":force_original_aspect_ratio=decrease", file], directory, ct);
            return new(null, [file]);
        }

        if (id is "base-extract-audio" or "audio-extract")
        {
            var codec = MediaActions.Text(config, "audioCodec", "copy");
            var file = MediaActions.Output(directory, node, codec == "flac" ? ".flac" : codec == "opus" ? ".opus" : ".mka");
            await tools.CheckedAsync("ffmpeg", ["-nostdin", "-y", "-v", "error", "-i", current, "-map", "0:a:" + MediaActions.Number(config, "trackId"),
                "-vn", "-c:a", codec == "opus" ? "libopus" : codec, file], directory, ct);
            return new(null, [file]);
        }

        return await ExtractSubtitlesAsync(config, current, directory, node, ct);
    }

    private async Task<MediaActionOutcome> VerifyDecodingAsync(IReadOnlyDictionary<string, System.Text.Json.JsonElement> config, string current, string directory, CancellationToken ct)
    {
        var args = new List<string> { "-nostdin", "-v", "error", "-xerror", "-i", current };
        var selected = MediaActions.Ids(MediaActions.Text(config, "trackIds"));
        if (MediaActions.Text(config, "language") != "")
        {
            var metadata = await scanner.ScanAsync(current, ct);
            var targetType = MediaActions.Text(config, "trackType", "all");
            var expectedType = targetType == "subtitles" ? "subtitle" : targetType;
            selected = metadata.GetProperty("streams").EnumerateArray().Where(s =>
                s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("language", out var language)
                && language.GetString() == MediaActions.Text(config, "language")
                && (selected.Length == 0 || selected.Contains(s.GetProperty("index").GetInt32()))
                && (targetType == "all" || s.GetProperty("codec_type").GetString() == expectedType))
                .Select(s => s.GetProperty("index").GetInt32()).ToArray();
            if (selected.Length == 0) throw new MediaToolException("sélection de pistes", 1);
        }
        if (selected.Length > 0) foreach (var track in selected) args.AddRange(["-map", "0:" + track]);
        else if (MediaActions.Text(config, "trackType", "all") != "all") args.AddRange(["-map", "0:" + MediaActions.Text(config, "trackType")[0] + "?"]);
        args.AddRange(["-fps_mode", "vfr", "-f", "null", "-"]);
        await tools.CheckedAsync("ffmpeg", args, directory, ct);
        return new(null, []);
    }

    private async Task<MediaActionOutcome> ExtractSubtitlesAsync(IReadOnlyDictionary<string, System.Text.Json.JsonElement> config, string current, string directory, string node, CancellationToken ct)
    {
        var metadata = await scanner.ScanAsync(current, ct);
        var selected = MediaActions.Ids(MediaActions.Text(config, "trackIds"));
        var files = new List<string>();
        foreach (var stream in metadata.GetProperty("streams").EnumerateArray().Where(s => s.GetProperty("codec_type").GetString() == "subtitle"
            && (selected.Length == 0 || selected.Contains(s.GetProperty("index").GetInt32()))))
        {
            var file = MediaActions.Output(directory, node + stream.GetProperty("index").GetInt32(), stream.GetProperty("codec_name").GetString() == "ass" ? ".ass" : ".srt");
            await tools.CheckedAsync("ffmpeg", ["-nostdin", "-y", "-v", "error", "-i", current, "-map", "0:" + stream.GetProperty("index").GetInt32(), file], directory, ct);
            files.Add(file);
        }
        return new(null, files);
    }
}
