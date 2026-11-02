using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.NodeAgent.Services;

namespace Encoding.NodeAgent.Runtime.Media.Handlers;

public sealed class MatroskaActionHandler(IProcessToolRunner tools) : IMediaActionHandler
{
    public bool CanHandle(string actionId) => actionId is
        "base-extract-track" or "base-extract-attachments" or "base-extract-chapters" or
        "mkv-extract-track" or "mkv-extract-attachments" or "mkv-extract-chapters" or
        "base-edit-track-properties" or "base-edit-matroska-metadata" or "base-remove-attachments" or "base-replace-chapters" or
        "mkv-edit-track-properties" or "mkv-edit-matroska-metadata" or "mkv-remove-attachments" or "mkv-replace-chapters" or
        "base-reorder-tracks" or "mkv-reorder-tracks" or
        "base-remux-matroska" or "base-remove-tracks" or "mkv-remux-matroska" or "mkv-remove-tracks" or
        "base-add-audio-track" or "base-add-subtitle-track" or "audio-add-track" or "subtitles-add-track";

    public async Task<MediaActionOutcome> ExecuteAsync(MediaActionContext context, CancellationToken ct)
    {
        var id = context.Id;
        var c = context.Config;
        var current = context.Current;
        var directory = context.Directory;
        var node = context.Node;
        var file = MediaActions.Output(directory, node, ".mkv");

        if (id is "base-extract-track" or "base-extract-attachments" or "base-extract-chapters" or "mkv-extract-track" or "mkv-extract-attachments" or "mkv-extract-chapters")
            return await ExtractAsync(id, c, current, directory, node, ct);

        if (id is "base-edit-track-properties" or "base-edit-matroska-metadata" or "base-remove-attachments"
            or "base-replace-chapters" or "mkv-edit-track-properties" or "mkv-edit-matroska-metadata"
            or "mkv-remove-attachments" or "mkv-replace-chapters")
            return await EditPropertiesAsync(id, c, current, directory, file, context.Extra, ct);

        return await MergeAsync(id, c, current, directory, file, context.Extra, ct);
    }

    private async Task<MediaActionOutcome> ExtractAsync(string id, IReadOnlyDictionary<string, JsonElement> c, string current, string directory, string node, CancellationToken ct)
    {
        if (id is "base-extract-chapters" or "mkv-extract-chapters")
        {
            var simple = MediaActions.Text(c, "format", "xml") != "xml";
            var args = new List<string> { current, "chapters" };
            if (simple) args.Add("--simple");
            var file = MediaActions.Output(directory, node, simple ? ".txt" : ".xml");
            var text = await tools.CheckedAsync("mkvextract", args, directory, ct);
            await File.WriteAllTextAsync(file, text, ct);
            return new(null, [file]);
        }
        var tracks = (id is "base-extract-track" or "mkv-extract-track") ? [int.Parse(MediaActions.Number(c, "trackId"))] : MediaActions.Ids(MediaActions.Text(c, "attachmentIds"));
        if (tracks.Length == 0)
        {
            var identification = await tools.CheckedAsync("mkvmerge", ["-J", current], directory, ct);
            using var json = JsonDocument.Parse(identification);
            tracks = json.RootElement.GetProperty("attachments").EnumerateArray().Select(a => a.GetProperty("id").GetInt32()).ToArray();
        }
        var args2 = new List<string> { current, (id is "base-extract-track" or "mkv-extract-track") ? "tracks" : "attachments" };
        var files = new List<string>();
        foreach (var track in tracks)
        {
            var result = MediaActions.Output(directory, node + track, ".bin");
            files.Add(result);
            args2.Add(track + ":" + result);
        }
        await tools.CheckedAsync("mkvextract", args2, directory, ct);
        return new(null, files);
    }

    private async Task<MediaActionOutcome> EditPropertiesAsync(
        string id, IReadOnlyDictionary<string, JsonElement> c, string current,
        string directory, string file, Func<string, Task<string>> extra, CancellationToken ct)
    {
        File.Copy(current, file, true);
        var args = new List<string> { file };
        if (id is "base-edit-matroska-metadata" or "mkv-edit-matroska-metadata")
            args.AddRange(["--edit", "info", "--set", "title=" + MediaActions.Text(c, "title")]);
        if (id is "base-edit-track-properties" or "mkv-edit-track-properties")
        {
            var raw = await tools.CheckedAsync("mkvmerge", ["-J", current], directory, ct);
            using var identification = JsonDocument.Parse(raw);
            var track = identification.RootElement.GetProperty("tracks").EnumerateArray().Single(t => t.GetProperty("id").GetInt32() == int.Parse(MediaActions.Number(c, "trackId")));
            args.AddRange(["--edit", "track:=" + track.GetProperty("properties").GetProperty("uid").GetUInt64()]);
            foreach (var pair in new[] { ("language", "language"), ("trackName", "name") })
                if (MediaActions.Text(c, pair.Item1) != "") args.AddRange(["--set", pair.Item2 + "=" + MediaActions.Text(c, pair.Item1)]);
            args.AddRange(["--set", "flag-default=" + (MediaActions.Flag(c, "defaultTrack") ? "1" : "0"), "--set", "flag-forced=" + (MediaActions.Flag(c, "forcedTrack") ? "1" : "0")]);
        }
        if (id is "base-replace-chapters" or "mkv-replace-chapters")
            args.AddRange(["--chapters", await extra(MediaActions.Text(c, "sourceFile"))]);
        if (id is "base-remove-attachments" or "mkv-remove-attachments")
        {
            var selected = MediaActions.Ids(MediaActions.Text(c, "attachmentIds"));
            if (selected.Length == 0)
            {
                await tools.CheckedAsync("mkvmerge", ["-o", file + ".next.mkv", "--no-attachments", current], directory, ct);
                File.Move(file + ".next.mkv", file, true);
                return new(file, []);
            }
            foreach (var attachment in selected) args.AddRange(["--delete-attachment", attachment.ToString()]);
        }
        await tools.CheckedAsync("mkvpropedit", args, directory, ct);
        return new(file, []);
    }

    private async Task<MediaActionOutcome> MergeAsync(
        string id, IReadOnlyDictionary<string, JsonElement> c, string current,
        string directory, string file, Func<string, Task<string>> extra, CancellationToken ct)
    {
        var merge = new List<string> { "-o", file };
        if (id is "base-reorder-tracks" or "mkv-reorder-tracks")
            merge.AddRange(["--track-order", string.Join(',', MediaActions.Ids(MediaActions.Text(c, "trackOrder")).Select(track => "0:" + track))]);
        if (id is "base-remux-matroska" or "base-remove-tracks" or "mkv-remux-matroska" or "mkv-remove-tracks")
        {
            var types = MediaActions.Text(c, "trackType", "all") == "all" ? new[] { "video", "audio", "subtitles" } : new[] { MediaActions.Text(c, "trackType") };
            var ids = MediaActions.Ids(MediaActions.Text(c, "trackIds"));
            if (MediaActions.Text(c, "language") != "")
            {
                var raw = await tools.CheckedAsync("mkvmerge", ["-J", current], directory, ct);
                using var identified = JsonDocument.Parse(raw);
                ids = identified.RootElement.GetProperty("tracks").EnumerateArray().Where(track => types.Contains(track.GetProperty("type").GetString())
                    && track.GetProperty("properties").TryGetProperty("language", out var language) && language.GetString() == MediaActions.Text(c, "language")
                    && (ids.Length == 0 || ids.Contains(track.GetProperty("id").GetInt32()))).Select(track => track.GetProperty("id").GetInt32()).ToArray();
                if (ids.Length == 0) throw new MediaToolException("sélection de pistes", 1);
            }
            if (ids.Length > 0) foreach (var type in types) merge.AddRange(["--" + (type == "subtitles" ? "subtitle" : type) + "-tracks",
                ((id is "base-remove-tracks" or "mkv-remove-tracks") ? "!" : "") + string.Join(',', ids)]);
            if ((id is "base-remove-tracks" or "mkv-remove-tracks") && ids.Length == 0) foreach (var type in types) merge.Add("--no-" + type);
            if (MediaActions.Text(c, "title") != "") merge.AddRange(["--title", MediaActions.Text(c, "title")]);
            if ((id is "base-remux-matroska" or "mkv-remux-matroska") && !MediaActions.Flag(c, "keepAttachments", true)) merge.Add("--no-attachments");
        }
        if (id is not ("base-remux-matroska" or "base-remove-tracks" or "base-reorder-tracks"
            or "base-add-audio-track" or "base-add-subtitle-track" or "mkv-remux-matroska"
            or "mkv-remove-tracks" or "mkv-reorder-tracks" or "audio-add-track" or "subtitles-add-track"))
            throw new IOException("Action média inconnue.");
        merge.Add(current);
        if (id is "base-add-audio-track" or "audio-add-track" or "base-add-subtitle-track" or "subtitles-add-track")
        {
            if (MediaActions.Text(c, "language") != "") merge.AddRange(["--language", "0:" + MediaActions.Text(c, "language")]);
            if (MediaActions.Text(c, "trackName") != "") merge.AddRange(["--track-name", "0:" + MediaActions.Text(c, "trackName")]);
            merge.AddRange([id is "base-add-audio-track" or "audio-add-track" ? "--default-track-flag" : "--forced-display-flag", "0:" + (MediaActions.Flag(c,
                id is "base-add-audio-track" or "audio-add-track" ? "defaultTrack" : "forcedTrack") ? "1" : "0")]);
            merge.Add(await extra(MediaActions.Text(c, "sourceFile")));
        }
        await tools.CheckedAsync("mkvmerge", merge, directory, ct);
        return new(file, []);
    }
}
