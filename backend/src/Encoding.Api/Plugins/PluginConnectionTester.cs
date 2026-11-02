using System.Net;
using System.Text.Json;

namespace Encoding.Api.Plugins;

public sealed record PluginConnectionTestRequest(IReadOnlyDictionary<string, JsonElement> Values);
public sealed record PluginConnectionTestResponse(bool Success, string Message);

public sealed class PluginConnectionTester(IHttpClientFactory clients)
{
    public async Task<PluginConnectionTestResponse> TestAsync(
        string pluginId, IReadOnlyDictionary<string, JsonElement> values, CancellationToken ct)
    {
        var baseUrl = ReadText(values, "baseUrl");
        var apiKey = ReadText(values, "apiKey");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(baseUri.UserInfo))
            throw new PluginConfigurationValidationException("L’adresse doit être une URL HTTP ou HTTPS valide, sans identifiant intégré.");

        var path = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(path), Endpoint(pluginId)));
        switch (pluginId)
        {
            case "plex":
                request.Headers.TryAddWithoutValidation("X-Plex-Token", apiKey);
                break;
            case "sonarr":
            case "radarr":
                request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
                break;
            case "jellyfin":
                request.Headers.TryAddWithoutValidation("X-Emby-Token", apiKey);
                break;
            default:
                throw new PluginConfigurationValidationException("Ce plugin ne prend pas en charge le test de connexion.");
        }

        try
        {
            var response = await clients.CreateClient("PluginConnectionTest").SendAsync(request, ct);
            if (pluginId == "sonarr" && response.StatusCode == HttpStatusCode.NotFound)
            {
                response.Dispose();
                using var legacyRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(path), "api/v3/system/status"));
                legacyRequest.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
                response = await clients.CreateClient("PluginConnectionTest").SendAsync(legacyRequest, ct);
            }
            using (response)
            {
                if (response.IsSuccessStatusCode)
                    return new(true, "Connexion réussie.");
                var message = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Connexion refusée : vérifiez la clé ou le jeton API.",
                    HttpStatusCode.NotFound => "Serveur joignable, mais l’API attendue est introuvable. Vérifiez l’adresse et la version du service.",
                    _ => $"Le serveur a répondu avec le statut HTTP {(int)response.StatusCode}."
                };
                return new(false, message);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, "Le serveur n’a pas répondu dans le délai imparti.");
        }
        catch (HttpRequestException)
        {
            return new(false, "Impossible de joindre le serveur. Vérifiez son adresse et son accessibilité depuis le serveur Encoding.");
        }
    }

    private static string Endpoint(string pluginId) => pluginId switch
    {
        "plex" => "identity",
        "sonarr" => "api/v5/system/status",
        "radarr" => "api/v3/system/status",
        "jellyfin" => "System/Info",
        _ => throw new PluginConfigurationValidationException("Ce plugin ne prend pas en charge le test de connexion.")
    };

    private static string ReadText(IReadOnlyDictionary<string, JsonElement> values, string key) =>
        values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
