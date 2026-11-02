using System.Threading.RateLimiting;
using Encoding.Api.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;

namespace Encoding.Api.Auth;

public static class AuthExtensions
{
    public const string AnonymousPolicy = "anonymous-protocol";
    public const string ClusterMeshPolicy = "cluster-mesh";
    public const string HeavyOperationsPolicy = "heavy-operations";
    /// <summary>Lecture répétée de la fiche du nœud par l'assistant de premier démarrage ; quota distinct de la connexion.</summary>
    public const string SetupProbePolicy = "setup-probe";
    public const string AdministratorPolicy = "Administrator";

    public static IServiceCollection AddClusterAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var options = AuthOptions.From(configuration);
        services.AddSingleton(options);
        services.AddSingleton<UserRepository>();
        services.AddSingleton<AuthService>();
        services.AddSingleton<IAuthorizationHandler, ManagementAccessHandler>();
        services.AddAuthentication(SessionAuthentication.Scheme)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthentication.Scheme, null);
        services.AddAuthorization(authorization =>
        {
            var requirement = new ManagementAccessRequirement();
            authorization.FallbackPolicy = new AuthorizationPolicyBuilder(SessionAuthentication.Scheme).AddRequirements(requirement).Build();
            authorization.AddPolicy(AdministratorPolicy, policy => policy
                .AddAuthenticationSchemes(SessionAuthentication.Scheme).RequireRole("admin").AddRequirements(requirement));
        });
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await Encoding.Api.Infrastructure.ApiProblem.TooManyRequests()
                    .ExecuteAsync(context.HttpContext);
            };
            // Quota par adresse source pour les routes joignables sans compte (connexion, initialisation, jumelage des nœuds).
            limiter.AddPolicy(AnonymousPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = options.AnonymousRequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            limiter.AddPolicy(ClusterMeshPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 1200, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            limiter.AddPolicy(SetupProbePolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            // Quota pour les opérations intensives (découverte disque, import/export ZIP de flows).
            limiter.AddPolicy(HeavyOperationsPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.Identity?.Name ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = options.HeavyOperationsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        return services;
    }

    public static WebApplication UseClusterAuthentication(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        return app;
    }
}
