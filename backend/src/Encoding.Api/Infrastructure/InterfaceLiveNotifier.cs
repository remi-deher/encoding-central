namespace Encoding.Api.Infrastructure;

/// <summary>
/// Annonce qu'un sujet a changé après toute mutation réussie des groupes de routes que <c>NotifyLive</c> ne couvre pas :
/// flows (et ce qui s'y rattache), plugins et outils, réglages. Workers, bibliothèques et exécutions gardent leurs propres sources.
/// Un seul point d'accroche : les endpoints n'ont pas à connaître le flux temps réel.
/// </summary>
public static class InterfaceLiveNotifier
{
    private static readonly Dictionary<string, string> Routes = new(StringComparer.OrdinalIgnoreCase)
    {
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
        // Les journaux de commandes alimentent la vue pipeline sans rien offrir à un worker : sujet à part, qui ne le réveille pas.
        if (value.EndsWith("/operation-logs", StringComparison.OrdinalIgnoreCase)) return "execution-logs";
        var rest = value.AsSpan(prefix.Length);
        var slash = rest.IndexOf('/');
        var head = slash < 0 ? rest : rest[..slash];
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
