using System.Net;
using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Contracts.Auth;
using Encoding.Contracts.Nodes;

namespace Encoding.Api.Nodes;

/// <summary>
/// Fiche du nœud local pour l'assistant de premier démarrage (aucune session n'existe encore).
/// Ne lit que l'état non sensible de l'agent local par liste blanche : nom, matériel, accélérations ; puis ajoute
/// les adresses IPv4/IPv6 de ses interfaces et les URL configurées. Jamais de clé de jumelage, de code ni de cluster.
/// </summary>
public static class SetupNodeProbe
{
    private static readonly JsonSerializerOptions AgentJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed record AgentStatus(
        string? Name,
        string? DisplayName,
        NodeInventory? Inventory,
        IReadOnlyList<NodeToolCapability>? Capabilities);

    public static async Task<SetupNodeResponse> ReadAsync(
        IHttpClientFactory clients, IConfiguration configuration, NetworkSettingsService network, CancellationToken cancellationToken)
    {
        AgentStatus? agent = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/worker/status");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await clients.CreateClient(LocalNodeEndpoints.HttpClientName).SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.OK)
                agent = Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Agent arrêté (fonction worker désactivée) ou en cours de démarrage : matériel inconnu, pas une erreur.
        }
        return Build(agent, LocalNetworkAddresses.Current(), Urls(network.Current.PublicUrl,
            configuration["Cluster:AdvertisedApiBaseUrl"], configuration["Cluster:Reconciliation:AdvertisedApiBaseUrl"]));
    }

    /// <summary>Projette l'état de l'agent sur les seuls champs publiables ; null si la réponse est illisible.</summary>
    private static AgentStatus? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<AgentStatus>(json, AgentJson); }
        catch (JsonException) { return null; }
    }

    public static SetupNodeResponse ProjectAgentStatus(string agentStatusJson, IReadOnlyList<SetupNodeAddress> addresses, IReadOnlyList<string> urls) =>
        Build(Parse(agentStatusJson), addresses, urls);

    private static SetupNodeResponse Build(AgentStatus? agent, IReadOnlyList<SetupNodeAddress> addresses, IReadOnlyList<string> urls) => new(
        agent?.Name,
        agent?.DisplayName,
        agent is not null,
        // Les polices installées ne servent pas à l'assistant : la fiche reste minimale.
        agent?.Inventory is { } inventory ? inventory with { FontSets = null } : null,
        agent?.Capabilities?.Select(tool => new SetupNodeTool(
            tool.ToolId,
            tool.AccelerationTypes ?? [],
            tool.EncoderChecks ?? [])).ToArray(),
        addresses,
        urls);

    /// <summary>
    /// URL configurées par lesquelles ce nœud se dit joignable, dédoublonnées. Un nom sans point (« http://encoding:8080 »,
    /// nom de service Docker par défaut) n'a de sens qu'à l'intérieur du réseau du conteneur : il n'est pas montré.
    /// </summary>
    public static IReadOnlyList<string> Urls(params string?[] candidates) =>
        candidates
            .Select(candidate => NetworkSettingsService.TryNormalizePublicUrl(candidate, out var normalized) ? normalized : null)
            .OfType<string>()
            .Where(IsReachableFromNetwork)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsReachableFromNetwork(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            return IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var ip) && !IPAddress.IsLoopback(ip);
        return uri.Host.Contains('.') && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }
}
