using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Encoding.Api.Nodes;

/// <summary>
/// Relais authentifié vers l'agent worker du même conteneur (architecture 0044) : l'interface gère « ce nœud » depuis la page
/// Nœuds, avec la session du serveur, sans appeler l'agent directement depuis le navigateur.
/// </summary>
public static class LocalNodeEndpoints
{
    public const string HttpClientName = "LocalAgent";
    public const string BaseUrlSetting = "LocalAgent:BaseUrl";

    public static string AgentBaseUrl(IConfiguration configuration) =>
        configuration[BaseUrlSetting] is { Length: > 0 } configured
            ? configured
            : $"http://127.0.0.1:{configuration.GetValue("WorkerUi:Port", 8081)}/";

    public static void MapLocalNodeEndpoints(this WebApplication app)
    {
        var local = app.MapGroup("/api/v1/local-node").WithTags("Local node");
        // Lecture : le rôle lecture voit l'état, mais ni la clé de jumelage ni le code de vérification.
        local.MapGet("/status", (HttpContext http, IHttpClientFactory clients, Infrastructure.ClusterIdentityRepository cluster, LocalNodeRegistry registry, CancellationToken ct) =>
            RelayAsync(http, clients, HttpMethod.Get, "status", ct, hideSecrets: !http.User.IsInRole("admin"), cluster, registry)).WithName("GetLocalNodeStatus");
        local.MapGet("/clusters", (HttpContext http, IHttpClientFactory clients, CancellationToken ct) =>
            RelayAsync(http, clients, HttpMethod.Get, "clusters", ct)).WithName("ListLocalNodeClusters");
        local.MapPost("/clusters", (HttpContext http, IHttpClientFactory clients, CancellationToken ct) =>
            RelayAsync(http, clients, HttpMethod.Post, "clusters", ct)).WithName("AddLocalNodeCluster");
        local.MapDelete("/clusters/{clusterId:guid}", (Guid clusterId, HttpContext http, IHttpClientFactory clients, CancellationToken ct) =>
            RelayAsync(http, clients, HttpMethod.Delete, "clusters/" + clusterId.ToString("D"), ct)).WithName("ForgetLocalNodeCluster");
        local.MapPost("/enroll", (HttpContext http, IHttpClientFactory clients, LocalNodeRegistry registry, CancellationToken ct) =>
            RelayWithDisplayNameAsync(http, clients, "enroll", registry, ct)).WithName("EnrollLocalNode");
        local.MapPost("/pairing-key", (HttpContext http, IHttpClientFactory clients, LocalNodeRegistry registry, CancellationToken ct) =>
            RelayWithDisplayNameAsync(http, clients, "pairing-key", registry, ct)).WithName("CreateLocalNodePairingKey");
        local.MapPost("/leave", (HttpContext http, IHttpClientFactory clients, CancellationToken ct) =>
            RelayAsync(http, clients, HttpMethod.Post, "leave", ct)).WithName("LeaveFromLocalNode");
        local.MapGet("/logs", (HttpContext http, IHttpClientFactory clients, CancellationToken ct) =>
            RelayAsync(http, clients, HttpMethod.Get, "logs" + (http.Request.QueryString.Value ?? string.Empty), ct)).WithName("GetLocalWorkerLogs");
    }

    /// <summary>Retire la clé de jumelage et le code de vérification d'un état de nœud destiné à un lecteur.</summary>
    public static string HideSecrets(string statusJson)
    {
        if (JsonNode.Parse(statusJson) is not JsonObject status) return statusJson;
        status["pairingKey"] = null;
        status["verificationCode"] = null;
        return status.ToJsonString();
    }

    /// <summary>Ajoute <c>standalone</c> à l'état du nœud et synchronise son identifiant et son nom d'affichage avec le registre local.</summary>
    private static async Task<string> MarkStandaloneAsync(string statusJson, Infrastructure.ClusterIdentityRepository cluster, LocalNodeRegistry? registry, CancellationToken cancellationToken)
    {
        if (JsonNode.Parse(statusJson) is not JsonObject status) return statusJson;
        var identity = await cluster.GetAsync(cancellationToken);
        var attachedTo = status["clusterId"]?.GetValue<string>();
        status["standalone"] = identity.Standalone && Guid.TryParse(attachedTo, out var id) && id == identity.Id;
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

    private static async Task<IResult> RelayWithDisplayNameAsync(HttpContext http, IHttpClientFactory clients, string path, LocalNodeRegistry registry, CancellationToken cancellationToken)
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
            return Results.Json(new { title = "La fonction worker de ce nœud ne répond pas. Activez-la depuis « Mode du nœud »." }, statusCode: (int)HttpStatusCode.ServiceUnavailable);
        }
    }

    private static async Task<IResult> RelayAsync(HttpContext http, IHttpClientFactory clients, HttpMethod method, string path,
        CancellationToken cancellationToken, bool hideSecrets = false, Infrastructure.ClusterIdentityRepository? cluster = null,
        LocalNodeRegistry? registry = null)
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
            if (cluster is not null && response.IsSuccessStatusCode) text = await MarkStandaloneAsync(text, cluster, registry, cancellationToken);
            return Results.Content(text, "application/json", statusCode: (int)response.StatusCode);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Results.Json(new { title = "La fonction worker de ce nœud ne répond pas. Activez-la depuis « Mode du nœud »." }, statusCode: (int)HttpStatusCode.ServiceUnavailable);
        }
    }
}
