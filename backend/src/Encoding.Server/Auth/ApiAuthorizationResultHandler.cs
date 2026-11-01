using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Encoding.Server.Auth;

internal sealed class ApiAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    public Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Succeeded)
            return next(context);

        var authenticated = context.User.Identity?.IsAuthenticated == true;
        context.Response.StatusCode = authenticated
            ? StatusCodes.Status403Forbidden
            : StatusCodes.Status401Unauthorized;
        return context.Response.WriteAsJsonAsync(new { error = authenticated ? "Accès refusé." : "Authentification requise." });
    }
}
