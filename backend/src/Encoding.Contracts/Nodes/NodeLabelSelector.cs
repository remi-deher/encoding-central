namespace Encoding.Contracts.Nodes;

/// <summary>Comma-separated label predicates are conjunctive; a bare key checks presence.</summary>
public static class NodeLabelSelector
{
    public static bool IsValid(string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector) || selector.Length > 1024) return false;
        var conditions = selector.Split(',', StringSplitOptions.TrimEntries);
        return conditions.Length <= 64 && conditions.All(condition =>
        {
            var separator = condition.IndexOf('=');
            var key = separator < 0 ? condition : condition[..separator];
            return System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-zA-Z][a-zA-Z0-9_.-]{0,63}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                && (separator < 0 || condition.Length - separator - 1 <= 256);
        });
    }

    public static bool Matches(string? selector, IReadOnlyDictionary<string, string?>? labels)
    {
        if (string.IsNullOrWhiteSpace(selector)) return true;
        if (labels is null) return false;
        foreach (var condition in selector.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = condition.IndexOf('=');
            var key = separator < 0 ? condition : condition[..separator];
            if (!labels.TryGetValue(key, out var value)
                || (separator >= 0 && !string.Equals(value, condition[(separator + 1)..], StringComparison.Ordinal))) return false;
        }
        return true;
    }
}
