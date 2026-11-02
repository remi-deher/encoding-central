using System.Net.Http.Json;
using System.Text.Json;
using Encoding.Core.Nodes;

namespace Encoding.NodeAgent;

/// <summary>
/// Relais de l'interface worker vers le central. Le navigateur reste sur le worker, donc le cookie de session
/// reste sur cette origine. Les routes de l'agent lui-même ne sont pas relayées. « Ce nœud » interroge cet agent,
/// après vérification de la session auprès du central.
/// </summary>
internal static class CentralUiProxy
{
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "proxy-connection", "transfer-encoding", "upgrade", "te", "trailer", "host"
    };
    /// <summary>Client nommé du relais : pas de redirection suivie (le navigateur les voit), pas de délai (flux SSE).</summary>
    public const string ClientName = "CentralProxy";

    /// <summary>
    /// Pas de conteneur de cookies : le client est partagé par tous les visiteurs, donc la session du central d'une personne
    /// serait réappliquée à toutes les suivantes. Les en-têtes <c>Cookie</c> et <c>Set-Cookie</c> sont relayés tels quels.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new() { AllowAutoRedirect = false, UseCookies = false };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Use(WebApplication app, Func<HttpContext, Task<string?>> resolveCentral, int localPort, string? secretFile)
    {
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api")
                || context.Request.Path.StartsWithSegments("/api/v1/worker"))
            {
                await next();
                return;
            }
            var client = context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

            var resolved = await resolveCentral(context);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                if (HttpMethods.IsGet(context.Request.Method)
                    && context.Request.Path.Equals("/api/v1/auth/status", StringComparison.OrdinalIgnoreCase))
                {
                    await context.Response.WriteAsJsonAsync(new { setupRequired = true, user = (object?)null });
                    return;
                }
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new { title = "Ce worker n’est pas relié à un central." });
                return;
            }
            var central = resolved.EndsWith('/') ? resolved : resolved + '/';

            if (IsPluginManagement(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { title = "Les plugins et les outils se gèrent sur le central." });
                return;
            }

            if (context.Request.Path.StartsWithSegments("/api/v1/local-node"))
            {
                await RelayLocalNodeAsync(context, client, central, localPort, secretFile);
                return;
            }

            var target = new Uri(new Uri(central), context.Request.Path + context.Request.QueryString);
            using var outgoing = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
            if (context.Request.ContentLength is > 0 || context.Request.Headers.TransferEncoding.Count > 0)
            {
                outgoing.Content = new StreamContent(context.Request.Body);
                if (context.Request.ContentType is { Length: > 0 } contentType)
                    outgoing.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
            foreach (var header in context.Request.Headers)
            {
                if (HopByHop.Contains(header.Key) || header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                if (!outgoing.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) && outgoing.Content is not null)
                    outgoing.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }

            using var incoming = await client.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)incoming.StatusCode;
            CopyHeaders(context, incoming, central);
            await incoming.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
    }

    /// <summary>Le worker affiche les plugins et les outils. Leur installation et leur activation restent sur le central.</summary>
    private static bool IsPluginManagement(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) return false;
        var path = request.Path.Value ?? "";
        if (path.StartsWith("/api/v1/store", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/api/v1/plugins", StringComparison.OrdinalIgnoreCase)) return true;
        return path.StartsWith("/api/v1/tools/", StringComparison.OrdinalIgnoreCase)
            && (path.EndsWith("/enabled", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/conformance-test", StringComparison.OrdinalIgnoreCase));
    }

    private static void CopyHeaders(HttpContext context, HttpResponseMessage incoming, string central)
    {
        foreach (var header in incoming.Headers.Concat(incoming.Content.Headers))
        {
            if (HopByHop.Contains(header.Key)) continue;
            if (header.Key.Equals("Location", StringComparison.OrdinalIgnoreCase))
            {
                var location = header.Value.FirstOrDefault() ?? "";
                if (location.StartsWith(central, StringComparison.OrdinalIgnoreCase))
                    location = "/" + location[central.Length..];
                context.Response.Headers.Location = location;
                continue;
            }
            if (header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var value in header.Value) context.Response.Headers.Append("Set-Cookie", value);
                continue;
            }
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }
    }

    private static async Task RelayLocalNodeAsync(HttpContext context, HttpClient client, string central, int localPort, string? secretFile)
    {
        var role = await SessionRoleAsync(context, client, central);
        if (role is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var secret = LocalEnrollment.Read(secretFile);
        if (string.IsNullOrEmpty(secret))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { title = "L’agent de ce worker n’a pas de secret local." });
            return;
        }
        var suffix = context.Request.Path.Value?["/api/v1/local-node".Length..] ?? "";
        using var outgoing = new HttpRequestMessage(new HttpMethod(context.Request.Method),
            new Uri($"http://127.0.0.1:{localPort}/api/v1/worker{suffix}{context.Request.QueryString}"));
        outgoing.Headers.TryAddWithoutValidation(LocalEnrollment.HeaderName, secret);
        if (context.Request.ContentLength is > 0 || context.Request.Headers.TransferEncoding.Count > 0)
        {
            outgoing.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType is { Length: > 0 } contentType)
                outgoing.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        using var incoming = await client.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)incoming.StatusCode;
        CopyHeaders(context, incoming, central);
        var body = await incoming.Content.ReadAsByteArrayAsync(context.RequestAborted);
        if (incoming.IsSuccessStatusCode && !string.Equals(role, "admin", StringComparison.Ordinal)
            && context.Request.Path.StartsWithSegments("/api/v1/local-node/status"))
            body = HideSecrets(body);
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, context.RequestAborted);
    }

    private static async Task<string?> SessionRoleAsync(HttpContext context, HttpClient client, string central)
    {
        using var probe = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(central), "api/v1/auth/status"));
        if (context.Request.Headers.TryGetValue("Cookie", out var cookie))
            probe.Headers.TryAddWithoutValidation("Cookie", cookie.ToArray());
        using var response = await client.SendAsync(probe, context.RequestAborted);
        if (!response.IsSuccessStatusCode) return null;
        var status = await response.Content.ReadFromJsonAsync<SessionStatus>(Json, context.RequestAborted);
        return status?.User is null ? null : status.User.Role;
    }

    private static byte[] HideSecrets(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Name is "pairingKey" or "verificationCode") writer.WriteNull(property.Name);
                    else property.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return stream.ToArray();
        }
        catch (JsonException) { return body; }
    }

    private sealed record SessionStatus(SessionUser? User);
    private sealed record SessionUser(string? Role);
}
