using System.Text.Json;

namespace Encoding.Api.Plugins;

public enum ArrWebhookKind { Test, Import, Ignored }

/// <summary>Événement de webhook Sonarr/Radarr réduit à ce que le déclencheur utilise : les chemins importés, vus par le service.</summary>
public sealed record ArrWebhookEvent(ArrWebhookKind Kind, IReadOnlyList<string> RemotePaths)
{
    private const int MaxPaths = 500;

    public static ArrWebhookEvent Parse(string pluginId, JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("eventType", out var type) || type.ValueKind != JsonValueKind.String)
            throw new FormatException("Le corps ne ressemble pas à un événement Sonarr ou Radarr.");
        switch (type.GetString())
        {
            case "Test":
                return new(ArrWebhookKind.Test, []);
            case "Download":
                var (fileProperty, listProperty, folderProperty) = pluginId == "sonarr"
                    ? ("episodeFile", "episodeFiles", "series")
                    : ("movieFile", "movieFiles", "movie");
                var folder = body.TryGetProperty(folderProperty, out var owner) && owner.ValueKind == JsonValueKind.Object
                    ? Text(owner, pluginId == "sonarr" ? "path" : "folderPath") : null;
                var files = new List<JsonElement>();
                if (body.TryGetProperty(fileProperty, out var single) && single.ValueKind == JsonValueKind.Object) files.Add(single);
                if (body.TryGetProperty(listProperty, out var list) && list.ValueKind == JsonValueKind.Array)
                    files.AddRange(list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object));
                var paths = files.Select(file => Text(file, "path") ?? (folder is not null && Text(file, "relativePath") is { } relative
                        ? folder.TrimEnd('/', '\\') + "/" + relative : null))
                    .Where(path => path is not null).Select(path => path!).Distinct(StringComparer.Ordinal).Take(MaxPaths).ToArray();
                return new(paths.Length == 0 ? ArrWebhookKind.Ignored : ArrWebhookKind.Import, paths);
            default:
                return new(ArrWebhookKind.Ignored, []);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString() : null;
}
