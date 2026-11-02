using System.Globalization;
using System.Text.Json;
using Encoding.Contracts.Cluster;
using Encoding.Contracts.Executions;

namespace Encoding.Api.Infrastructure;

public interface IClusterTaskPeerClient
{
    Task<bool> SupportsSourceWritesAsync(Guid cluster,Guid peer,string publicKey,string url,CancellationToken ct);
}

/// <summary>A nonce binds the capability proof to this call. The existing mesh exchange format is unchanged.</summary>
public sealed class ClusterTaskPeerClient(ClusterPeerIdentityStore identity,IHttpClientFactory clients) : IClusterTaskPeerClient
{
    internal const string Path="api/v1/cluster/tasks/status";
    internal const string ProbeKind="source-write-status";
    internal const int Version=1;
    internal static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    internal static async Task<byte[]?> ReadAsync(HttpContent content,CancellationToken ct)
    {
        if(content.Headers.ContentLength>8192) return null;
        await using var stream=await content.ReadAsStreamAsync(ct);var bytes=new byte[8193];var count=0;
        while(count<bytes.Length)
        {
            var read=await stream.ReadAsync(bytes.AsMemory(count),ct);if(read==0) break;count+=read;
        }
        return count>8192?null:bytes[..count];
    }
    public async Task<bool> SupportsSourceWritesAsync(Guid cluster,Guid peer,string publicKey,string url,CancellationToken ct)
    {
        var nonce=Guid.NewGuid();var body=JsonSerializer.SerializeToUtf8Bytes(new ClusterTaskProbe(Version,cluster,nonce,ProbeKind),Json);
        var time=DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var request=new HttpRequestMessage(HttpMethod.Post,new Uri(new Uri(url),Path)){Content=new ByteArrayContent(body)};
        request.Headers.Add(ClusterReconciliationSignature.PeerHeader,identity.PeerId.ToString("D"));
        request.Headers.Add(ClusterReconciliationSignature.TimeHeader,time);
        request.Headers.Add(ClusterReconciliationSignature.SignatureHeader,ClusterReconciliationSignature.Create(identity,time,body));
        try
        {
            using var response=await clients.CreateClient("ClusterReconciliation").SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
            if(!response.IsSuccessStatusCode) return false;
            var received=await ReadAsync(response.Content,ct);if(received is null) return false;
            string? Header(string name)=>response.Headers.TryGetValues(name,out var values)?values.FirstOrDefault():null;
            if(!Guid.TryParse(Header(ClusterReconciliationSignature.PeerHeader),out var signer) || signer!=peer
                || !ClusterReconciliationSignature.Verify(publicKey,peer,Header(ClusterReconciliationSignature.TimeHeader),
                    Header(ClusterReconciliationSignature.SignatureHeader),received)) return false;
            var proof=JsonSerializer.Deserialize<ClusterTaskProbeResponse>(received,Json);
            return proof is { ProtocolVersion:Version } && proof.ClusterId==cluster && proof.Nonce==nonce && proof.PeerId==peer
                && proof.Capabilities?.Contains(SourceWriteProtocol.Capability,StringComparer.Ordinal)==true;
        }
        catch(Exception exception) when(exception is HttpRequestException or IOException or JsonException || exception is OperationCanceledException && !ct.IsCancellationRequested) { return false; }
    }
}
