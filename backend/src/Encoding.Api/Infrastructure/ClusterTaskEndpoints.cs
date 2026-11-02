using System.Globalization;
using System.Text.Json;
using Encoding.Api.Auth;
using Encoding.Contracts.Cluster;
using Encoding.Contracts.Executions;

namespace Encoding.Api.Infrastructure;

public static class ClusterTaskEndpoints
{
    public static void MapClusterTaskEndpoints(this WebApplication app)
    {
        app.MapPost("/"+ClusterTaskPeerClient.Path,async(HttpRequest request,HttpResponse response,ClusterIdentityRepository clusters,
            ClusterPeerIdentityStore identity,ClusterPeerAdmissionRepository admissions,CancellationToken ct)=>
        {
            var cluster=await clusters.GetAsync(ct);
            if(!Guid.TryParse(request.Headers[ClusterReconciliationSignature.PeerHeader],out var peer)
                || await admissions.GetActivePublicKeyAsync(cluster.Id,peer,ct) is not {} key) return (IResult)Results.Unauthorized();
            using var content=new StreamContent(request.Body);
            var body=await ClusterTaskPeerClient.ReadAsync(content,ct);
            if(body is null) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            if(!ClusterReconciliationSignature.Verify(key,peer,request.Headers[ClusterReconciliationSignature.TimeHeader],
                request.Headers[ClusterReconciliationSignature.SignatureHeader],body)) return Results.Unauthorized();
            ClusterTaskProbe? probe;
            try {probe=JsonSerializer.Deserialize<ClusterTaskProbe>(body,ClusterTaskPeerClient.Json);}
            catch(JsonException) {return Results.BadRequest();}
            if(probe is not { ProtocolVersion:ClusterTaskPeerClient.Version,Kind:ClusterTaskPeerClient.ProbeKind } || probe.ClusterId!=cluster.Id
                || probe.Nonce==Guid.Empty) return Results.BadRequest();
            var result=JsonSerializer.SerializeToUtf8Bytes(new ClusterTaskProbeResponse(ClusterTaskPeerClient.Version,cluster.Id,probe.Nonce,identity.PeerId,
                [SourceWriteProtocol.Capability]),ClusterTaskPeerClient.Json);
            var time=DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            response.Headers[ClusterReconciliationSignature.PeerHeader]=identity.PeerId.ToString("D");
            response.Headers[ClusterReconciliationSignature.TimeHeader]=time;
            response.Headers[ClusterReconciliationSignature.SignatureHeader]=ClusterReconciliationSignature.Create(identity,time,result);
            return Results.Bytes(result,"application/json");
        }).AllowAnonymous().RequireRateLimiting(AuthExtensions.ClusterMeshPolicy).WithTags("Cluster");
    }
}
