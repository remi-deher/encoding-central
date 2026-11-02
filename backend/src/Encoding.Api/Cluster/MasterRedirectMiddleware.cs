namespace Encoding.Api.Cluster;

/// <summary>
/// Redirige automatiquement les requêtes HTTP (code 308 Permanent Redirect) vers le nouveau Maître
/// lorsqu'un ancien maître a passé la main ou lorsqu'une URL de redirection est active.
/// </summary>
public sealed class MasterRedirectMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, LeadershipRepository repository)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Ne pas rediriger les sondes de santé ni l'état de gouvernance local
        if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/v1/cluster/leadership", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/v1/worker", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var masterUrl = await repository.GetMasterUrlAsync(context.RequestAborted);
        if (!string.IsNullOrWhiteSpace(masterUrl))
        {
            var targetUri = new Uri(new Uri(masterUrl), path + context.Request.QueryString);
            context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
            context.Response.Headers.Location = targetUri.ToString();
            return;
        }

        await next(context);
    }
}
