namespace Encoding.Api.Auth;

/// <summary>
/// Portées d’écriture d’un jeton API sur le MCP et les opérations métier. Sans portée, un jeton ne fait que lire,
/// quel que soit son rôle ; <c>mcp:write</c> couvre toutes les écritures, les autres sont limitées à un domaine.
/// </summary>
public static class ApiTokenScopes
{
    public const string ClaimType = "api_token_scopes";
    public const string AllWrites = "mcp:write";

    public static readonly IReadOnlyList<string> Allowed =
    [
        AllWrites, "libraries:write", "nodes:write", "flows:write", "executions:control", "plugins:write"
    ];

    /// <summary>Valide et normalise une liste de portées ; retourne la liste de portées inconnues le cas échéant.</summary>
    public static (IReadOnlyList<string> Scopes, IReadOnlyList<string> Unknown) Normalize(IEnumerable<string>? requested)
    {
        var scopes = new List<string>();
        var unknown = new List<string>();
        foreach (var raw in requested ?? [])
        {
            var scope = raw?.Trim().ToLowerInvariant() ?? string.Empty;
            if (scope.Length == 0) continue;
            if (!Allowed.Contains(scope)) unknown.Add(scope);
            else if (!scopes.Contains(scope)) scopes.Add(scope);
        }
        return (scopes, unknown);
    }

    public static string? Serialize(IReadOnlyList<string> scopes) => scopes.Count == 0 ? null : string.Join(',', scopes);

    public static IReadOnlyList<string> Parse(string? stored) =>
        string.IsNullOrWhiteSpace(stored) ? [] : stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Portée spécifique requise pour écrire dans un domaine du catalogue d’opérations ; <see cref="AllWrites"/> la remplace toujours.</summary>
    public static string RequiredFor(string domain) => domain switch
    {
        "libraries" => "libraries:write",
        "nodes-and-groups" => "nodes:write",
        "executions-and-logs" => "executions:control",
        "flows-and-components" or "flow-templates" or "component-packs" or "scripts" or "encoding-profiles" => "flows:write",
        "plugins-and-integrations" => "plugins:write",
        _ => AllWrites
    };
}
