using System.Text.Json;
using System.Text.RegularExpressions;

namespace Encoding.WorkerAgent.Runtime;

/// <summary>Lit le code de motif court que le central joint à un 409 de <c>steps/start</c> et <c>steps/complete</c> (<c>{"reason":"…"}</c>).</summary>
internal static partial class RuntimeRefusalReason
{
    private const string Marker = "Content: ";

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex Code();

    /// <summary>Le code du motif, ou <c>null</c> si le central n'en a pas fourni (ancienne version, corps vide ou illisible).</summary>
    public static string? From(HttpRequestException exception)
    {
        var index = exception.Message.IndexOf(Marker, StringComparison.Ordinal);
        if (index < 0) return null;
        try
        {
            using var document = JsonDocument.Parse(exception.Message[(index + Marker.Length)..]);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
                && reason.GetString() is { } code && Code().IsMatch(code) ? code : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Message d'échec <c>cluster_refused</c> : le motif du central y figure s'il est connu.</summary>
    public static string Describe(HttpRequestException exception, string text) =>
        From(exception) is { } code ? $"cluster_refused : {text} (motif : {code})" : $"cluster_refused : {text}";
}
