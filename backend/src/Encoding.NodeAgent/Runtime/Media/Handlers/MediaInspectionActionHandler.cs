using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.NodeAgent.Services;

namespace Encoding.NodeAgent.Runtime.Media.Handlers;

public sealed class MediaInspectionActionHandler(IProcessToolRunner tools, MediaScanner scanner) : IMediaActionHandler
{
    public bool CanHandle(string actionId) =>
        actionId.StartsWith("base-probe-", StringComparison.Ordinal) ||
        actionId.StartsWith("probe-", StringComparison.Ordinal) ||
        actionId.StartsWith("base-inspect-", StringComparison.Ordinal) ||
        actionId.StartsWith("probe-inspect-", StringComparison.Ordinal) ||
        actionId is "base-export-media-report" or "probe-export-report";

    public async Task<MediaActionOutcome> ExecuteAsync(MediaActionContext context, CancellationToken ct)
    {
        var id = context.Id;
        if (id.StartsWith("base-probe-", StringComparison.Ordinal) || id.StartsWith("probe-", StringComparison.Ordinal))
        {
            if (id == "probe-validate-output")
                throw new InvalidOperationException("Le validateur de sortie doit recevoir les métadonnées source et résultat.");
            var metadata = await scanner.ScanAsync(context.Current, ct);
            var value = FlowMediaValues.Probe(id, metadata, int.Parse(MediaActions.Number(context.Config, "trackId")));
            return new(null, [], value);
        }

        var raw = await tools.CheckedAsync("mediainfo", ["--Output=JSON", context.Current], context.Directory, ct);
        using var json = JsonDocument.Parse(raw);
        var valueElement = json.RootElement.Clone();
        if (id is "base-export-media-report" or "probe-export-report")
        {
            var file = MediaActions.Output(context.Directory, context.Node, ".json");
            await File.WriteAllTextAsync(file, raw, ct);
            return new(null, [file], valueElement);
        }
        if (id is not ("base-inspect-media" or "probe-inspect-media") && valueElement.TryGetProperty("media", out var media) && media.TryGetProperty("track", out var tracks))
            valueElement = JsonSerializer.SerializeToElement(tracks.EnumerateArray().Where(track => id is not ("base-inspect-hdr" or "probe-inspect-hdr") || track.TryGetProperty("@type",
                out var type) && type.GetString() == "Video").Skip(int.Parse(MediaActions.Number(context.Config, "trackId"))).Take(1));
        return new(null, [], valueElement);
    }
}
