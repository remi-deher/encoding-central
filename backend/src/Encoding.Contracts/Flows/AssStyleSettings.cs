using System.Text.Json;

namespace Encoding.Contracts.Flows;

/// <summary>Style snapshot stored in the immutable flow version; measurements reference 360 lines.</summary>
public sealed record AssStyleSettings(string FontFamily = "Trebuchet MS", double FontSize = 23, double Outline = 2.5, double Margin = 20)
{
    public static AssStyleSettings From(IReadOnlyDictionary<string, JsonElement> values)
    {
        string Text(string key, string fallback) => values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
        double Number(string key, double fallback) => values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : fallback;
        var settings = new AssStyleSettings(Text("fontFamily", "Trebuchet MS"), Number("fontSize", 23), Number("fontOutline", 2.5), Number("fontMargin", 20));
        if (settings.FontFamily.Length is < 1 or > 128 || settings.FontFamily.IndexOfAny([',', '\r', '\n']) >= 0
            || settings.FontSize is < 6 or > 80 || settings.Outline is < 0 or > 10 || settings.Margin is < 0 or > 100)
            throw new SubtitleException("Profil de style ASS invalide.");
        return settings;
    }
}
