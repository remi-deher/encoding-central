using System.Text.RegularExpressions;

namespace Encoding.Core.Logging;

/// <summary>
/// Service de masquage des données sensibles dans les journaux système (tokens, mots de passe, clés privées).
/// </summary>
public static partial class SystemLogSanitizer
{
    private const string Redacted = "***REDACTED***";
    private const string RedactedKey = "***REDACTED PRIVATE KEY***";

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9_\-\.]+", RegexOptions.Compiled)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"enc_(?:pat|sec|node)_[A-Za-z0-9]+", RegexOptions.Compiled)]
    private static partial Regex EncodingSecretTokenRegex();

    [GeneratedRegex(@"-----BEGIN [A-Z0-9_\- ]+PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9_\- ]+PRIVATE KEY-----", RegexOptions.Compiled)]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(@"(?i)(?:[""']?\b(password|passwd|pwd|secret|api[_-]?key|apikey|token|access_token|refresh_token|x-plex-token)\b[""']?)\s*([:=])\s*(""(?:\\""|[^""])*""|'[^']*'|[^\s""',;{}]+)",
        RegexOptions.Compiled)]
    private static partial Regex KeyValueSecretRegex();

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var result = PrivateKeyRegex().Replace(text, RedactedKey);
        result = BearerTokenRegex().Replace(result, "Bearer " + Redacted);
        result = EncodingSecretTokenRegex().Replace(result, Redacted);
        result = KeyValueSecretRegex().Replace(result, match =>
        {
            var keyMatch = match.Groups[1].Value;
            var delimiter = match.Groups[2].Value;
            var originalVal = match.Groups[3].Value;
            var quotes = originalVal.StartsWith('"') ? "\"" : (originalVal.StartsWith('\'') ? "'" : string.Empty);
            var hadKeyQuotes = match.Value.StartsWith('"') ? "\"" : (match.Value.StartsWith('\'') ? "'" : string.Empty);
            return $"{hadKeyQuotes}{keyMatch}{hadKeyQuotes}{delimiter} {quotes}{Redacted}{quotes}";
        });

        return result;
    }

    public static string? SanitizeException(Exception? exception)
    {
        if (exception is null)
            return null;

        var formatted = $"{exception.GetType().FullName}: {Sanitize(exception.Message)}";
        if (!string.IsNullOrWhiteSpace(exception.StackTrace))
        {
            formatted += Environment.NewLine + Sanitize(exception.StackTrace);
        }

        if (exception.InnerException is not null)
        {
            formatted += Environment.NewLine + " ---> " + SanitizeException(exception.InnerException);
        }

        return formatted;
    }
}
