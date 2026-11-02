using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Encoding.Api.Auth;
using Encoding.Api.Infrastructure;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

public sealed record AiRpc(Guid ClusterId, Guid Id, string Operation, string Payload);
public sealed record AiRpcReply(Guid Id, string Payload);

/// <summary>Authenticated and encrypted peer RPC, using the admission keys already maintained by the mesh.</summary>
public sealed class AiClusterTransport(ClusterPeerIdentityStore identity, ClusterIdentityRepository clusters,
    ClusterPeerAdmissionRepository admissions, ClusterReconciliationRepository peers, ClusterReconciliationHealth health,
    AiClusterStore store, IHttpClientFactory clients, IConfiguration configuration)
{
    public const string Path = "/api/v1/cluster/ai/exchange";
    private const int MaxRpcBytes = 16 * 1024 * 1024;
    public Guid LocalId => identity.PeerId;
    public async Task<IReadOnlyList<string>> PeersAsync(CancellationToken ct) =>
        (await peers.GetPeerEndpointsAsync(ct)).Concat(health.GetStatus().Peers.Select(p => p.Url))
        .Concat(configuration.GetSection("Cluster:Reconciliation:Peers").Get<string[]>() ?? [])
        .Select(ClusterReconciliationRepository.TryNormalizePeerUrl).OfType<string>()
        .Where(url => !string.Equals(url, ClusterReconciliationRepository.TryNormalizePeerUrl(configuration["Cluster:Reconciliation:AdvertisedApiBaseUrl"]), StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToArray();

    public async Task<T> CallAsync<T>(string url, string operation, object value, TimeSpan timeout, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var cluster = await clusters.GetAsync(ct);
        var rpc = new AiRpc(cluster.Id, id, operation, await store.ProtectAsync(value, "rpc/" + id + "/" + operation, ct));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(rpc);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(url), Path)) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add(ClusterReconciliationSignature.PeerHeader, identity.PeerId.ToString());
        request.Headers.Add(ClusterReconciliationSignature.TimeHeader, timestamp);
        request.Headers.Add(ClusterReconciliationSignature.SignatureHeader, ClusterReconciliationSignature.Create(identity, timestamp, bytes));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancellation.CancelAfter(timeout);
        using var response = await clients.CreateClient("AiCluster").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        response.EnsureSuccessStatusCode();
        var body = await ReadAsync(await response.Content.ReadAsStreamAsync(cancellation.Token), cancellation.Token);
        string? Header(string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
        if (!Guid.TryParse(Header(ClusterReconciliationSignature.PeerHeader), out var sender)
            || await admissions.GetActivePublicKeyAsync(cluster.Id, sender, cancellation.Token) is not { } key
            || !ClusterReconciliationSignature.Verify(key, sender, Header(ClusterReconciliationSignature.TimeHeader), Header(ClusterReconciliationSignature.SignatureHeader), body))
            throw new InvalidOperationException("Réponse IA non authentifiée.");
        var reply = JsonSerializer.Deserialize<AiRpcReply>(body) ?? throw new InvalidOperationException("Réponse IA vide.");
        if (reply.Id != id) throw new InvalidOperationException("Réponse IA inattendue.");
        return await store.UnprotectAsync<T>(reply.Payload, "reply/" + id, cancellation.Token);
    }

    /// <summary>Adresse du pair qui répond sous cet identifiant, ou null s'il ne répond à aucune sonde.</summary>
    public async Task<string?> FindNodeUrlAsync(Guid nodeId, CancellationToken ct)
    {
        foreach (var url in await PeersAsync(ct))
        {
            try
            {
                var node = await CallAsync<AiNodeAvailability>(url, "availability", new { }, TimeSpan.FromSeconds(15), ct);
                if (node.NodeId == nodeId) return url;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested) { }
        }
        return null;
    }

    public async Task<T> CallNodeAsync<T>(Guid nodeId, string operation, object value, TimeSpan timeout, CancellationToken ct)
    {
        if (nodeId == Guid.Empty || nodeId == LocalId)
            throw new ArgumentException("Le nœud coordinateur distant est invalide.");
        foreach (var url in await PeersAsync(ct))
        {
            try
            {
                var node = await CallAsync<AiNodeAvailability>(url, "availability", new { }, TimeSpan.FromSeconds(15), ct);
                if (node.NodeId == nodeId)
                    return await CallAsync<T>(url, operation, value, timeout, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Keep searching known peer endpoints; never run the request on another coordinator.
            }
        }
        throw new InvalidOperationException("Le coordinateur de cette conversation est indisponible. Réessayez lorsque ce nœud sera joignable.");
    }

    public async Task<IResult> ReceiveAsync(HttpContext http, AiClusterService service, CancellationToken ct)
    {
        var bytes = await ReadAsync(http.Request.Body, ct);
        var cluster = await clusters.GetAsync(ct);
        if (!Guid.TryParse(http.Request.Headers[ClusterReconciliationSignature.PeerHeader], out var sender)
            || await admissions.GetActivePublicKeyAsync(cluster.Id, sender, ct) is not { } key
            || !ClusterReconciliationSignature.Verify(key, sender, http.Request.Headers[ClusterReconciliationSignature.TimeHeader],
                http.Request.Headers[ClusterReconciliationSignature.SignatureHeader], bytes)) return Results.Unauthorized();
        var request = JsonSerializer.Deserialize<AiRpc>(bytes);
        if (request is null || request.ClusterId != cluster.Id || request.Id == Guid.Empty) return Results.BadRequest();
        var value = await store.UnprotectAsync<JsonElement>(request.Payload, "rpc/" + request.Id + "/" + request.Operation, ct);
        // Only inference/status/session operations are available to peers; no token-management operation exists here.
        var result = await service.DispatchAsync(request.Operation, value, ct);
        var body = JsonSerializer.SerializeToUtf8Bytes(new AiRpcReply(request.Id, await store.ProtectAsync(result, "reply/" + request.Id, ct)));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        http.Response.Headers[ClusterReconciliationSignature.PeerHeader] = identity.PeerId.ToString();
        http.Response.Headers[ClusterReconciliationSignature.TimeHeader] = timestamp;
        http.Response.Headers[ClusterReconciliationSignature.SignatureHeader] = ClusterReconciliationSignature.Create(identity, timestamp, body);
        return Results.Bytes(body, "application/json");
    }
    internal static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > MaxRpcBytes) throw new ArgumentException("Message IA trop volumineux.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return output.ToArray();
    }
}
