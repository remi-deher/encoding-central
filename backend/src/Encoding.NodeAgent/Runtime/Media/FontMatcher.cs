using System.Buffers.Binary;
using System.Text;

namespace Encoding.NodeAgent.Runtime.Media;

/// <summary>
/// Descripteur d'identité d'une police pour la déduplication lors de l'ajout en pièce jointe (ADR 0006).
/// Compare d'abord la famille et le style lus dans la table SFNT « name » du TTF/OTF/TTC,
/// puis retombe sur le nom de fichier normalisé (sans casse, sans extension, sans séparateurs).
/// </summary>
public sealed record FontDescriptor(
    string FileName,
    string? Family,
    string? Style,
    string? FullName,
    IReadOnlySet<string> MatchKeys,
    long Size = 0);

public static class FontMatcher
{
    private static readonly char[] Separators = [' ', '-', '_', '.', ',', '(', ')', '\t', '\r', '\n'];

    /// <summary>
    /// Normalise une chaîne de texte (nom de police, famille, style ou nom de fichier sans extension) :
    /// supprime tous les séparateurs, espaces, tirets et ponctuation, et convertit en minuscules invariantes.
    /// Exemples : « Arial Bold » -> « arialbold », « Arial-Bold » -> « arialbold », « ArialBold » -> « arialbold ».
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Crée un descripteur de police uniquement à partir de son nom de fichier (repli sans métadonnées binaires).
    /// </summary>
    public static FontDescriptor FromFileName(string fileName, long size = 0)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var key = Normalize(baseName);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (key.Length > 0) keys.Add(key);
        return new(fileName, null, null, null, keys, size);
    }

    /// <summary>
    /// Lit une police sur le disque (TTF, OTF, TTC). En cas d'impossibilité de lecture ou format non reconnu,
    /// retombe immédiatement sur la normalisation du nom de fichier sans jamais lever d'exception.
    /// </summary>
    public static FontDescriptor Read(string filePath, string? explicitFileName = null, long explicitSize = 0)
    {
        var fileName = explicitFileName ?? Path.GetFileName(filePath);
        var size = explicitSize > 0 ? explicitSize : (File.Exists(filePath) ? new FileInfo(filePath).Length : 0);
        try
        {
            if (File.Exists(filePath))
            {
                var bytes = File.ReadAllBytes(filePath);
                var parsed = ParseFontBytes(bytes, fileName, size);
                if (parsed is not null) return parsed;
            }
        }
        catch
        {
            // Fallback silencieux sur le nom de fichier normalisé
        }
        return FromFileName(fileName, size);
    }

    /// <summary>
    /// Vérifie si deux descripteurs de police représentent la même police (intersection de leurs clés normalisées).
    /// </summary>
    public static bool IsEquivalent(FontDescriptor a, FontDescriptor b) =>
        a.MatchKeys.Overlaps(b.MatchKeys);

    /// <summary>
    /// Tente d'extraire la famille, le style et le nom complet depuis la table « name » du binaire SFNT.
    /// Supporte les polices TrueType (0x00010000, 'true'), OpenType ('OTTO') et les collections TTC ('ttcf').
    /// </summary>
    public static FontDescriptor? ParseFontBytes(byte[] bytes, string fileName, long size = 0)
    {
        if (bytes.Length < 12) return null;
        try
        {
            var magic = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4));
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var baseName = Normalize(Path.GetFileNameWithoutExtension(fileName));
            if (baseName.Length > 0) keys.Add(baseName);

            string? primaryFamily = null;
            string? primaryStyle = null;
            string? primaryFullName = null;

            if (magic == 0x74746366) // "ttcf" - TrueType Collection
            {
                var numFonts = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4));
                var limit = Math.Clamp(numFonts, 1, 32);
                for (var f = 0; f < limit; f++)
                {
                    var offsetPos = 12 + f * 4;
                    if (bytes.Length < offsetPos + 4) break;
                    var sfntOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offsetPos, 4));
                    if (ExtractNames(bytes, sfntOffset, out var fam, out var sty, out var full))
                    {
                        if (primaryFamily is null) { primaryFamily = fam; primaryStyle = sty; primaryFullName = full; }
                        AddDescriptorKeys(keys, fam, sty, full);
                    }
                }
            }
            else if (magic is 0x00010000 or 0x4F54544F or 0x74727565) // TrueType 1.0, "OTTO", "true"
            {
                if (ExtractNames(bytes, 0, out var fam, out var sty, out var full))
                {
                    primaryFamily = fam;
                    primaryStyle = sty;
                    primaryFullName = full;
                    AddDescriptorKeys(keys, fam, sty, full);
                }
            }
            else
            {
                return null;
            }

            return new FontDescriptor(fileName, primaryFamily, primaryStyle, primaryFullName, keys, size);
        }
        catch
        {
            return null;
        }
    }

    private static void AddDescriptorKeys(HashSet<string> keys, string? family, string? style, string? fullName)
    {
        if (!string.IsNullOrWhiteSpace(fullName))
        {
            var normFull = Normalize(fullName);
            if (normFull.Length > 0) keys.Add(normFull);
        }

        if (!string.IsNullOrWhiteSpace(family))
        {
            var normFam = Normalize(family);
            var normSty = !string.IsNullOrWhiteSpace(style) ? Normalize(style) : string.Empty;
            if (normSty.Length > 0)
            {
                keys.Add(normFam + normSty);
            }
            if (IsRegular(style))
            {
                keys.Add(normFam);
                keys.Add(normFam + "regular");
            }
        }
    }

    private static bool IsRegular(string? style)
    {
        if (string.IsNullOrWhiteSpace(style)) return true;
        var norm = Normalize(style);
        return norm is "" or "regular" or "normal" or "book" or "roman" or "standard";
    }

    private static bool ExtractNames(byte[] bytes, int sfntOffset, out string? family, out string? style, out string? fullName)
    {
        family = null;
        style = null;
        fullName = null;

        if (bytes.Length < sfntOffset + 12) return false;
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(sfntOffset + 4, 2));
        if (bytes.Length < sfntOffset + 12 + numTables * 16) return false;

        var nameTableOffset = 0;
        for (var i = 0; i < numTables; i++)
        {
            var rec = sfntOffset + 12 + i * 16;
            var tableTag = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(rec, 4));
            if (tableTag == 0x6E616D65) // "name"
            {
                nameTableOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(rec + 8, 4));
                break;
            }
        }

        if (nameTableOffset <= 0 || bytes.Length < nameTableOffset + 6) return false;
        var count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(nameTableOffset + 2, 2));
        var stringOffset = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(nameTableOffset + 4, 2));
        var storageOffset = nameTableOffset + stringOffset;
        if (bytes.Length < storageOffset) return false;

        string? typFamily = null;
        string? typStyle = null;

        var recordLimit = Math.Clamp((int)count, 0, 512);
        for (var i = 0; i < recordLimit; i++)
        {
            var rec = nameTableOffset + 6 + i * 12;
            if (bytes.Length < rec + 12) break;

            var platformId = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(rec, 2));
            var langId = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(rec + 4, 2));
            var nameId = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(rec + 6, 2));
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(rec + 8, 2));
            var strRecOffset = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(rec + 10, 2));
            var actualOffset = storageOffset + strRecOffset;

            if (actualOffset < 0 || bytes.Length < actualOffset + length) continue;

            string? val = null;
            if (platformId is 3 or 0)
            {
                val = System.Text.Encoding.BigEndianUnicode.GetString(bytes, actualOffset, length);
            }
            else if (platformId == 1)
            {
                val = System.Text.Encoding.Latin1.GetString(bytes, actualOffset, length);
            }

            if (string.IsNullOrWhiteSpace(val)) continue;
            val = val.Trim().Trim('\0');
            if (val.Length == 0) continue;

            var isEnglish = langId == 0x0409;
            switch (nameId)
            {
                case 1: if (family is null || isEnglish) family = val; break;
                case 2: if (style is null || isEnglish) style = val; break;
                case 4: if (fullName is null || isEnglish) fullName = val; break;
                case 16: if (typFamily is null || isEnglish) typFamily = val; break;
                case 17: if (typStyle is null || isEnglish) typStyle = val; break;
            }
        }

        family = typFamily ?? family;
        style = typStyle ?? style;
        return family is not null || fullName is not null;
    }
}
