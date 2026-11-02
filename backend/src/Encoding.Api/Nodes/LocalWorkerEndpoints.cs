using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Encoding.Api.Nodes;

/// <summary>
/// Relais authentifié vers l'agent worker du même conteneur (architecture 0044) : l'interface gère « ce worker » depuis la page
/// Workers, avec la session du serveur, sans appeler l'agent directement depuis le navigateur.
/// </summary>
public static class LocalWorkerEndpoints
{
    public const string HttpClientName = "LocalAgent";
    public const string BaseUrlSetting = "LocalAgent:BaseUrl";

    public static string AgentBaseUrl(IConfiguration configuration) =>
        configuration[BaseUrlSetting] is { Length: > 0 } configured
            ? configured
            : $"http://127.0.0.1:{configuration.GetValue("WorkerUi:Port", 8081)}/";

    public static void MapLocalNodeEndpoints(this WebApplication app)
    {
        var local = app.MapGroup("/api/v1/local-worker").WithTags("Local node");
        // Lecture : le rôle lecture voit l'état, mais ni la clé de jumelage ni le code de vérification.
        local.MapGet("/status", (HttpContext http, IHttpClientFactory clients, LocalWorkerRegistry registry, CancellationToken ct) =>
            RelayAsync(http, clients, HttpMethod.Get, "status", ct, hideSecrets: !http.User.IsInRole("admin"), registry)).WithName("GetLocalNodeStatus");
        local.MapGet("/centrals", (HttpContext http, IHttpClientFactory clients, CancellationToken ct) =>
            RelayAsync(http, clients, HttpMethod.Get, "clusters", ct)).WithName("ListLocalNodeClusters");
        local.MapPost("/centrals", (HttpContext http, IHttpClientFactory clients, CancellationToken ct) =>
            RelayAsync(http, clients, HttpMethod.Post, "clusters", ct)).WithName("AddLocalNodeCluster");
        local.MapDelete("/centrals/{clusterId:guid}", (Guid clusterId, HttpContext http, IHttpClientFactory clients, CancellationToken ct) =>
            RelayAsync(http, clients, HttpMethod.Delete, "clusters/" + clusterId.ToString("D"), ct)).WithName("ForgetLocalNodeCluster");
        local.MapPost("/enroll", (HttpContext http, IHttpClientFactory clients, LocalWorkerRegistry registry, CancellationToken ct) =>
            RelayWithDisplayNameAsync(http, clients, "enroll", registry, ct)).WithName("EnrollLocalNode");
        local.MapPost("/pairing-key", (HttpContext http, IHttpClientFactory clients, LocalWorkerRegistry registry, CancellationToken ct) =>
            RelayWithDisplayNameAsync(http, clients, "pairing-key", registry, ct)).WithName("CreateLocalNodePairingKey");
        local.MapPost("/leave", (HttpContext http, IHttpClientFactory clients, CancellationToken ct) =>
            RelayAsync(http, clients, HttpMethod.Post, "leave", ct)).WithName("LeaveFromLocalNode");
        local.MapGet("/logs", (HttpContext http, IHttpClientFactory clients, CancellationToken ct) =>
            RelayAsync(http, clients, HttpMethod.Get, "logs" + (http.Request.QueryString.Value ?? string.Empty), ct)).WithName("GetLocalWorkerLogs");
    }

    /// <summary>Retire la clé de jumelage et le code de vérification d'un état de worker destiné à un lecteur.</summary>
    public static string HideSecrets(string statusJson)
    {
        if (JsonNode.Parse(statusJson) is not JsonObject status) return statusJson;
        status["pairingKey"] = null;
        status["verificationCode"] = null;
        return status.ToJsonString();
    }

    /// <summary>Synchronise l'identifiant et le nom d'affichage du worker avec le registre local.</summary>
    private static async Task<string> SyncLocalWorkerAsync(string statusJson, LocalWorkerRegistry? registry, CancellationToken cancellationToken)
    {
        if (JsonNode.Parse(statusJson) is not JsonObject status) return statusJson;
        if (registry is not null)
        {
            if (Guid.TryParse(status["nodeId"]?.GetValue<string>(), out var nodeId) && nodeId != Guid.Empty)
                await registry.RegisterAsync(nodeId, cancellationToken);
            if (string.IsNullOrWhiteSpace(status["displayName"]?.GetValue<string>()))
            {
                var resolved = await registry.ResolveDisplayNameAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(resolved))
                    status["displayName"] = resolved;
            }
        }
        return status.ToJsonString();
    }

    private static async Task<IResult> RelayWithDisplayNameAsync(HttpContext http, IHttpClientFactory clients, string path, LocalWorkerRegistry registry, CancellationToken cancellationToken)
    {
        using var body = new StreamReader(http.Request.Body);
        var content = await body.ReadToEndAsync(cancellationToken);
        if (JsonNode.Parse(string.IsNullOrWhiteSpace(content) ? "{}" : content) is JsonObject obj)
        {
            var current = obj["displayName"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(current))
            {
                var resolved = await registry.ResolveDisplayNameAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    obj["displayName"] = resolved;
                    content = obj.ToJsonString();
                }
            }
            else
            {
                await registry.RequestNameAsync(current, cancellationToken);
            }
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/worker/" + path)
        {
            Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
        };
        try
        {
            using var response = await clients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NoContent) return Results.NoContent();
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return Results.Content(text, "application/json", statusCode: (int)response.StatusCode);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Results.Json(new { title = "La fonction worker de ce worker ne répond pas. Activez-la depuis « Mode du worker »." }, statusCode: (int)HttpStatusCode.ServiceUnavailable);
        }
    }

    private static async Task<IResult> RelayAsync(HttpContext http, IHttpClientFactory clients, HttpMethod method, string path,
        CancellationToken cancellationToken, bool hideSecrets = false, LocalWorkerRegistry? registry = null)
    {
        using var request = new HttpRequestMessage(method, "api/v1/worker/" + path);
        if (method != HttpMethod.Get)
        {
            using var body = new StreamReader(http.Request.Body);
            request.Content = new StringContent(await body.ReadToEndAsync(cancellationToken), System.Text.Encoding.UTF8, "application/json");
        }
        try
        {
            using var response = await clients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NoContent) return Results.NoContent();
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (hideSecrets && response.IsSuccessStatusCode) text = HideSecrets(text);
            if (registry is not null && response.IsSuccessStatusCode) text = await SyncLocalWorkerAsync(text, registry, cancellationToken);
            return Results.Content(text, "application/json", statusCode: (int)response.StatusCode);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Results.Json(new { title = "La fonction worker de ce worker ne répond pas. Activez-la depuis « Mode du worker »." }, statusCode: (int)HttpStatusCode.ServiceUnavailable);
        }
    }
}
