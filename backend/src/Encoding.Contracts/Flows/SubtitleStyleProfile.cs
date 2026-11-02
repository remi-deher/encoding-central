using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Encoding.Contracts.Flows;

/// <summary>Un style ASS V4+, mesuré sur le canevas de référence du profil.</summary>
public sealed record SubtitleStyle(
    string Name,
    string FontName,
    double FontSize,
    string PrimaryColour,
    string SecondaryColour,
    string OutlineColour,
    string BackColour,
    int Bold,
    int Italic,
    int Underline,
    int StrikeOut,
    double ScaleX,
    double ScaleY,
    double Spacing,
    double Angle,
    int BorderStyle,
    double Outline,
    double Shadow,
    int Alignment,
    double MarginL,
    double MarginR,
    double MarginV,
    int Encoding);

/// <summary>
/// Document de profil de sous-titres (ressource <c>subtitle-profile</c>). Le moteur n'a pas de styles en propre :
/// il met ce document à l'échelle du cadre affiché.
/// </summary>
public sealed record SubtitleStyleProfile(
    string ProfileVersion,
    int ReferenceWidth,
    int ReferenceHeight,
    string Title,
    int WrapStyle,
    bool ScaledBorderAndShadow,
    string Matrix,
    IReadOnlyList<SubtitleStyle> Styles)
{
    private static readonly Regex Colour = new("^&H[0-9A-Fa-f]{8}$", RegexOptions.CultureInvariant);
    private static readonly Regex NamePattern = new("^[A-Za-z0-9][A-Za-z0-9 _.-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,15}$", RegexOptions.CultureInvariant);
    private static readonly Regex MatrixPattern = new("^[A-Za-z0-9. ]{1,32}$", RegexOptions.CultureInvariant);

    public static SubtitleStyleProfile Parse(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { throw new SubtitleException("Le profil de sous-titres n'est pas un JSON valide."); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new SubtitleException("Le profil de sous-titres doit être un objet JSON.");
            if (!root.TryGetProperty("formatVersion", out var format) || format.ValueKind != JsonValueKind.Number || format.GetInt32() != 1)
                throw new SubtitleException("Version de profil non prise en charge (formatVersion 1 attendu).");
            var version = Text(root, "profileVersion") ?? throw new SubtitleException("Le profil doit nommer profileVersion.");
            if (!VersionPattern.IsMatch(version)) throw new SubtitleException("profileVersion contient 1 à 16 caractères (lettres, chiffres, point, tiret).");
            if (!root.TryGetProperty("reference", out var reference) || reference.ValueKind != JsonValueKind.Object)
                throw new SubtitleException("Le profil doit indiquer son canevas de référence.");
            var width = Int(reference, "width");
            var height = Int(reference, "height");
            if (width is < 16 or > 8192 || height is < 16 or > 8192)
                throw new SubtitleException("Le canevas de référence doit être compris entre 16 et 8192 pixels.");
            var script = root.TryGetProperty("script", out var scriptElement) && scriptElement.ValueKind == JsonValueKind.Object ? scriptElement : default;
            var title = script.ValueKind == JsonValueKind.Object ? Text(script, "title") ?? "New subtitles" : "New subtitles";
            if (title.Length is < 1 or > 128 || title.IndexOfAny(['\r', '\n']) >= 0)
                throw new SubtitleException("Le titre du script contient entre 1 et 128 caractères, sans saut de ligne.");
            var wrap = script.ValueKind == JsonValueKind.Object && script.TryGetProperty("wrapStyle", out _) ? Int(script, "wrapStyle") : 0;
            if (wrap is < 0 or > 3) throw new SubtitleException("WrapStyle doit être 0, 1, 2 ou 3.");
            var scaled = script.ValueKind != JsonValueKind.Object || !script.TryGetProperty("scaledBorderAndShadow", out var scaledValue) || scaledValue.ValueKind != JsonValueKind.False;
            var matrix = script.ValueKind == JsonValueKind.Object ? Text(script, "ycbcrMatrix") ?? "TV.601" : "TV.601";
            if (!MatrixPattern.IsMatch(matrix)) throw new SubtitleException("La matrice YCbCr du profil est invalide.");
            if (!root.TryGetProperty("styles", out var styles) || styles.ValueKind != JsonValueKind.Array || styles.GetArrayLength() is < 1 or > 32)
                throw new SubtitleException("Le profil contient entre 1 et 32 styles.");
            var parsed = new List<SubtitleStyle>();
            foreach (var style in styles.EnumerateArray()) parsed.Add(ReadStyle(style));
            if (parsed.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != parsed.Count)
                throw new SubtitleException("Deux styles portent le même nom.");
            return new(version, width, height, title, wrap, scaled, matrix, parsed);
        }
    }

    private static SubtitleStyle ReadStyle(JsonElement style)
    {
        if (style.ValueKind != JsonValueKind.Object) throw new SubtitleException("Un style du profil doit être un objet.");
        var name = Text(style, "name") ?? throw new SubtitleException("Un style doit avoir un nom.");
        var font = Text(style, "fontName") ?? throw new SubtitleException($"Le style « {name} » doit nommer une police.");
        if (!NamePattern.IsMatch(name) || font.Length is < 1 or > 128 || font.IndexOfAny([',', '\r', '\n']) >= 0)
            throw new SubtitleException($"Nom ou police invalide pour le style « {name} ».");
        var primary = ColourOf(style, "primaryColour", name);
        var secondary = ColourOf(style, "secondaryColour", name);
        var outline = ColourOf(style, "outlineColour", name);
        var back = ColourOf(style, "backColour", name);
        var bold = Flag(style, "bold", name);
        var italic = Flag(style, "italic", name);
        var underline = Flag(style, "underline", name);
        var strike = Flag(style, "strikeOut", name);
        var scaleX = Number(style, "scaleX", 100, 1, 1000, name);
        var scaleY = Number(style, "scaleY", 100, 1, 1000, name);
        var spacing = Number(style, "spacing", 0, -50, 50, name);
        var angle = Number(style, "angle", 0, -360, 360, name);
        var border = (int)Number(style, "borderStyle", 1, 1, 3, name);
        if (border is not (1 or 3)) throw new SubtitleException($"Le style de bordure de « {name} » est 1 ou 3.");
        if (!style.TryGetProperty("fontSize", out _)) throw new SubtitleException($"« fontSize » absent pour « {name} ».");
        var fontSize = Number(style, "fontSize", 0, 1, 400, name);
        var outlineSize = Number(style, "outline", 0, 0, 50, name);
        var shadow = Number(style, "shadow", 0, 0, 50, name);
        var alignment = (int)Number(style, "alignment", 2, 1, 9, name);
        var marginL = Number(style, "marginL", 0, 0, 500, name);
        var marginR = Number(style, "marginR", 0, 0, 500, name);
        var marginV = Number(style, "marginV", 0, 0, 500, name);
        var encoding = (int)Number(style, "encoding", 1, 0, 255, name);
        return new(name, font, fontSize, primary, secondary, outline, back, bold, italic, underline, strike, scaleX, scaleY, spacing, angle, border, outlineSize, shadow, alignment, marginL, marginR, marginV, encoding);
    }

    private static string ColourOf(JsonElement style, string key, string name)
    {
        var value = Text(style, key) ?? throw new SubtitleException($"Couleur « {key} » absente pour « {name} ».");
        if (!Colour.IsMatch(value)) throw new SubtitleException($"Couleur « {key} » de « {name} » : attendu &H puis 8 chiffres hexadécimaux.");
        return value;
    }

    private static int Flag(JsonElement style, string key, string name)
    {
        var value = (int)Number(style, key, 0, -1, 1, name);
        if (value is not (-1 or 0 or 1)) throw new SubtitleException($"« {key} » de « {name} » vaut -1, 0 ou 1.");
        return value;
    }

    private static double Number(JsonElement style, string key, double fallback, double min, double max, string name)
    {
        if (!style.TryGetProperty(key, out var value)) return fallback;
        var number = value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed) ? parsed : double.NaN;
        if (double.IsNaN(number) || number < min || number > max)
            throw new SubtitleException($"« {key} » de « {name} » est hors limites ({min.ToString(CultureInfo.InvariantCulture)} à {max.ToString(CultureInfo.InvariantCulture)}).");
        return number;
    }

    private static int Int(JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new SubtitleException($"Le champ « {key} » du profil doit être un entier.");
        return number;
    }

    private static string? Text(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;
}
