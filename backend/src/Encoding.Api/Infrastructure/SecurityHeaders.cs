namespace Encoding.Api.Infrastructure;

/// <summary>
/// En-têtes de sécurité de base, posés par l'application pour qu'ils existent même sans reverse proxy.
/// Réglages (section <c>Security</c>) : <c>Hsts</c> (vrai par défaut, seulement sur HTTPS), <c>HstsMaxAgeDays</c> (365),
/// <c>ContentSecurityPolicy</c> (vide par défaut : à renseigner après essai avec l'interface réelle).
/// </summary>
public static class SecurityHeaders
{
    public static WebApplication UseSecurityHeaders(this WebApplication app)
    {
        var hsts = app.Configuration.GetValue("Security:Hsts", true);
        var maxAge = TimeSpan.FromDays(Math.Max(1, app.Configuration.GetValue("Security:HstsMaxAgeDays", 365)));
        var csp = app.Configuration["Security:ContentSecurityPolicy"];
        var hstsValue = $"max-age={(long)maxAge.TotalSeconds}";
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.TryAdd("X-Content-Type-Options", "nosniff");
                headers.TryAdd("X-Frame-Options", "DENY");
                headers.TryAdd("Referrer-Policy", "same-origin");
                if (hsts && context.Request.IsHttps) headers.TryAdd("Strict-Transport-Security", hstsValue);
                if (!string.IsNullOrWhiteSpace(csp)) headers.TryAdd("Content-Security-Policy", csp);
                // Réponses d'API (sessions, jetons, état du cluster) : jamais mises en cache par un navigateur ou un proxy.
                if (context.Request.Path.StartsWithSegments("/api")) headers.TryAdd("Cache-Control", "no-store");
                return Task.CompletedTask;
            });
            return next();
        });
        return app;
    }
}
