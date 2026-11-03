namespace Encoding.Api.Infrastructure;

/// <summary>
/// Annonce aux interfaces et aux workers qu'un sujet a changé, après toute mutation réussie de l'API.
/// Un seul point d'accroche : les endpoints n'ont pas à connaître le flux temps réel.
/// </summary>
public static class InterfaceLiveNotifier
{
    private static readonly Dictionary<string, string> Routes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["executions"] = "executions",
        ["media-executions"] = "executions",
        ["runtime"] = "executions",
        ["activity"] = "executions",
        ["workers"] = "workers",
        ["worker-groups"] = "workers",
        ["local-worker"] = "workers",
        ["central"] = "workers",
        ["libraries"] = "libraries",
        ["integrations"] = "libraries",
        ["plugins"] = "plugins",
        ["tools"] = "plugins",
        ["store"] = "plugins",
        ["flows"] = "flows",
        ["scripts"] = "flows",
        ["templates"] = "flows",
        ["component-packs"] = "flows",
        ["encoding-profiles"] = "flows",
        ["resources"] = "flows",
        ["system"] = "settings",
        ["network"] = "settings",
        ["tokens"] = "settings",
        ["users"] = "settings",
    };

    /// <summary>Renvoie le sujet touché par une requête de mutation, ou null si elle n'annonce rien.</summary>
    public static string? TopicFor(string method, PathString path)
    {
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)) return null;
        var value = path.Value;
        const string prefix = "/api/v1/";
        if (value is null || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = value.AsSpan(prefix.Length);
        var slash = rest.IndexOf('/');
        var head = slash < 0 ? rest : rest[..slash];
        // Les journaux de commandes alimentent la vue pipeline sans rien offrir à un worker : sujet à part, qui ne le réveille pas.
        if (value.EndsWith("/operation-logs", StringComparison.OrdinalIgnoreCase)) return "execution-logs";
        // Les renouvellements de bail, battements de cœur et verrous ne changent rien de visible. Un « claim » est un POST
        // qui répond 204 quand il n'y a rien à prendre : l'annoncer réveillerait les workers, qui referaient un claim, en boucle.
        if (value.EndsWith("/renew", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("/claim", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("/execution-states", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("/heartbeat", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/disk-locks/", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/directory-browser/", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/dav/", StringComparison.OrdinalIgnoreCase))
            return null;
        return Routes.TryGetValue(head.ToString(), out var topic) ? topic : null;
    }

    public static IApplicationBuilder UseInterfaceLiveNotifications(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            await next();
            if (context.Response.StatusCode is < 200 or >= 300) return;
            if (TopicFor(context.Request.Method, context.Request.Path) is { } topic)
                // Absent d'un hôte réduit (tests, outils) : annoncer est un confort, jamais une cause d'échec de la requête.
                context.RequestServices.GetService<InterfaceLiveBroadcaster>()?.Publish(topic);
        });
}
