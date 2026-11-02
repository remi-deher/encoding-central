using System.Security.Claims;
using Encoding.Api.Auth;

namespace Encoding.Api.Mcp;

/// <summary>
/// Droits d’écriture sur le MCP et les opérations métier. Une session (ou l’accès local sans authentification) écrit selon son rôle ;
/// un jeton API administrateur ne lit que tant qu’il ne porte pas la portée d’écriture du domaine (ou <c>mcp:write</c>).
/// </summary>
public static class McpAccess
{
    private const string TokenAuthType = "api_token";

    public static bool IsApiToken(ClaimsPrincipal user) => user.FindFirstValue("auth_type") == TokenAuthType;

    /// <summary>Portée manquante pour cette opération d’écriture, ou null si l’appel peut aller au contrôle de rôle habituel.</summary>
    public static string? MissingScope(ClaimsPrincipal user, BusinessOperationDescriptor operation)
    {
        if (operation.ReadOnly || !user.IsInRole("admin") || !IsApiToken(user)) return null;
        var granted = ApiTokenScopes.Parse(user.FindFirstValue(ApiTokenScopes.ClaimType));
        var required = ApiTokenScopes.RequiredFor(operation.Domain);
        return granted.Contains(ApiTokenScopes.AllWrites) || granted.Contains(required) ? null : required;
    }

    public static string MissingScopeMessage(string required) =>
        $"Ce jeton API est en lecture seule pour cette opération : ajoutez la portée « {required} » (ou « {ApiTokenScopes.AllWrites} ») à un jeton dédié.";
}
