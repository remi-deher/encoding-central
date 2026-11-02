using Encoding.Api.Nodes;
using Microsoft.AspNetCore.Authorization;

namespace Encoding.Api.Auth;

/// <summary>Marque une opération en POST qui ne modifie aucun état du cluster : le rôle lecture peut l’appeler.</summary>
public sealed record ViewerAccessMetadata;

/// <summary>Autorise un viewer à appeler une route d’assistant qui ne peut agir que sur ses conversations privées.</summary>
public sealed record AssistantConversationAccessMetadata;

/// <summary>Marque une lecture appelée aussi par les workers, authentifiés par leur jeton de nœud plutôt que par une session.</summary>
public sealed record NodeCredentialAccessMetadata;

public static class ManagementAccessExtensions
{
    public static TBuilder AllowViewer<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new ViewerAccessMetadata());

    public static TBuilder AllowAssistantConversation<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AssistantConversationAccessMetadata());

    public static TBuilder AllowNodeCredentials<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new NodeCredentialAccessMetadata());
}

public sealed class ManagementAccessRequirement : IAuthorizationRequirement;

/// <summary>
/// Règle unique d’accès à l’API de gestion : toute route exige une session ; lecture pour tout rôle,
/// modification réservée aux administrateurs et protégée contre les requêtes inter-sites.
/// </summary>
public sealed class ManagementAccessHandler(NodeRepository nodes) : AuthorizationHandler<ManagementAccessRequirement>
{
    public const string CsrfHeader = "X-Encoding-Csrf";

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ManagementAccessRequirement requirement)
    {
        if (context.Resource is not HttpContext http) return;
        var endpoint = http.GetEndpoint();
        var safe = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method);

        if (safe && endpoint?.Metadata.GetMetadata<NodeCredentialAccessMetadata>() is not null
            && http.Request.Headers["X-Node-Token"].ToString() is { Length: > 0 } token
            && await nodes.AuthenticateTokenAsync(token, http.RequestAborted))
        {
            context.Succeed(requirement);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true) return;
        if (safe)
        {
            context.Succeed(requirement);
            return;
        }

        // Le cookie SameSite=Strict est la première défense ; l’en-tête personnalisé impose en plus une requête same-origin.
        // Les requêtes M2M authentifiées par jeton API (Bearer) ne sont pas vulnérables aux attaques CSRF de navigateur.
        var isApiToken = context.User.FindFirst("auth_type")?.Value == "api_token";
        if (!isApiToken && http.Request.Headers[CsrfHeader] != "1") return;
        if (context.User.IsInRole("admin")
            || endpoint?.Metadata.GetMetadata<ViewerAccessMetadata>() is not null
            || endpoint?.Metadata.GetMetadata<AssistantConversationAccessMetadata>() is not null)
            context.Succeed(requirement);
    }
}

