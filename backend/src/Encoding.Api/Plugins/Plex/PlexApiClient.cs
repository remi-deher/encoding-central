using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Encoding.Contracts.Plugins;

namespace Encoding.Api.Plugins.Plex;

/// <summary>Échec de dialogue avec plex.tv ou un serveur Plex ; le message ne contient jamais de jeton.</summary>
public sealed class PlexApiException(string message) : Exception(message);

/// <summary>
/// Adaptateur HTTP vers plex.tv (PIN, ressources du compte) et vers un serveur Plex (identité, sections) — 0008.
/// Jetons uniquement en en-tête X-Plex-Token, jamais dans l'URL ni dans les messages.
/// </summary>
public sealed class PlexApiClient(IHttpClientFactory clients)
{
    public const string HttpClientName = "PlexSetup";
    public const string Product = "Encoding";
    private static readonly Uri PlexTv = new("https://plex.tv/");

    public async Task<(long Id, string Code, DateTimeOffset ExpiresAt)> CreatePinAsync(string clientIdentifier, CancellationToken ct)
    {
        using var document = await SendAsync(HttpMethod.Post, new Uri(PlexTv, "api/v2/pins?strong=true"), clientIdentifier, null, ct)
            ?? throw new PlexApiException("plex.tv n’a pas créé de code de connexion.");
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var id) || !id.TryGetInt64(out var pinId)
            || !root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String)
            throw new PlexApiException("Réponse de plex.tv inattendue pour le code de connexion.");
        return (pinId, code.GetString()!, ReadExpiry(root));
    }

    /// <summary>Jeton du compte si le PIN est autorisé, null s'il est en attente ; <c>expired</c> si plex.tv ne le connaît plus.</summary>
    public async Task<(string? Token, bool Expired)> CheckPinAsync(long pinId, string clientIdentifier, CancellationToken ct)
    {
        using var document = await SendAsync(HttpMethod.Get, new Uri(PlexTv, $"api/v2/pins/{pinId}"), clientIdentifier, null, ct);
        if (document is null) return (null, true);
        var root = document.RootElement;
        var token = root.TryGetProperty("authToken", out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Length > 0
            ? value.GetString() : null;
        return (token, token is null && ReadExpiry(root) <= DateTimeOffset.UtcNow);
    }

    /// <summary>Serveurs Plex du compte avec le jeton d'accès propre à chacun (à garder côté serveur).</summary>
    public async Task<IReadOnlyList<(PlexServer Server, string AccessToken)>> ServersAsync(string accountToken, string clientIdentifier, CancellationToken ct)
    {
        using var document = await SendAsync(HttpMethod.Get, new Uri(PlexTv, "api/v2/resources?includeHttps=1&includeRelay=1"), clientIdentifier, accountToken, ct)
            ?? throw new PlexApiException("plex.tv n’a pas renvoyé la liste des serveurs.");
        return ParseServers(document.RootElement);
    }

    public static IReadOnlyList<(PlexServer Server, string AccessToken)> ParseServers(JsonElement root)
    {
        var result = new List<(PlexServer, string)>();
        if (root.ValueKind != JsonValueKind.Array) return result;
        foreach (var resource in root.EnumerateArray())
        {
            var provides = Text(resource, "provides") ?? "";
            var id = Text(resource, "clientIdentifier");
            var token = Text(resource, "accessToken");
            if (!provides.Split(',').Contains("server") || id is null || token is null) continue;
            var connections = new List<PlexServerConnection>();
            if (resource.TryGetProperty("connections", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var connection in list.EnumerateArray())
                    if (Text(connection, "uri") is { } uri && Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https")
                        connections.Add(new(uri.TrimEnd('/'), Bool(connection, "local"), Bool(connection, "relay")));
            // Adresses locales d'abord, relais en dernier : le serveur Encoding est en général sur le même réseau.
            var ordered = connections.OrderBy(c => c.Relay).ThenByDescending(c => c.Local).ThenBy(c => c.Uri.StartsWith("https", StringComparison.Ordinal)).ToList();
            result.Add((new PlexServer(id, Text(resource, "name") ?? id, Bool(resource, "owned"), ordered), token));
        }
        return result;
    }

    public async Task VerifyServerAsync(string baseUrl, string token, CancellationToken ct)
    {
        using var document = await SendAsync(HttpMethod.Get, ServerUri(baseUrl, "identity"), null, token, ct)
            ?? throw new PlexApiException("Le serveur Plex n’a pas répondu à cette adresse.");
    }

    public async Task<IReadOnlyList<RemoteLibrary>> SectionsAsync(string baseUrl, string token, CancellationToken ct)
    {
        using var document = await SendAsync(HttpMethod.Get, ServerUri(baseUrl, "library/sections"), null, token, ct)
            ?? throw new PlexApiException("Le serveur Plex n’a pas renvoyé ses bibliothèques.");
        return ParseSections(document.RootElement);
    }

    public static IReadOnlyList<RemoteLibrary> ParseSections(JsonElement root)
    {
        var result = new List<RemoteLibrary>();
        if (!root.TryGetProperty("MediaContainer", out var container) || !container.TryGetProperty("Directory", out var directories)
            || directories.ValueKind != JsonValueKind.Array) return result;
        foreach (var directory in directories.EnumerateArray())
        {
            var key = directory.TryGetProperty("key", out var value) ? value.ToString() : null;
            if (string.IsNullOrEmpty(key)) continue;
            var locations = directory.TryGetProperty("Location", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(location => Text(location, "path")).OfType<string>().ToList()
                : [];
            result.Add(new(key, Text(directory, "title") ?? key, Text(directory, "type") ?? "", locations));
        }
        return result;
    }

    private static Uri ServerUri(string baseUrl, string path)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new PlexApiException("L’adresse du serveur Plex doit être une URL HTTP ou HTTPS valide, sans identifiant intégré.");
        return new Uri(new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"), path);
    }

    /// <summary>JSON de la réponse, null pour 404 ; toute autre erreur devient une PlexApiException sans jeton.</summary>
    private async Task<JsonDocument?> SendAsync(HttpMethod method, Uri uri, string? clientIdentifier, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Plex-Product", Product);
        if (clientIdentifier is not null) request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", clientIdentifier);
        if (token is not null) request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(HttpClientName).SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PlexApiException(uri.Host == PlexTv.Host ? "plex.tv n’a pas répondu dans le délai imparti." : "Le serveur Plex n’a pas répondu dans le délai imparti.");
        }
        catch (HttpRequestException)
        {
            throw new PlexApiException(uri.Host == PlexTv.Host
                ? "Impossible de joindre plex.tv depuis le serveur Encoding."
                : "Impossible de joindre le serveur Plex à cette adresse depuis le serveur Encoding.");
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new PlexApiException("Plex a refusé l’accès : jeton invalide ou révoqué.");
            if (!response.IsSuccessStatusCode)
                throw new PlexApiException($"Plex a répondu avec le statut HTTP {(int)response.StatusCode}.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            try { return await JsonDocument.ParseAsync(stream, cancellationToken: ct); }
            catch (JsonException) { throw new PlexApiException("Réponse de Plex illisible (JSON attendu)."); }
        }
    }

    private static DateTimeOffset ReadExpiry(JsonElement root) =>
        root.TryGetProperty("expiresAt", out var value) && value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var at)
            ? at
            : DateTimeOffset.UtcNow.AddMinutes(15);

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.String && value.GetString() is "1" or "true");
}
