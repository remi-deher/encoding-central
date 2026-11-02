using System.Data.Common;
using System.Diagnostics;
using Encoding.Api.Infrastructure.Data.Sqlite;
using Xunit.Abstractions;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Auth;
using Encoding.Api.Executions;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Nodes;
using Encoding.Api.Plugins;
using Encoding.NodeAgent.Runtime;
using Encoding.NodeAgent.Runtime.Media;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed partial class ClusterTaskTransferTests(ITestOutputHelper output)
{
    private sealed class Wire:IHttpClientFactory
    {
        internal readonly Dictionary<string,HttpClient> Servers=new();
        internal bool LoseAck,LoseActivation,CorruptBlob,DelayBlobs;internal int BlobRequests;internal Func<Task>? BeforeAcknowledgement;
        public HttpClient CreateClient(string name)=>new(new Bridge(this));
        private sealed class Bridge(Wire wire):HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
            {
                var bytes=await request.Content!.ReadAsByteArrayAsync(ct);var message=JsonSerializer.Deserialize<ClusterTaskPeerRequest>(bytes,TaskCheckpointRepository.Json)!;
                if(message.Kind=="blob") {Interlocked.Increment(ref wire.BlobRequests);if(wire.DelayBlobs) await Task.Delay(30,ct);}
                if(wire.LoseActivation&&message.Kind=="activate") return new(HttpStatusCode.GatewayTimeout);
                using var clone=new HttpRequestMessage(request.Method,request.RequestUri!.PathAndQuery){Content=new ByteArrayContent(bytes)};
                foreach(var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key,header.Value);
                var result=await wire.Servers[request.RequestUri.Host].SendAsync(clone,HttpCompletionOption.ResponseHeadersRead,ct);
                if(message.Kind=="receive" && wire.BeforeAcknowledgement is {} before) await before();
                if(wire.LoseAck&&message.Kind=="receive") {result.Dispose();return new(HttpStatusCode.GatewayTimeout);}
                if(wire.CorruptBlob&&message.Kind=="blob") {result.Dispose();return new(HttpStatusCode.OK){Content=new ByteArrayContent([1,2,3,4])};}
                return result;
            }
        }
    }
    private sealed class Clock:TimeProvider
    {
        private DateTimeOffset now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>now;
        internal void Advance()=>now+=TimeSpan.FromSeconds(140);
    }
    private sealed class Pair:IAsyncDisposable
    {
        internal readonly Clock Clock=new();internal readonly Wire Wire=new();internal Site A=null!,B=null!;internal readonly Guid Cluster=Guid.NewGuid(),Library=Guid.NewGuid();internal readonly byte[] Key=RandomNumberGenerator.GetBytes(32);
        internal static async Task<Pair> CreateAsync()
        {
            var p=new Pair();p.A=await Site.CreateAsync(p,"a.test");p.B=await Site.CreateAsync(p,"b.test");await p.A.AdmitAsync(p.B);await p.B.AdmitAsync(p.A);
            p.A.Health.ConfirmCatchUp("http://b.test/",p.B.Identity.PeerId,true);p.A.Health.ConfirmSourceWriteSupport(p.B.Identity.PeerId,true);
            p.B.Health.ConfirmCatchUp("http://a.test/",p.A.Identity.PeerId,true);p.B.Health.ConfirmSourceWriteSupport(p.A.Identity.PeerId,true);return p;
        }
        internal async Task SyncAsync(Site from,Site to,bool metadata=false)
        {
            if(metadata) await from.Entities.CaptureLocalChangesAsync(default);
            var source=new ClusterReconciliationRepository(from.Db.Db);var target=new ClusterReconciliationRepository(to.Db.Db);
            var changes=await source.GetEventsAfterAsync(Cluster,await target.GetVectorAsync(default),32,default);
            await target.ApplyIncomingAsync(changes,default);if(metadata) await to.Entities.ApplyPendingAsync(default);await to.Ownership.ProjectAsync(default);
            for(var i=0;i<4;i++) await to.Projection.ProjectAsync(default);
        }
        public async ValueTask DisposeAsync(){await A.DisposeAsync();await B.DisposeAsync();}
    }
    private sealed class Site:IAsyncDisposable
    {
        internal TestDatabase Db=null!;internal ClusterPeerIdentityStore Identity=null!;internal ClusterReconciliationHealth Health=new();internal TaskOwnershipRepository Ownership=null!;internal TaskCheckpointRepository Checkpoints=null!;internal TaskCoordinationProjection Projection=null!;internal RuntimeArtifactStore Artifacts=null!;internal ClusterTaskCoordinator Coordinator=null!;internal Guid Node=Guid.NewGuid();internal WebApplication App=null!;internal RuntimeRepository Runtime=null!;internal ClusterTaskTransport Transport=null!;internal ClusterEntityReconciliation Entities=null!;internal ClusterTaskMaintenance Maintenance=null!;internal string Root=>directory;
        private ClusterDataKeyStore keys=null!;private readonly string directory=Path.Combine(Path.GetTempPath(),"server-transfer-"+Guid.NewGuid().ToString("N"));private Pair pair=null!;
        internal static async Task<Site> CreateAsync(Pair pair,string host)
        {
            var s=new Site{pair=pair};Directory.CreateDirectory(s.directory);s.Db=await TestDatabase.CreateSqliteAsync("server-transfer");await DatabaseMigrator.ApplyAsync(s.Db.Db,Harness.RepositoryRoot(),default);
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["PluginSettings:KeyDirectory"]=Path.Combine(s.directory,"keys"),["Runtime:ArtifactDirectory"]=Path.Combine(s.directory,"artifacts")}).Build();var protection=new EphemeralDataProtectionProvider();
            s.Identity=new(config,protection);s.keys=new(pair.Cluster,pair.Key);s.Entities=new(s.Db.Db,s.keys,protection,NullLogger<ClusterEntityReconciliation>.Instance);await new ClusterIdentityRepository(s.Db.Db).AdoptAsync(pair.Cluster,"pair",default);s.Artifacts=new(s.Db.Db,config);var settings=new PluginConfigurationRepository(s.Db.Db,protection);
            s.Checkpoints=new(s.Identity,s.keys,settings,s.Artifacts);s.Projection=new(s.Db.Db,s.Identity,s.Checkpoints,pair.Clock);s.Ownership=new(s.Db.Db,s.Identity,s.Health,s.Checkpoints,s.Projection);await s.Ownership.InitializeAsync(default);s.Health.Enable();
            var(registry,_,eligibility)=Harness.Services();var nodes=new NodeRepository(s.Db.Db);var local=new LocalNodeRegistry(nodes);var transport=new ClusterTaskTransport(s.Db.Db,s.Identity,s.Health,pair.Wire,s.Artifacts);s.Transport=transport;var barrier=new ClusterSourceWriteBarrier(s.Db.Db,s.Health,s.Identity,coordination:s.Projection);
            s.Runtime=new(s.Db.Db,eligibility,registry,new NodePresenceOptions(TimeSpan.FromHours(1)),settings,new NodeGroupRepository(s.Db.Db),barrier,s.Ownership);
            await using(var seed=s.Db.Db.CreateCommand("INSERT INTO libraries(id,name) VALUES(@library,'library'); INSERT INTO cluster_nodes(id,name,roles,max_concurrent_tasks,capabilities,last_seen_at) VALUES(@node,'local',ARRAY['server','worker'],2,@caps,clock_timestamp()); INSERT INTO library_node_accesses(library_id,node_id,mode,local_path,can_read,can_write) VALUES(@library,@node,'direct',@path,true,true)"))
            {seed.Parameters.AddWithValue("library",pair.Library);seed.Parameters.AddWithValue("node",s.Node);seed.Parameters.AddWithValue("path",s.directory);seed.Parameters.AddWithValue("caps",SqlKind.Json,JsonSerializer.Serialize(Harness.Capabilities(true),new JsonSerializerOptions(JsonSerializerDefaults.Web)));await seed.ExecuteNonQueryAsync();}
            await local.RegisterAsync(s.Node,default);s.Coordinator=new(s.Db.Db,s.Identity,s.Health,s.Ownership,s.Checkpoints,s.Projection,transport,barrier,local,eligibility,new NodeGroupRepository(s.Db.Db),pair.Clock);
            var builder=WebApplication.CreateBuilder();builder.WebHost.UseTestServer();builder.Services.AddAuthorization(o=>o.DefaultPolicy=new AuthorizationPolicyBuilder().RequireAssertion(_=>true).Build());
            builder.Services.AddRateLimiter(o=>o.AddFixedWindowLimiter(AuthExtensions.ClusterMeshPolicy,limits=>{limits.PermitLimit=10000;limits.Window=TimeSpan.FromMinutes(1);}));
            builder.Services.AddSingleton<DbDataSource>(s.Db.Db);builder.Services.AddSingleton(s.Identity);builder.Services.AddSingleton(new ClusterIdentityRepository(s.Db.Db));builder.Services.AddSingleton(new ClusterPeerAdmissionRepository(s.Db.Db,new(s.Db.Db),s.Identity,s.keys));
            builder.Services.AddSingleton(s.Coordinator);builder.Services.AddSingleton(s.Ownership);builder.Services.AddSingleton(s.Checkpoints);builder.Services.AddSingleton(s.Projection);builder.Services.AddSingleton(s.Artifacts);builder.Services.AddSingleton<ClusterTaskPeerHandler>();
            s.Maintenance=new(s.Db.Db,s.Identity,s.Health,s.Checkpoints,s.Projection,s.Coordinator,transport,s.Artifacts,logger:NullLogger<ClusterTaskMaintenance>.Instance);builder.Services.AddSingleton(s.Maintenance);
            s.App=builder.Build();s.App.UseRateLimiter();s.App.UseAuthorization();s.App.MapClusterTaskCoordinationEndpoints();
            var history=new ExecutionRepository(s.Db.Db);s.App.MapGet("/api/v1/executions",async(int? limit,int? offset)=>await history.OverviewAsync(new ExecutionFilterQuery(Limit:limit??50,Offset:offset??0),default));
            await s.App.StartAsync();pair.Wire.Servers[host]=s.App.GetTestClient();return s;
        }
        internal async Task AdmitAsync(Site other)
        {
            await using var cmd=Db.Db.CreateCommand("INSERT INTO cluster_peer_admissions(cluster_id,peer_id,public_key,approved_by_peer_id,key_fingerprint,issued_at,signature) VALUES(@cluster,@peer,@key,@peer,'test',clock_timestamp(),'test')");cmd.Parameters.AddWithValue("cluster",pair.Cluster);cmd.Parameters.AddWithValue("peer",other.Identity.PeerId);cmd.Parameters.AddWithValue("key",other.Identity.PublicKey);await cmd.ExecuteNonQueryAsync();
        }
        internal async Task<Guid> EnqueueAsync(string path="a.mkv",FlowDefinition? definition=null)
        {
            var(registry,_,_)=Harness.Services();var flow=definition??Harness.Chain(Harness.Node(registry,"input","base-input-video"),Harness.Node(registry,"end","base-complete-flow"));var id=Guid.NewGuid();
            await using var c=await Db.Db.OpenConnectionAsync();await using var tx=await c.BeginTransactionAsync();Assert.True(await RuntimeRepository.EnqueueInTransactionAsync(new(id,pair.Library,path,flow.Id,flow.Version),flow,c,tx,default));await Ownership.CreateAsync([id],c,tx,default);await tx.CommitAsync();await Ownership.PrepareClaimsAsync(default);return id;
        }
        internal async Task SeedAsync(int count,string prefix)
        {
            var(registry,_,_)=Harness.Services();var flow=Harness.Chain(Harness.Node(registry,"input","base-input-video"),Harness.Node(registry,"end","base-complete-flow"));var ids=new List<Guid>();
            await using var c=await Db.Db.OpenConnectionAsync();await using var tx=await c.BeginTransactionAsync();
            for(var i=0;i<count;i++) {var id=Guid.NewGuid();Assert.True(await RuntimeRepository.EnqueueInTransactionAsync(new(id,pair.Library,$"load/{prefix}-{i}.mkv",flow.Id,flow.Version),flow,c,tx,default));ids.Add(id);}
            await Ownership.CreateAsync(ids,c,tx,default);await tx.CommitAsync();Assert.True(await Ownership.PrepareClaimsAsync(default));
        }
        internal async Task<string> AddCheckpointAsync(Guid id,byte[] bytes,JsonElement? before=null,JsonElement? after=null,byte[]? original=null,bool handoff=false)
        {
            var digest=Convert.ToHexStringLower(SHA256.HashData(bytes));Directory.CreateDirectory(Artifacts.DirectoryFor(id));await File.WriteAllBytesAsync(Artifacts.PathFor(id,digest),bytes);
            var metadata=JsonSerializer.SerializeToElement(new{videoCodec="hevc"});var context=new RuntimeContext(digest,".mkv",new Dictionary<string,JsonElement>(),before??metadata,after??metadata,[]);
            var cp=new RuntimeCheckpoint(handoff?"handoff":"input",Guid.NewGuid(),"success",handoff?"remux":"end",context,null,Handoff:handoff?new("server",pair.B.Identity.PeerId):null);
            await using var c=await Db.Db.OpenConnectionAsync();await using var tx=await c.BeginTransactionAsync();await using var cmd=Sql.Command("INSERT INTO execution_artifacts(execution_id,sha256,byte_length) VALUES(@id,@sha,@bytes); UPDATE execution_work SET source_sha=@source,before_metadata=@before WHERE execution_id=@id; UPDATE executions SET revision=1 WHERE id=@id; INSERT INTO execution_steps(execution_id,node_id,invocation_id,completed_at,completed_revision,idempotent,checkpoint) VALUES(@id,@node,@invoke,clock_timestamp(),1,true,@cp)",c,tx);cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("sha",digest);cmd.Parameters.AddWithValue("bytes",bytes.Length);cmd.Parameters.AddWithValue("source",original is null?digest:Convert.ToHexStringLower(SHA256.HashData(original)));cmd.Parameters.AddWithValue("before",SqlKind.Json,context.BeforeMetadata!.Value.GetRawText());cmd.Parameters.AddWithValue("node",cp.NodeId);cmd.Parameters.AddWithValue("invoke",cp.InvocationId);cmd.Parameters.AddWithValue("cp",SqlKind.Json,JsonSerializer.Serialize(cp,TaskCheckpointRepository.Json));await cmd.ExecuteNonQueryAsync();
            if(original is not null)
            {
                var sourceHash=Convert.ToHexStringLower(SHA256.HashData(original));await Artifacts.CacheAsync(id,sourceHash,original.Length,new MemoryStream(original),default);
                await using var source=Sql.Command("INSERT INTO execution_artifacts(execution_id,sha256,byte_length) VALUES(@id,@sha,@bytes) ON CONFLICT DO NOTHING",c,tx);source.Parameters.AddWithValue("id",id);source.Parameters.AddWithValue("sha",sourceHash);source.Parameters.AddWithValue("bytes",original.Length);await source.ExecuteNonQueryAsync();
            }
            await Checkpoints.SaveAsync(id,c,tx,default);await tx.CommitAsync();await Projection.ProjectAsync(default);return digest;
        }
        public async ValueTask DisposeAsync(){await App.DisposeAsync();Identity.Dispose();keys.Dispose();await Db.DisposeAsync();if(Directory.Exists(directory)) Directory.Delete(directory,true);}
    }
    [Fact]
    public async Task CooperativeTransferMovesVerifiedCheckpointAndOnlyThenActivatesTarget()
    {
        await using var pair=await Pair.CreateAsync();var id=await pair.A.EnqueueAsync();var sha=await pair.A.AddCheckpointAsync(id,[3,5,7,9]);await pair.SyncAsync(pair.A,pair.B);
        var moved=await pair.A.Coordinator.MoveAsync(id,pair.B.Identity.PeerId,default);Assert.True(moved.Success,moved.Detail);
        Assert.Equal(pair.B.Identity.PeerId,(await pair.A.Ownership.GetAsync(id,default))!.OwnerPeerId);Assert.Equal(pair.B.Identity.PeerId,(await pair.B.Ownership.GetAsync(id,default))!.OwnerPeerId);
        Assert.Equal(2,(await pair.B.Ownership.GetAsync(id,default))!.Epoch);Assert.Equal(new byte[]{3,5,7,9},await File.ReadAllBytesAsync(pair.B.Artifacts.PathFor(id,sha)));
        await using var resumed=pair.B.Db.Db.CreateCommand("SELECT checkpoint FROM execution_steps WHERE execution_id=@id");resumed.Parameters.AddWithValue("id",id);var cp=JsonSerializer.Deserialize<RuntimeCheckpoint>((string)(await resumed.ExecuteScalarAsync())!,TaskCheckpointRepository.Json)!;Assert.Equal("end",cp.NextNodeId);Assert.Equal(cp.Context.BeforeMetadata?.GetRawText(),cp.Context.CurrentMetadata?.GetRawText());
    }
    [Fact]
    public async Task LostAcknowledgementKeepsSourceOwnerAndTargetInactive()
    {
        await using var pair=await Pair.CreateAsync();var id=await pair.A.EnqueueAsync();await pair.SyncAsync(pair.A,pair.B);pair.Wire.LoseAck=true;
        Assert.False((await pair.A.Coordinator.MoveAsync(id,pair.B.Identity.PeerId,default)).Success);Assert.Equal("active",(await pair.A.Ownership.GetAsync(id,default))!.State);Assert.Equal(pair.A.Identity.PeerId,(await pair.B.Ownership.GetAsync(id,default))!.OwnerPeerId);
        await using var work=pair.B.Db.Db.CreateCommand("SELECT count(*) FROM execution_work");Assert.Equal(0L,Convert.ToInt64(await work.ExecuteScalarAsync()));
        await pair.SyncAsync(pair.A,pair.B);pair.Wire.LoseAck=false;Assert.True((await pair.A.Coordinator.MoveAsync(id,pair.B.Identity.PeerId,default)).Success);
    }
    [Fact]
    public async Task LostActivationLeavesBothInactiveUntilJournalCatchUp()
    {
        await using var pair=await Pair.CreateAsync();var id=await pair.A.EnqueueAsync();await pair.SyncAsync(pair.A,pair.B);pair.Wire.LoseActivation=true;
        Assert.True((await pair.A.Coordinator.MoveAsync(id,pair.B.Identity.PeerId,default)).Success);Assert.Equal("transferred",(await pair.A.Ownership.GetAsync(id,default))!.State);Assert.Equal(pair.A.Identity.PeerId,(await pair.B.Ownership.GetAsync(id,default))!.OwnerPeerId);
        await pair.SyncAsync(pair.A,pair.B);Assert.Equal("active",(await pair.B.Ownership.GetAsync(id,default))!.State);Assert.Equal(pair.B.Identity.PeerId,(await pair.B.Ownership.GetAsync(id,default))!.OwnerPeerId);
    }
    [Fact]
    public async Task CorruptArtifactPreventsAcknowledgementAndActivation()
    {
        await using var pair=await Pair.CreateAsync();var id=await pair.A.EnqueueAsync();await pair.A.AddCheckpointAsync(id,[2,4,6,8]);await pair.SyncAsync(pair.A,pair.B);pair.Wire.CorruptBlob=true;
        Assert.False((await pair.A.Coordinator.MoveAsync(id,pair.B.Identity.PeerId,default)).Success);Assert.Equal(pair.A.Identity.PeerId,(await pair.A.Ownership.GetAsync(id,default))!.OwnerPeerId);
    }
    [Fact]
    public async Task ManualRecoveryRequiresNeutralizationGraceAndCachedArtifacts()
    {
        await using var pair=await Pair.CreateAsync();var id=await pair.A.EnqueueAsync();var sha=await pair.A.AddCheckpointAsync(id,[2,4,6,8]);await pair.SyncAsync(pair.A,pair.B);
        Assert.False((await pair.B.Coordinator.RecoverAsync(id,default)).Success);Assert.True((await pair.B.Coordinator.NeutralizeAsync(pair.A.Identity.PeerId,new(true,"Installation et workers arrêtés"),default)).Success);
        Assert.False((await pair.B.Coordinator.RecoverAsync(id,default)).Success);
        pair.Clock.Advance();
        Assert.False((await pair.B.Coordinator.RecoverAsync(id,default)).Success);
        await pair.B.Artifacts.CacheAsync(id,sha,4,new MemoryStream([2,4,6,8]),default);Assert.True((await pair.B.Coordinator.RecoverAsync(id,default)).Success);
        await pair.SyncAsync(pair.B,pair.A);await pair.A.Ownership.InitializeAsync(default);Assert.False(await pair.A.Ownership.PrepareClaimsAsync(default));Assert.Equal(pair.B.Identity.PeerId,(await pair.A.Ownership.GetAsync(id,default))!.OwnerPeerId);
    }
    [Fact]
    public async Task ReturningOwnerMustCatchUpAndCannotReclaimTransferredWorkAfterReadmission()
    {
        await using var pair=await Pair.CreateAsync();var id=await pair.A.EnqueueAsync();await pair.A.AddCheckpointAsync(id,[2,4,6,8]);await pair.SyncAsync(pair.A,pair.B);
        Assert.True((await pair.B.Coordinator.NeutralizeAsync(pair.A.Identity.PeerId,new(true,"Installation et workers arrêtés"),default)).Success);
        pair.Clock.Advance();
        var sha=Convert.ToHexStringLower(SHA256.HashData(new byte[]{2,4,6,8}));await pair.B.Artifacts.CacheAsync(id,sha,4,new MemoryStream([2,4,6,8]),default);
        Assert.True((await pair.B.Coordinator.RecoverAsync(id,default)).Success);
        Assert.False((await pair.B.Coordinator.ReadmitAsync(pair.A.Identity.PeerId,default)).Success);
        await pair.SyncAsync(pair.B,pair.A);
        var readmitted=await pair.B.Coordinator.ReadmitAsync(pair.A.Identity.PeerId,default);Assert.True(readmitted.Success,readmitted.Detail);
        await pair.SyncAsync(pair.B,pair.A);Assert.True(await pair.A.Ownership.PrepareClaimsAsync(default));
        await using var active=pair.A.Db.Db.CreateCommand($"SELECT {TaskOwnershipRepository.ActiveSql("@id")}");active.Parameters.AddWithValue("id",id);Assert.False(await active.ScalarBoolAsync(default));
    }
    [Fact]
    public async Task ConcurrentCreationsConvergeToOneWinnerAndKeepTheOtherResultQuarantined()
    {
        await using var pair=await Pair.CreateAsync();var a=await pair.A.EnqueueAsync();var b=await pair.B.EnqueueAsync();
        await pair.SyncAsync(pair.A,pair.B);await pair.SyncAsync(pair.B,pair.A);
        var aa=await pair.A.Ownership.GetAsync(a,default);var ab=await pair.A.Ownership.GetAsync(b,default);
        var ba=await pair.B.Ownership.GetAsync(a,default);var bb=await pair.B.Ownership.GetAsync(b,default);
        Assert.Equal(aa!.State,ba!.State);Assert.Equal(ab!.State,bb!.State);Assert.Equal(1,new[]{aa,ab}.Count(o=>o.State=="active"));
        Assert.Equal(1,new[]{aa,ab}.Count(o=>o.State=="quarantined"));
    }
    [Fact]
    public async Task RelayedObservationExpiresAndNeverNeutralizesOrTransfersItsSubject()
    {
        await using var pair=await Pair.CreateAsync();var id=await pair.A.EnqueueAsync();await pair.SyncAsync(pair.A,pair.B);
        var observation=new ClusterTaskEvent(2,Guid.NewGuid(),pair.Cluster,pair.A.Identity.PeerId,"observation",Guid.Empty,0,Guid.Empty,SubjectPeerId:pair.B.Identity.PeerId,RecordedAt:pair.Clock.GetUtcNow(),Sequence:1,Reachable:false);
        await using(var c=await pair.A.Db.Db.OpenConnectionAsync()) await using(var tx=await c.BeginTransactionAsync()) {await pair.A.Coordinator.AppendAsync(observation,c,tx,default);await tx.CommitAsync();}
        await pair.SyncAsync(pair.A,pair.B);Assert.Equal(pair.A.Identity.PeerId,(await pair.B.Ownership.GetAsync(id,default))!.OwnerPeerId);
        await using(var proof=pair.B.Db.Db.CreateCommand("SELECT count(*) FROM task_neutralizations")) Assert.Equal(0L,Convert.ToInt64(await proof.ExecuteScalarAsync()));
        pair.Clock.Advance();
        var stale=observation with {EventId=Guid.NewGuid(),Sequence=2};
        await using(var c=await pair.A.Db.Db.OpenConnectionAsync()) await using(var tx=await c.BeginTransactionAsync()) {await pair.A.Coordinator.AppendAsync(stale,c,tx,default);await tx.CommitAsync();}
        await pair.SyncAsync(pair.A,pair.B);
        await using var sequence=pair.B.Db.Db.CreateCommand("SELECT sequence FROM task_peer_observations");Assert.Equal(1L,Convert.ToInt64(await sequence.ExecuteScalarAsync()));
    }
    [Fact]
    public void ExplicitServerHandoffRequiresNewWorkerAndPreservesTheSegmentBoundary()
    {
        var(registry,_,eligibility)=Harness.Services();var target=Guid.NewGuid();
        var relay=Harness.Node(registry,"relay","nodes-handoff",new(){["mode"]=JsonSerializer.SerializeToElement("server"),["targetServerPeerId"]=JsonSerializer.SerializeToElement(target)});
        var flow=Harness.Chain(Harness.Node(registry,"input","base-input-video"),relay,Harness.Node(registry,"end","base-complete-flow"));
        Assert.Equal("server",RuntimeHandoffPolicy.Resolve(relay,null).Mode);Assert.Equal(target,RuntimeHandoffPolicy.Resolve(relay,null).TargetNodeId);
        Assert.Equal(new[]{"input","relay"},RuntimeHandoffPolicy.Segment(flow,"input").Select(n=>n.Id));
        Assert.True(eligibility.CanRun(flow,Harness.Capabilities(true),Guid.NewGuid()));
        Assert.False(eligibility.CanRun(flow,Harness.Capabilities(true).Select(c=>c.ToolId=="plugin/nodes"?c with {Version="1.7.0"}:c).ToArray(),Guid.NewGuid()));
    }

    [MediaToolsFact]
    public async Task EncodingOnAThenRemuxOnBPreservesOriginalMetadataAndPublishesOneVerifiedResult()
    {
        await using var pair=await Pair.CreateAsync();var tools=new ProcessToolRunner();var scanner=new MediaScanner(tools);var ct=CancellationToken.None;
        var original=Path.Combine(pair.A.Root,"a.mkv");var encoded=Path.Combine(pair.A.Root,"encoded.mkv");
        await tools.CheckedAsync("ffmpeg",["-v","error","-y","-f","lavfi","-i","color=c=red:size=32x32:duration=0.3","-c:v","ffv1",original],pair.A.Root,ct);
        await tools.CheckedAsync("ffmpeg",["-v","error","-y","-i",original,"-c:v","libx264",encoded],pair.A.Root,ct);
        var before=await scanner.ScanAsync(original,ct);var afterA=await scanner.ScanAsync(encoded,ct);File.Copy(original,Path.Combine(pair.B.Root,"a.mkv"));
        var(registry,_,_)=Harness.Services();var relay=Harness.Node(registry,"handoff","nodes-handoff",new(){["mode"]=JsonSerializer.SerializeToElement("server"),["targetServerPeerId"]=JsonSerializer.SerializeToElement(pair.B.Identity.PeerId)});
        var flow=Harness.Chain(Harness.Node(registry,"input","base-input-video"),Harness.Node(registry,"encode","base-transcode-video"),relay,Harness.Node(registry,"remux","mkv-remux-matroska",new(){["processingMode"]=JsonSerializer.SerializeToElement("direct")}),Harness.Node(registry,"end","base-complete-flow"));
        var id=await pair.A.EnqueueAsync(definition:flow);await pair.A.AddCheckpointAsync(id,await File.ReadAllBytesAsync(encoded),before,afterA,await File.ReadAllBytesAsync(original),handoff:true);await pair.SyncAsync(pair.A,pair.B);
        Assert.True((await pair.A.Coordinator.MoveAsync(id,pair.B.Identity.PeerId,ct)).Success);
        var lease=await pair.B.Runtime.ClaimAsync(pair.B.Node,ct);Assert.NotNull(lease);Assert.Equal("remux",lease.Checkpoint!.NextNodeId);
        Assert.Equal(before.GetRawText(),lease.Checkpoint.Context.BeforeMetadata!.Value.GetRawText());Assert.Equal(afterA.GetRawText(),lease.Checkpoint.Context.CurrentMetadata!.Value.GetRawText());
        var step=await pair.B.Runtime.StartStepAsync(id,pair.B.Node,new(lease.Epoch,"remux"),ct);Assert.NotNull(step);
        var remux=Path.Combine(pair.B.Root,"remux.mkv");await tools.CheckedAsync("ffmpeg",["-v","error","-y","-i",pair.B.Artifacts.PathFor(id,lease.Checkpoint.Context.CurrentSha256),"-c","copy",remux],pair.B.Root,ct);
        var afterB=await scanner.ValidateAsync(remux,"video",afterA,ct);var digest=Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(remux)));
        await using(var file=File.OpenRead(remux)) Assert.True(await pair.B.Artifacts.PutAsync(id,pair.B.Node,lease.Epoch,digest,file,false,ct));
        var context=lease.Checkpoint.Context with {CurrentSha256=digest,CurrentMetadata=afterB};var execution=new ExecutionRepository(pair.B.Db.Db);
        Assert.True(await pair.B.Runtime.CompleteStepAsync(id,pair.B.Node,new(lease.Epoch,Guid.NewGuid(),lease.Revision,new("remux",step.InvocationId,"success","end",context,null)),ct));
        var terminal=await pair.B.Runtime.StartStepAsync(id,pair.B.Node,new(lease.Epoch,"end"),ct);Assert.NotNull(terminal);
        Assert.True(await pair.B.Runtime.CompleteStepAsync(id,pair.B.Node,new(lease.Epoch,Guid.NewGuid(),(await execution.GetAsync(id,ct))!.Revision,new("end",terminal.InvocationId,null,null,context,"success")),ct));
        var transfer=new RuntimeTransferRepository(pair.B.Db.Db,ownership:pair.B.Ownership);
        Assert.True(await transfer.FinishAsync(id,pair.B.Node,new(lease.Epoch,Guid.NewGuid(),(await execution.GetAsync(id,ct))!.Revision,"succeeded","Remux terminé",digest,".mkv",before,afterB),ct));
        var relative=$".encoding-results/{id:D}/result.mkv";var published=Path.Combine(pair.B.Root,relative);Directory.CreateDirectory(Path.GetDirectoryName(published)!);File.Copy(remux,published);
        Assert.True(await transfer.ConfirmAsync(id,pair.B.Node,new(lease.Epoch,Guid.NewGuid(),(await execution.GetAsync(id,ct))!.Revision,relative,digest),ct));
        Assert.Equal("succeeded",(await execution.GetAsync(id,ct))!.State);Assert.Equal(await File.ReadAllBytesAsync(original),await File.ReadAllBytesAsync(Path.Combine(pair.B.Root,"a.mkv")));
    }

    [LargeCoordinationMeasurementTheory]
    [InlineData(true)]
    public async Task MeasuresTwoServerOwnershipLoad(bool concurrentCapture)
    {
        await using var pair=await Pair.CreateAsync();var setup=Stopwatch.StartNew();await pair.A.SeedAsync(3500,"a");await pair.B.SeedAsync(3500,"b");setup.Stop();
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(12));var claims=new List<double>();var renewals=new List<double>();var overviews=new List<double>();var history=new List<double>();var ids=new HashSet<Guid>();var gate=new object();
        var beforeA=((SqliteDataSource)pair.A.Db.Db).Writer.Snapshot();var beforeB=((SqliteDataSource)pair.B.Db.Db).Writer.Snapshot();var watch=Stopwatch.StartNew();
        var done=0;var passes=0;var start=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var replicationStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Worker(Site site)
        {
            await start.Task;await replicationStarted.Task;
            var finish=new RuntimeTransferRepository(site.Db.Db,ownership:site.Ownership);
            for(var i=0;i<50;)
            {
                await Task.Yield();
                var timer=Stopwatch.StartNew();var lease=await site.Runtime.ClaimAsync(site.Node,timeout.Token);lock(gate) claims.Add(timer.Elapsed.TotalMilliseconds);
                if(lease is null) {await Task.Delay(30,timeout.Token);continue;}
                lock(gate) Assert.True(ids.Add(lease.ExecutionId));timer.Restart();Assert.NotNull(await site.Runtime.RenewAsync(lease.ExecutionId,site.Node,lease.Epoch,timeout.Token));lock(gate) renewals.Add(timer.Elapsed.TotalMilliseconds);
                Assert.True(await finish.FinishAsync(lease.ExecutionId,site.Node,new(lease.Epoch,Guid.NewGuid(),lease.Revision,"failed","Synthetic measurement, no media processing.",null,null),timeout.Token));i++;
            }
            Interlocked.Increment(ref done);
        }
        async Task Reads(string host)
        {
            await start.Task;
            var client=pair.Wire.Servers[host];
            for(var i=0;i<50;i++) {var timer=Stopwatch.StartNew();using var state=await client.GetAsync("/api/v1/executions?limit=50&offset=0",timeout.Token);state.EnsureSuccessStatusCode();lock(gate) overviews.Add(timer.Elapsed.TotalMilliseconds);timer.Restart();using var page=await client.GetAsync("/api/v1/executions?limit=50&offset=200",timeout.Token);page.EnsureSuccessStatusCode();lock(gate) history.Add(timer.Elapsed.TotalMilliseconds);}
        }
        async Task Replicate()
        {
            await start.Task;replicationStarted.SetResult();
            while(Volatile.Read(ref done)<2) {passes++;await pair.SyncAsync(pair.A,pair.B,concurrentCapture);await pair.SyncAsync(pair.B,pair.A,concurrentCapture);await pair.A.Maintenance.CaptureDirtyAsync(timeout.Token);await pair.B.Maintenance.CaptureDirtyAsync(timeout.Token);await Task.Delay(100,timeout.Token);}
        }
        var jobs=new[]{Worker(pair.A),Worker(pair.B),Reads("a.test"),Reads("b.test"),Replicate()};start.SetResult();await Task.WhenAll(jobs);watch.Stop();Assert.True(passes>0);
        var afterA=((SqliteDataSource)pair.A.Db.Db).Writer.Snapshot();var afterB=((SqliteDataSource)pair.B.Db.Db).Writer.Snapshot();
        Assert.Equal(100,ids.Count);Assert.Equal(beforeA.Rejected,afterA.Rejected);Assert.Equal(beforeB.Rejected,afterB.Rejected);Assert.Equal(beforeA.Expired,afterA.Expired);Assert.Equal(beforeB.Expired,afterB.Expired);
        static object Stats(List<double> values) {var v=values.Order().ToArray();double P(double p)=>Math.Round(v[(int)Math.Ceiling(p*v.Length)-1],3);return new{count=v.Length,p50_ms=P(.5),p95_ms=P(.95),p99_ms=P(.99),max_ms=v[^1]};}
        output.WriteLine("TWO_SERVER_MEASUREMENT "+JsonSerializer.Serialize(new{servers=2,sqlite_files=2,host_processes=1,synthetic_workers=2,queued=7000,concurrentCapture,replication_passes=passes,setup_ms=setup.Elapsed.TotalMilliseconds,elapsed_ms=watch.Elapsed.TotalMilliseconds,claim=Stats(claims),renew=Stats(renewals),overview=Stats(overviews),history=Stats(history),writerA=new{admissions=afterA.Admitted-beforeA.Admitted,wait_ms=(afterA.WaitSeconds-beforeA.WaitSeconds)*1000,hold_ms=(afterA.HoldSeconds-beforeA.HoldSeconds)*1000,rejected=afterA.Rejected-beforeA.Rejected,expired=afterA.Expired-beforeA.Expired},writerB=new{admissions=afterB.Admitted-beforeB.Admitted,wait_ms=(afterB.WaitSeconds-beforeB.WaitSeconds)*1000,hold_ms=(afterB.HoldSeconds-beforeB.HoldSeconds)*1000,rejected=afterB.Rejected-beforeB.Rejected,expired=afterB.Expired-beforeB.Expired}}));
    }

    [Fact]
    public async Task RemoteCancellationIsExecutedByTheOwnerAndNeverByAnObserverReplica()
    {
        await using var pair=await Pair.CreateAsync();var id=await pair.A.EnqueueAsync();await pair.SyncAsync(pair.A,pair.B);
        var observer=new ExecutionRepository(pair.B.Db.Db,ownership:pair.B.Ownership,coordinator:pair.B.Coordinator);
        Assert.True(await observer.CancelAsync(id,default));Assert.Equal("cancelled",(await new ExecutionRepository(pair.A.Db.Db).GetAsync(id,default))!.State);
        var next=await pair.A.EnqueueAsync("other.mkv");await pair.SyncAsync(pair.A,pair.B);pair.Wire.Servers.Remove("a.test");
        pair.B.Health.Fail("http://a.test/","partition");Assert.False(await observer.CancelAsync(next,default));Assert.Equal("queued",(await new ExecutionRepository(pair.A.Db.Db).GetAsync(next,default))!.State);
    }

    [Fact]
    public async Task CancellationDuringReceptionCannotBeUndoneByALaterActivation()
    {
        await using var pair=await Pair.CreateAsync();var id=await pair.A.EnqueueAsync();await pair.SyncAsync(pair.A,pair.B);
        pair.Wire.BeforeAcknowledgement=async()=>Assert.True(await new ExecutionRepository(pair.A.Db.Db,ownership:pair.A.Ownership).CancelAsync(id,default));
        Assert.False((await pair.A.Coordinator.MoveAsync(id,pair.B.Identity.PeerId,default)).Success);
        Assert.Equal("cancelled",(await new ExecutionRepository(pair.A.Db.Db).GetAsync(id,default))!.State);
        Assert.Equal(pair.A.Identity.PeerId,(await pair.B.Ownership.GetAsync(id,default))!.OwnerPeerId);
    }

    [Fact]
    public async Task BackgroundCacheAndExplicitTransferShareOneVerifiedDownload()
    {
        await using var pair=await Pair.CreateAsync();var id=await pair.A.EnqueueAsync();var hash=await pair.A.AddCheckpointAsync(id,[2,4,6,8]);pair.Wire.DelayBlobs=true;
        var blob=new ClusterTaskArtifact(hash,4);
        await Task.WhenAll(pair.B.Transport.FetchAsync(pair.A.Identity.PeerId,id,blob,default),pair.B.Transport.FetchAsync(pair.A.Identity.PeerId,id,blob,default));
        Assert.Equal(1,pair.Wire.BlobRequests);Assert.True(await pair.B.Artifacts.IsCachedAsync(id,hash,4,default));
        await pair.B.Transport.FetchAsync(pair.A.Identity.PeerId,id,blob,default);Assert.Equal(1,pair.Wire.BlobRequests);
    }

    [Fact]
    public async Task TwoServerSelfHealingAutomaticallyNeutralizesRecoversAndReadmits()
    {
        await using var pair=await Pair.CreateAsync();
        await using(var link=pair.A.Db.Db.CreateCommand("UPDATE cluster_nodes SET last_seen_at=clock_timestamp()-interval '3 minutes' WHERE id=@node; UPDATE cluster_peer_admissions SET node_id=@node WHERE peer_id=@peer"))
        {link.Parameters.AddWithValue("node",pair.A.Node);link.Parameters.AddWithValue("peer",pair.A.Identity.PeerId);await link.ExecuteNonQueryAsync();}
        await using(var link=pair.B.Db.Db.CreateCommand("INSERT INTO cluster_nodes(id,name,roles,max_concurrent_tasks,capabilities,last_seen_at) VALUES(@node,'peer-a',ARRAY['server','worker'],2,'[]',clock_timestamp()-interval '3 minutes'); UPDATE cluster_peer_admissions SET node_id=@node WHERE peer_id=@peer"))
        {link.Parameters.AddWithValue("node",pair.A.Node);link.Parameters.AddWithValue("peer",pair.A.Identity.PeerId);await link.ExecuteNonQueryAsync();}
        var id=await pair.A.EnqueueAsync();var sha=await pair.A.AddCheckpointAsync(id,[2,4,6,8]);
        await pair.SyncAsync(pair.A,pair.B,metadata:true);
        await pair.B.Artifacts.CacheAsync(id,sha,4,new MemoryStream([2,4,6,8]),default);
        pair.B.Health.Fail("http://a.test/","offline");
        await pair.B.Maintenance.HealTwoServerClusterAsync(default);
        await using(var neutralized=pair.B.Db.Db.CreateCommand("SELECT count(*) FROM task_neutralizations WHERE peer_id=@peer AND released_at IS NULL"))
        {neutralized.Parameters.AddWithValue("peer",pair.A.Identity.PeerId);Assert.Equal(1L,Convert.ToInt64(await neutralized.ExecuteScalarAsync()));}
        pair.Clock.Advance();
        await pair.B.Maintenance.HealTwoServerClusterAsync(default);
        Assert.Equal(pair.B.Identity.PeerId,(await pair.B.Ownership.GetAsync(id,default))!.OwnerPeerId);
        await pair.SyncAsync(pair.B,pair.A);
        pair.B.Health.ConfirmCatchUp("http://a.test/",pair.A.Identity.PeerId,true);
        pair.B.Health.ConfirmSourceWriteSupport(pair.A.Identity.PeerId,true);
        await pair.B.Maintenance.HealTwoServerClusterAsync(default);
        await using(var released=pair.B.Db.Db.CreateCommand("SELECT count(*) FROM task_neutralizations WHERE peer_id=@peer AND released_at IS NULL"))
        {released.Parameters.AddWithValue("peer",pair.A.Identity.PeerId);Assert.Equal(0L,Convert.ToInt64(await released.ExecuteScalarAsync()));}
    }

}
