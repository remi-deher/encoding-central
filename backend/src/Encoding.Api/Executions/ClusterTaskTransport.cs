using System.Globalization;
using System.Collections.Concurrent;
using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Executions;
using System.Data.Common;
namespace Encoding.Api.Executions;

public sealed class ClusterTaskTransport(DbDataSource db,ClusterPeerIdentityStore identity,ClusterReconciliationHealth health,IHttpClientFactory clients,RuntimeArtifactStore artifacts)
{
    private readonly ConcurrentDictionary<(Guid,string,long),Lazy<Task>> inflight=new();
    internal const string Path="api/v1/cluster/tasks/coordinate";
    internal static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    internal static async Task<byte[]?> ReadAsync(Stream stream,CancellationToken ct)
    {
        using var output=new MemoryStream();var buffer=new byte[8192];int read;
        while((read=await stream.ReadAsync(buffer,ct))>0) {if(output.Length+read>512*1024) return null;await output.WriteAsync(buffer.AsMemory(0,read),ct);}return output.ToArray();
    }
    private async Task<(string Url,string Key,Guid Cluster)> PeerAsync(Guid peer,CancellationToken ct)
    {
        await using var cmd=db.CreateCommand("SELECT a.public_key,a.cluster_id FROM cluster_peer_admissions a JOIN cluster_identity i ON i.id=a.cluster_id WHERE a.peer_id=@peer AND a.revoked_at IS NULL");
            cmd.Parameters.AddWithValue("peer",peer);await using var r=await cmd.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct)
            || health.AuthenticatedEndpoint(peer) is not {} endpoint) throw new InvalidOperationException("Serveur admis et adresse authentifiée requis.");return(endpoint,r.GetString(0),r.GetGuid(1));
    }
    private HttpRequestMessage Request(string url,ClusterTaskPeerRequest data)
    {
        var body=JsonSerializer.SerializeToUtf8Bytes(data,Json);var time=DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var request=new HttpRequestMessage(HttpMethod.Post,new Uri(new Uri(url),Path)){Content=new ByteArrayContent(body)};
        request.Headers.Add(ClusterReconciliationSignature.PeerHeader,identity.PeerId.ToString("D"));
            request.Headers.Add(ClusterReconciliationSignature.TimeHeader,time);request.Headers.Add(ClusterReconciliationSignature.SignatureHeader,
            ClusterReconciliationSignature.Create(identity,time,body));return request;
    }
    public async Task<ClusterTaskPeerReply?> CallAsync(Guid peer,string kind,string? payload,CancellationToken ct)
    {
        var remote=await PeerAsync(peer,ct);var nonce=Guid.NewGuid();using var request=Request(remote.Url,new(ClusterTaskMessageVersions.Current,remote.Cluster,nonce,kind,Payload:payload));
        using var response=await clients.CreateClient("ClusterTaskTransfer").SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);if(!response.IsSuccessStatusCode) return null;
        var body=await ReadAsync(await response.Content.ReadAsStreamAsync(ct),ct);if(body is null) return null;
        string? Header(string name)=>response.Headers.TryGetValues(name,out var values)?values.FirstOrDefault():null;
        if(!Guid.TryParse(Header(ClusterReconciliationSignature.PeerHeader),out var author)||author!=peer||!ClusterReconciliationSignature.Verify(remote.Key,
            peer,Header(ClusterReconciliationSignature.TimeHeader),Header(ClusterReconciliationSignature.SignatureHeader),body)) return null;
        var reply=JsonSerializer.Deserialize<ClusterTaskPeerReply>(body,Json);return reply is not null && ClusterTaskMessageVersions.IsSupported(reply.Version) && reply.Nonce==nonce && reply.ClusterId==remote.Cluster && reply.PeerId==peer?reply:null;
    }
    public async Task FetchAsync(Guid peer,Guid execution,ClusterTaskArtifact artifact,CancellationToken ct)
    {
        var key=(execution,artifact.Sha256,artifact.Bytes);Lazy<Task>? candidate=null;
        candidate=new(async()=>{try {await FetchCoreAsync(peer,execution,artifact,ct);} finally {inflight.TryRemove(key,out _);}});
        await inflight.GetOrAdd(key,candidate).Value.WaitAsync(ct);
    }
    private async Task FetchCoreAsync(Guid peer,Guid execution,ClusterTaskArtifact artifact,CancellationToken ct)
    {
        if(await artifacts.IsCachedAsync(execution,artifact.Sha256,artifact.Bytes,ct)) return;
        var remote=await PeerAsync(peer,ct);using var request=Request(remote.Url,new(ClusterTaskMessageVersions.Current,remote.Cluster,Guid.NewGuid(),"blob",execution,artifact.Sha256));
        using var response=await clients.CreateClient("ClusterTaskTransfer").SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength is {} bytes && bytes!=artifact.Bytes) throw new InvalidDataException("Taille d’artefact incorrecte.");
        await artifacts.CacheAsync(execution,artifact.Sha256,artifact.Bytes,await response.Content.ReadAsStreamAsync(ct),ct);
    }
    internal static void SignResponse(HttpResponse response,ClusterPeerIdentityStore identity,byte[] body)
    {
        var time=DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        response.Headers[ClusterReconciliationSignature.PeerHeader]=identity.PeerId.ToString("D");
            response.Headers[ClusterReconciliationSignature.TimeHeader]=time;
            response.Headers[ClusterReconciliationSignature.SignatureHeader]=ClusterReconciliationSignature.Create(identity,time,body);
    }
}
