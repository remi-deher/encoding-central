using Encoding.Api.Workers;
using Microsoft.AspNetCore.Authorization;

namespace Encoding.Api.Auth;

/// <summary>Marque une opération en POST qui ne modifie aucun état du central : le rôle lecture peut l’appeler.</summary>
public sealed record ViewerAccessMetadata;

/// <summary>Autorise un viewer à appeler une route d’assistant qui ne peut agir que sur ses conversations privées.</summary>
public sealed record AssistantConversationAccessMetadata;

/// <summary>Marque une lecture appelée aussi par les workers, authentifiés par leur jeton de worker plutôt que par une session.</summary>
public sealed record WorkerCredentialAccessMetadata;

/// <summary>
/// Étend <see cref="WorkerCredentialAccessMetadata"/> à l'écriture pour une route précise où un worker remet un rapport sur LUI-MÊME
/// (identité déduite du jeton par la route, jamais d'identifiant fourni). Ne s'emploie qu'avec une décision explicite (voir auth-ops).
/// </summary>
public sealed record WorkerReportAccessMetadata;

public static class ManagementAccessExtensions
{
    public static TBuilder AllowViewer<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new ViewerAccessMetadata());

    public static TBuilder AllowAssistantConversation<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AssistantConversationAccessMetadata());

    public static TBuilder AllowNodeCredentials<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new WorkerCredentialAccessMetadata());

    /// <summary>Rapport d'un worker sur son propre état (écriture) : jeton worker accepté pour cette route seulement, hors session et hors CSRF.</summary>
    public static TBuilder AllowNodeCredentialReport<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new WorkerCredentialAccessMetadata(), new WorkerReportAccessMetadata());
}

public sealed class ManagementAccessRequirement : IAuthorizationRequirement;

/// <summary>
/// Règle unique d’accès à l’API de gestion : toute route exige une session ; lecture pour tout rôle,
/// modification réservée aux administrateurs et protégée contre les requêtes inter-sites.
/// </summary>
public sealed class ManagementAccessHandler(WorkerRepository workers) : AuthorizationHandler<ManagementAccessRequirement>
{
    public const string CsrfHeader = "X-Encoding-Csrf";

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ManagementAccessRequirement requirement)
    {
        if (context.Resource is not HttpContext http) return;
        var endpoint = http.GetEndpoint();
        var safe = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method);

        // Jeton worker : lectures marquées AllowNodeCredentials ; écriture seulement si la route porte en plus WorkerReportAccessMetadata
        // (AllowNodeCredentialReport). Un jeton worker n'ouvre jamais une autre route d'écriture. Pas de CSRF ici : l'en-tête X-Node-Token
        // n'est jamais envoyé automatiquement par un navigateur.
        var nodeAllowed = endpoint?.Metadata.GetMetadata<WorkerCredentialAccessMetadata>() is not null
            && (safe || endpoint.Metadata.GetMetadata<WorkerReportAccessMetadata>() is not null);
        if (nodeAllowed
            && http.Request.Headers["X-Node-Token"].ToString() is { Length: > 0 } token
            && await workers.AuthenticateTokenAsync(token, http.RequestAborted))
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

