using System.Globalization;
using System.Net;
using System.Text.Json;
using Encoding.Api.Auth;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Cluster;
using Encoding.Contracts.Executions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.RateLimiting;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class ClusterTaskPeerClientTests
{
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {public HttpClient CreateClient(string name)=>client;}
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>handler(request,ct);}
    private static ClusterPeerIdentityStore Identity(string root)=>new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["PluginSettings:KeyDirectory"]=root}).Build(),new EphemeralDataProtectionProvider());

    [Theory]
    [InlineData("valid",true)]
    [InlineData("old-nonce",false)]
    [InlineData("wrong-peer",false)]
    [InlineData("wrong-cluster",false)]
    [InlineData("invalid-signature",false)]
    [InlineData("unsupported",false)]
    [InlineData("old-version",false)]
    [InlineData("oversized",false)]
    [InlineData("malformed",false)]
    public async Task CapabilityProofMustBeSignedAndBoundToThisRequest(string scenario,bool expected)
    {
        var root=Path.Combine(Path.GetTempPath(),"task-probe-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            using var local=Identity(Path.Combine(root,"local"));using var remote=Identity(Path.Combine(root,"remote"));var cluster=Guid.NewGuid();
            using var http=new HttpClient(new Handler(async(request,ct)=>
            {
                var body=await request.Content!.ReadAsByteArrayAsync(ct);
                string Header(string name)=>request.Headers.GetValues(name).Single();
                Assert.True(ClusterReconciliationSignature.Verify(local.PublicKey,local.PeerId,Header(ClusterReconciliationSignature.TimeHeader),Header(ClusterReconciliationSignature.SignatureHeader),body));
                var probe=JsonSerializer.Deserialize<ClusterTaskProbe>(body,ClusterTaskPeerClient.Json)!;
                Assert.Equal(ClusterTaskPeerClient.ProbeKind,probe.Kind);
                var proof=new ClusterTaskProbeResponse(scenario=="old-version"?0:1,scenario=="wrong-cluster"?Guid.NewGuid():cluster,
                    scenario=="old-nonce"?Guid.NewGuid():probe.Nonce,scenario=="wrong-peer"?Guid.NewGuid():remote.PeerId,scenario=="unsupported"?[]:[SourceWriteProtocol.Capability]);
                var result=JsonSerializer.SerializeToUtf8Bytes(proof,ClusterTaskPeerClient.Json);
                if(scenario=="oversized") result=new byte[8193];if(scenario=="malformed") result=[(byte)'{'];
                var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(result)};
                var time=DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                response.Headers.Add(ClusterReconciliationSignature.PeerHeader,remote.PeerId.ToString("D"));response.Headers.Add(ClusterReconciliationSignature.TimeHeader,time);
                response.Headers.Add(ClusterReconciliationSignature.SignatureHeader,scenario=="invalid-signature"?"invalid":ClusterReconciliationSignature.Create(remote,time,result));return response;
            }));
            Assert.Equal(expected,await new ClusterTaskPeerClient(local,new Factory(http)).SupportsSourceWritesAsync(cluster,remote.PeerId,remote.PublicKey,"http://peer/",default));
        }
        finally {Directory.Delete(root,true);}
    }

    [Fact]
    public async Task RealTaskStatusEndpointRequiresAdmissionAndProvesItsCapability()
    {
        var root=Path.Combine(Path.GetTempPath(),"task-status-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            await using var db=await TestDatabase.CreateSqliteAsync("task-status");await DatabaseMigrator.ApplyAsync(db.Db,Harness.RepositoryRoot(),default);
            var clusters=new ClusterIdentityRepository(db.Db);var cluster=(await clusters.GetAsync(default)).Id;
            using var local=Identity(Path.Combine(root,"local"));using var remote=Identity(Path.Combine(root,"remote"));
            using var keys=new ClusterDataKeyStore(cluster,new byte[32]);var admissions=new ClusterPeerAdmissionRepository(db.Db,clusters,remote,keys);
            var builder=WebApplication.CreateBuilder();builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(clusters);builder.Services.AddSingleton(remote);builder.Services.AddSingleton(admissions);
            builder.Services.AddRateLimiter(options=>options.AddPolicy(AuthExtensions.ClusterMeshPolicy,_=>RateLimitPartition.GetNoLimiter("test")));
            await using var app=builder.Build();app.UseRateLimiter();app.MapClusterTaskEndpoints();await app.StartAsync();
            using var http=app.GetTestClient();var client=new ClusterTaskPeerClient(local,new Factory(http));
            Assert.False(await client.SupportsSourceWritesAsync(cluster,remote.PeerId,remote.PublicKey,"http://localhost/",default));
            await using(var admit=db.Db.CreateCommand("INSERT INTO cluster_peer_admissions(cluster_id,peer_id,public_key,approved_by_peer_id,key_fingerprint,issued_at,signature) VALUES(@cluster,@peer,@key,@peer,'test-key',clock_timestamp(),'test-signature')"))
            {
                admit.Parameters.AddWithValue("cluster",cluster);admit.Parameters.AddWithValue("peer",local.PeerId);admit.Parameters.AddWithValue("key",local.PublicKey);await admit.ExecuteNonQueryAsync();
            }
            Assert.True(await client.SupportsSourceWritesAsync(cluster,remote.PeerId,remote.PublicKey,"http://localhost/",default));
            await using(var revoke=db.Db.CreateCommand("UPDATE cluster_peer_admissions SET revoked_at=clock_timestamp()")) await revoke.ExecuteNonQueryAsync();
            Assert.False(await client.SupportsSourceWritesAsync(cluster,remote.PeerId,remote.PublicKey,"http://localhost/",default));
        }
        finally {Directory.Delete(root,true);}
    }
}
