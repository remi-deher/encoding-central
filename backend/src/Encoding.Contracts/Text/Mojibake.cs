using System.Text;

namespace Encoding.Contracts.Text;

/// <summary>
/// Répare le texte UTF-8 qui a été relu à tort en Windows-1252/Latin-1 puis réencodé (« vidÃ©o » au lieu de « vidéo »).
/// Un client Windows qui lit un fichier JSON sans préciser UTF-8 produit exactement ce défaut.
/// </summary>
public static class Mojibake
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // Octets 0x80-0x9F de Windows-1252 qui ne sont pas des caractères de contrôle Latin-1.
    private static readonly Dictionary<char, byte> Cp1252 = new()
    {
        ['€'] = 0x80, ['‚'] = 0x82, ['ƒ'] = 0x83, ['„'] = 0x84, ['…'] = 0x85, ['†'] = 0x86, ['‡'] = 0x87, ['ˆ'] = 0x88,
        ['‰'] = 0x89, ['Š'] = 0x8A, ['‹'] = 0x8B, ['Œ'] = 0x8C, ['Ž'] = 0x8E, ['‘'] = 0x91, ['’'] = 0x92, ['“'] = 0x93,
        ['”'] = 0x94, ['•'] = 0x95, ['–'] = 0x96, ['—'] = 0x97, ['˜'] = 0x98, ['™'] = 0x99, ['š'] = 0x9A, ['›'] = 0x9B,
        ['œ'] = 0x9C, ['ž'] = 0x9E, ['Ÿ'] = 0x9F,
    };

    /// <summary>Retourne le texte corrigé, ou le texte d'origine s'il n'est pas du mojibake (jamais d'exception).</summary>
    public static string? Repair(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var current = text;
        // Un double encodage est possible : on répare tant que le résultat reste valide.
        for (var pass = 0; pass < 3; pass++)
        {
            var repaired = RepairOnce(current);
            if (repaired is null) break;
            current = repaired;
        }
        return current;
    }

    private static string? RepairOnce(string text)
    {
        var hasLead = false;
        foreach (var c in text)
        {
            if (c is 'Ã' or 'Â' or 'â' or 'Å' or 'Ð' or 'Ñ' or 'ð') hasLead = true;
            else if (c > 0xFF && !Cp1252.ContainsKey(c)) return null;
        }
        if (!hasLead) return null;
        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c <= 0xFF) bytes[i] = (byte)c;
            else bytes[i] = Cp1252[c];
        }
        try
        {
            var repaired = StrictUtf8.GetString(bytes);
            return repaired.Length < text.Length ? repaired : null;
        }
        catch (DecoderFallbackException) { return null; }
    }
}
