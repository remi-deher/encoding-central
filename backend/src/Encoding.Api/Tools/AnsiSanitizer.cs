using System.Text.RegularExpressions;

namespace Encoding.Api.Tools;

/// <summary>
/// Nettoie les séquences d'échappement ANSI, les retours chariots et les codes de contrôle
/// issus des affichages consoles des CLIs pour ne conserver que le texte utile.
/// </summary>
public static partial class AnsiSanitizer
{
    private static readonly Regex AnsiPattern = new(
        @"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CarriageReturnPattern = new(
        @"\r+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] RateLimitKeywords =
    [
        "rate limit", "too many requests", "429", "quota exceeded",
        "try again in", "usage limit reached", "hit your limit", "temporarily blocked"
    ];

    public static string Sanitize(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var stripped = AnsiPattern.Replace(input, string.Empty);
        stripped = CarriageReturnPattern.Replace(stripped, string.Empty);
        return stripped.Trim();
    }

    public static bool DetectRateLimit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lower = text.ToLowerInvariant();
        return RateLimitKeywords.Any(keyword => lower.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }
}
