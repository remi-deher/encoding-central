namespace Encoding.Contracts.Plugins;

/// <summary>Règles d'identité des plugins et composants tiers (identifiant stable, utilisé dans des chemins et des capacités).</summary>
public static class PluginIdentity
{
    private static readonly System.Text.RegularExpressions.Regex PluginId =
        new(@"^[a-z0-9]+(-[a-z0-9]+)*(\.[a-z0-9]+(-[a-z0-9]+)*)?$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex ComponentId =
        new(@"^[a-z0-9]+(-[a-z0-9]+)*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Identifiant qualifié d'un paquet du Store : « espace.nom » (0063), « official » étant réservé au dépôt officiel.</summary>
    public static bool IsQualifiedPluginId(string id) => IsValidPluginId(id) && id.Contains('.');
    public static bool IsOfficialNamespace(string id) => id.StartsWith("official.", StringComparison.Ordinal);

    public static bool IsValidPluginId(string id) => id.Length <= 64 && PluginId.IsMatch(id);
    public static bool IsValidComponentId(string? id) => id is { Length: <= 96 } && ComponentId.IsMatch(id);
}
