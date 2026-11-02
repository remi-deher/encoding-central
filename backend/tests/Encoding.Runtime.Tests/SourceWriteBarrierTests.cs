using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Nodes;
using Encoding.Api.Plugins;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.NodeAgent.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Encoding.Runtime.Tests;

[Collection(RuntimeCollection.Name)]
public sealed class SourceWriteBarrierTests
{
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override long GetTimestamp()=>ticks;
        public void Advance(TimeSpan duration)=>ticks+=duration.Ticks;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly string Directory=Path.Combine(Path.GetTempPath(),"source-write-"+Guid.NewGuid().ToString("N"));
        public readonly Clock Clock=new();
        public readonly ClusterReconciliationHealth Health;
        public readonly Guid Worker=Guid.NewGuid(),Library=Guid.NewGuid(),Execution=Guid.NewGuid();
        public TestDatabase Database=null!;
        public ClusterPeerIdentityStore Identity=null!;
        public ClusterSourceWriteBarrier Barrier=null!;
        public PendingSourceWriteRepository Writes=null!;
        public FlowDefinition Flow=null!;
        public static async Task<Fixture> CreateAsync()
        {
            var f=new Fixture();System.IO.Directory.CreateDirectory(f.Directory);
            f.Database=await TestDatabase.CreateSqliteAsync("source-write");await DatabaseMigrator.ApplyAsync(f.Database.Db,Harness.RepositoryRoot(),default);
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["PluginSettings:KeyDirectory"]=Path.Combine(f.Directory,"keys")}).Build();
            f.Identity=new(config,new EphemeralDataProtectionProvider());f.Barrier=new(f.Database.Db,f.Health,f.Identity);f.Writes=new(f.Database.Db,f.Barrier);
            var(registry,_,_)=Harness.Services();
            f.Flow=new(1,Guid.NewGuid(),1,"Edit","video",[
                new("input",null,registry.FindReference("base-input-video")!,new Dictionary<string,JsonElement>()),
                new("edit",null,registry.FindReference("mkv-edit-in-place")!,new Dictionary<string,JsonElement>())],[new("edge","input","success","edit")]);
            await using var cmd=f.Database.Db.CreateCommand("""
              INSERT INTO cluster_nodes(id,name,roles,max_concurrent_tasks,capabilities,last_seen_at) VALUES(@worker,'worker',ARRAY['worker'],2,@caps,clock_timestamp());
              INSERT INTO libraries(id,name) VALUES(@library,'library');
              INSERT INTO library_node_accesses(library_id,node_id,mode,local_path,can_read,can_write) VALUES(@library,@worker,'direct',@root,true,true);
              INSERT INTO library_replacement_policies(library_id,enabled,grace_period_hours,revision) VALUES(@library,true,1,1);
              INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt) VALUES(@id,'source.mkv','library','Edit',1,@flow,'running',1);
              INSERT INTO execution_work(execution_id,library_id,media_path,media_key,worker_node_id,source_node_id,lease_epoch,lease_until,source_sha)
                VALUES(@id,@library,'source.mkv','SOURCE.MKV',@worker,@worker,1,clock_timestamp()+interval '60 seconds',@sha);
              INSERT INTO media_reservations(library_id,media_key,execution_id,lease_epoch,lease_until) VALUES(@library,'SOURCE.MKV',@id,1,'infinity');
              INSERT INTO execution_artifacts(execution_id,sha256,byte_length) VALUES(@id,@sha,1);
              """);
            cmd.Parameters.AddWithValue("worker",f.Worker);cmd.Parameters.AddWithValue("library",f.Library);cmd.Parameters.AddWithValue("id",f.Execution);
            cmd.Parameters.AddWithValue("root",f.Directory);cmd.Parameters.AddWithValue("sha",new string('a',64));
            cmd.Parameters.AddWithValue("flow",SqlKind.Json,JsonSerializer.Serialize(f.Flow,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            cmd.Parameters.AddWithValue("caps",SqlKind.Json,"[{\"toolId\":\"runtime/source-write-guard-v1\",\"version\":\"1.0.0\",\"mediaTypes\":[],\"accelerationTypes\":[]},{\"toolId\":\"plugin/base\",\"version\":\"1.9.0\",\"mediaTypes\":[],\"accelerationTypes\":[]},{\"toolId\":\"plugin/mkv\",\"version\":\"1.1.0\",\"mediaTypes\":[],\"accelerationTypes\":[]},{\"toolId\":\"mkvtoolnix\",\"version\":\"92.0\",\"mediaTypes\":[],\"accelerationTypes\":[]},{\"toolId\":\"ffprobe\",\"version\":\"7.0\",\"mediaTypes\":[],\"accelerationTypes\":[]}]");
            await cmd.ExecuteNonQueryAsync();return f;
        }
        private Fixture() {Health=new(Clock);Health.Enable();}
        public async Task<Guid> AddPeerAsync()
        {
            var peer=Guid.NewGuid();await using var cmd=Database.Db.CreateCommand("INSERT INTO cluster_peer_admissions(cluster_id,peer_id,public_key,approved_by_peer_id,key_fingerprint,issued_at,signature) SELECT id,@peer,'test-key',@peer,'test-key',clock_timestamp(),'test-signature' FROM cluster_identity");
            cmd.Parameters.AddWithValue("peer",peer);await cmd.ExecuteNonQueryAsync();return peer;
        }
        public async Task QueueAsync()
        {
            await using var c=await Database.Db.OpenConnectionAsync();await using var tx=await c.BeginTransactionAsync();
            await PendingSourceWriteRepository.EnqueueAsync(Execution,Worker,Library,"source.mkv",".encoding-results/"+Execution+"/result.mkv",new string('a',64),new string('b',64),".mkv",1,c,tx,default);
            await RuntimeRepository.ReleaseAsync(Execution,c,tx,default);await tx.CommitAsync();
        }
        public async ValueTask DisposeAsync()
        {
            Identity.Dispose();await Database.DisposeAsync();System.IO.Directory.Delete(Directory,true);
        }
    }

    [Fact]
    public async Task AutonomousNodeDoesNotNeedAClusterButEveryAdmittedServerMustCatchUp()
    {
        await using var f=await Fixture.CreateAsync();Assert.Null(await f.Barrier.CheckAsync(default));
        var first=await f.AddPeerAsync();Assert.NotNull(await f.Barrier.CheckAsync(default));
        f.Health.Succeed("http://peer/");Assert.NotNull(await f.Barrier.CheckAsync(default)); // Presence alone is not authorization.
        f.Health.ConfirmCatchUp("http://peer/",first,false);Assert.NotNull(await f.Barrier.CheckAsync(default));
        f.Health.ConfirmCatchUp("http://peer/",first,true);Assert.NotNull(await f.Barrier.CheckAsync(default));f.Health.ConfirmSourceWriteSupport(first,true);Assert.Null(await f.Barrier.CheckAsync(default));
        var third=await f.AddPeerAsync();Assert.NotNull(await f.Barrier.CheckAsync(default));
        f.Health.ConfirmCatchUp("http://third/",third,true);f.Health.ConfirmSourceWriteSupport(third,true);Assert.Null(await f.Barrier.CheckAsync(default));
        f.Health.Fail("http://peer/","partition");Assert.NotNull(await f.Barrier.CheckAsync(default));
    }

    [Fact]
    public async Task PermitsExpireAndAreNotCarriedAcrossServiceRestart()
    {
        await using var f=await Fixture.CreateAsync();var peer=await f.AddPeerAsync();
        f.Health.ConfirmCatchUp("http://peer/",peer,true);f.Health.ConfirmSourceWriteSupport(peer,true);Assert.Null(await f.Barrier.CheckAsync(default));
        f.Clock.Advance(TimeSpan.FromSeconds(46));Assert.NotNull(await f.Barrier.CheckAsync(default));
        var restartedHealth=new ClusterReconciliationHealth();restartedHealth.Enable();
        var restarted=new ClusterSourceWriteBarrier(f.Database.Db,restartedHealth,f.Identity);
        Assert.NotNull(await restarted.CheckAsync(default));
    }

    [Fact]
    public async Task DeferredReplacementRetainsReservationAndRejectsStaleClaims()
    {
        await using var f=await Fixture.CreateAsync();await f.QueueAsync();
        var visible=await new SourceReplacementRepository(f.Database.Db,f.Barrier).GetAsync(f.Execution,default);
        Assert.Equal("waiting",visible!.State);
        await using(var count=f.Database.Db.CreateCommand("SELECT count(*) FROM media_reservations")) Assert.Equal(1L,(long)(await count.ExecuteScalarAsync())!);
        var first=await f.Writes.ClaimAsync(f.Worker,default);Assert.NotNull(first);
        Assert.Null(await f.Writes.ClaimAsync(f.Worker,default));Assert.False(await f.Writes.AuthorizeAsync(f.Execution,Guid.NewGuid(),first!.ClaimId,f.Directory,default));
        Assert.True(await f.Writes.AuthorizeAsync(f.Execution,f.Worker,first.ClaimId,f.Directory,default));
        await using(var expire=f.Database.Db.CreateCommand("UPDATE pending_source_writes SET claim_until=clock_timestamp()-interval '1 second'")) await expire.ExecuteNonQueryAsync();
        var second=await f.Writes.ClaimAsync(f.Worker,default);Assert.NotNull(second);Assert.NotEqual(first.ClaimId,second!.ClaimId);
        Assert.False(await f.Writes.AuthorizeAsync(f.Execution,f.Worker,first.ClaimId,f.Directory,default));
        Assert.False(await f.Writes.CompleteAsync(f.Execution,f.Worker,new(first.ClaimId,null),default));
        Assert.True(await f.Writes.CompleteAsync(f.Execution,f.Worker,new(second.ClaimId,null),default));
        Assert.Equal("waiting",(await new SourceReplacementRepository(f.Database.Db).GetAsync(f.Execution,default))!.State);
    }

    [Fact]
    public async Task LostConnectivityAndDisabledPolicyKeepPublishedResultWaiting()
    {
        await using var f=await Fixture.CreateAsync();await f.QueueAsync();var peer=await f.AddPeerAsync();
        Assert.Null(await f.Writes.ClaimAsync(f.Worker,default));
        f.Health.ConfirmCatchUp("http://peer/",peer,true);f.Health.ConfirmSourceWriteSupport(peer,true);var claim=await f.Writes.ClaimAsync(f.Worker,default);Assert.NotNull(claim);
        f.Health.Fail("http://peer/","partition");Assert.False(await f.Writes.AuthorizeAsync(f.Execution,f.Worker,claim!.ClaimId,f.Directory,default));
        Assert.True(await f.Writes.CompleteAsync(f.Execution,f.Worker,new(claim.ClaimId,null),default));
        f.Health.ConfirmCatchUp("http://peer/",peer,true);f.Health.ConfirmSourceWriteSupport(peer,true);
        await using(var disable=f.Database.Db.CreateCommand("UPDATE library_replacement_policies SET enabled=false")) await disable.ExecuteNonQueryAsync();
        Assert.Null(await f.Writes.ClaimAsync(f.Worker,default));
    }

    [Fact]
    public async Task IsolatedNativeStepIsSuspendedBeforeInvocationAndFencesTheOldLease()
    {
        await using var f=await Fixture.CreateAsync();var(registry,_,eligibility)=Harness.Services();
        var validation=new FlowDefinitionValidator(registry).Validate(f.Flow);Assert.True(validation.IsValid,string.Join("; ",validation.Issues.Select(i=>i.Code+": "+i.Message)));
        var runtime=new RuntimeRepository(f.Database.Db,eligibility,registry,new NodePresenceOptions(TimeSpan.FromHours(1)),new PluginConfigurationRepository(f.Database.Db,new EphemeralDataProtectionProvider()),new NodeGroupRepository(f.Database.Db),f.Barrier);
        var start=await runtime.StartStepAsync(f.Execution,f.Worker,new(1,"input"),default);Assert.NotNull(start);
        var context=new RuntimeContext(new string('a',64),".mkv",new Dictionary<string,JsonElement>(),null,null,[]);
        Assert.True(await runtime.CompleteStepAsync(f.Execution,f.Worker,new(1,Guid.NewGuid(),0,new("input",start!.InvocationId,"success","edit",context,null)),default));
        var peer=await f.AddPeerAsync();Assert.Null(await runtime.StartStepAsync(f.Execution,f.Worker,new(1,"edit"),default));
        Assert.Null(await runtime.RenewAsync(f.Execution,f.Worker,1,default));
        await using(var read=f.Database.Db.CreateCommand("SELECT count(*) FROM runtime_source_write_waits")) Assert.Equal(1L,(long)(await read.ExecuteScalarAsync())!);
        Assert.Equal("running",(await new ExecutionRepository(f.Database.Db).GetAsync(f.Execution,default))!.State);
        await using(var ready=f.Database.Db.CreateCommand("UPDATE execution_work SET available_at=clock_timestamp()-interval '1 second'")) await ready.ExecuteNonQueryAsync();
        Assert.Null(await runtime.ClaimAsync(f.Worker,default));
        f.Health.ConfirmCatchUp("http://peer/",peer,true);f.Health.ConfirmSourceWriteSupport(peer,true);
        var resumed=await runtime.ClaimAsync(f.Worker,default);Assert.NotNull(resumed);
        Assert.True(resumed!.Epoch>1);Assert.Equal("edit",resumed.Checkpoint!.NextNodeId);
        Assert.NotNull(await runtime.StartStepAsync(f.Execution,f.Worker,new(resumed.Epoch,"edit"),default));
    }

    [Fact]
    public async Task LegacyWorkerCannotClaimADeferredSourceMutationInACluster()
    {
        await using var f=await Fixture.CreateAsync();await f.QueueAsync();var peer=await f.AddPeerAsync();
        f.Health.ConfirmCatchUp("http://peer/",peer,true);f.Health.ConfirmSourceWriteSupport(peer,true);
        await using(var legacy=f.Database.Db.CreateCommand("UPDATE cluster_nodes SET capabilities='[]'")) await legacy.ExecuteNonQueryAsync();
        Assert.Null(await f.Writes.ClaimAsync(f.Worker,default));
    }

    [Fact]
    public async Task CompletionIsIdempotentAndReleasesTheTerminalMediaReservation()
    {
        await using var f=await Fixture.CreateAsync();await f.QueueAsync();
        await using(var terminal=f.Database.Db.CreateCommand("UPDATE executions SET state='succeeded',finished_at=clock_timestamp()")) await terminal.ExecuteNonQueryAsync();
        var claim=(await f.Writes.ClaimAsync(f.Worker,default))!;
        var completion=new PendingSourceWriteCompletion(claim.ClaimId,new("failed",null,null,0,DateTimeOffset.UtcNow,"Source conservée."));
        Assert.True(await f.Writes.CompleteAsync(f.Execution,f.Worker,completion,default));
        Assert.True(await f.Writes.CompleteAsync(f.Execution,f.Worker,completion,default));
        await using var count=f.Database.Db.CreateCommand("SELECT count(*) FROM media_reservations");Assert.Equal(0L,(long)(await count.ExecuteScalarAsync())!);
        Assert.Equal("failed",(await new SourceReplacementRepository(f.Database.Db).GetAsync(f.Execution,default))!.State);
    }

    [MediaToolsFact]
    public async Task ValidatedPublicationContinuesDuringIsolationAndSourceReplacementResumesSafely()
    {
        var(registry,validator,eligibility)=Harness.Services();
        await using var fixture=await Harness.Fixture.CreateAsync("sqlite",registry,validator,eligibility,sourceWriteGuards:true);
        await fixture.DeferredSourceWriteEndToEndAsync();
    }

    [MediaToolsFact]
    public async Task FinalPublicationFreesItsWorkerWhileTheSourceWriteWaits()
    {
        var(registry,validator,eligibility)=Harness.Services();
        await using var fixture=await Harness.Fixture.CreateAsync("sqlite",registry,validator,eligibility,sourceWriteGuards:true);
        await fixture.DeferredSourceWriteEndToEndAsync(continueFlow:false);
    }

    [Fact]
    public async Task PermissionAndRootChangesAreCheckedAgainAfterAClaim()
    {
        await using var f=await Fixture.CreateAsync();await f.QueueAsync();
        var claim=(await f.Writes.ClaimAsync(f.Worker,default))!;
        Assert.False(await f.Writes.AuthorizeAsync(f.Execution,f.Worker,claim.ClaimId,f.Directory+"-other",default));
        await using(var revoke=f.Database.Db.CreateCommand("UPDATE library_node_accesses SET can_write=false")) await revoke.ExecuteNonQueryAsync();
        Assert.False(await f.Writes.AuthorizeAsync(f.Execution,f.Worker,claim.ClaimId,f.Directory,default));
        Assert.True(await f.Writes.CompleteAsync(f.Execution,f.Worker,new(claim.ClaimId,null),default));
    }

    [Fact]
    public async Task PartitionImmediatelyBeforeNativeMutationSuspendsWithoutReplayingAnUncertainStep()
    {
        await using var f=await Fixture.CreateAsync();var(registry,_,eligibility)=Harness.Services();
        var runtime=new RuntimeRepository(f.Database.Db,eligibility,registry,new NodePresenceOptions(TimeSpan.FromHours(1)),new PluginConfigurationRepository(f.Database.Db,new EphemeralDataProtectionProvider()),new NodeGroupRepository(f.Database.Db),f.Barrier);
        var start=(await runtime.StartStepAsync(f.Execution,f.Worker,new(1,"input"),default))!;
        var context=new RuntimeContext(new string('a',64),".mkv",new Dictionary<string,JsonElement>(),null,null,[]);
        Assert.True(await runtime.CompleteStepAsync(f.Execution,f.Worker,new(1,Guid.NewGuid(),0,new("input",start.InvocationId,"success","edit",context,null)),default));
        var peer=await f.AddPeerAsync();f.Health.ConfirmCatchUp("http://peer/",peer,true);f.Health.ConfirmSourceWriteSupport(peer,true);
        Assert.NotNull(await runtime.StartStepAsync(f.Execution,f.Worker,new(1,"edit"),default));
        Assert.True(await runtime.AuthorizeSourceWriteAsync(f.Execution,f.Worker,1,f.Directory,default));
        f.Health.Fail("http://peer/","partition");Assert.False(await runtime.AuthorizeSourceWriteAsync(f.Execution,f.Worker,1,f.Directory,default));
        Assert.Null(await runtime.RenewAsync(f.Execution,f.Worker,1,default));
        await using var steps=f.Database.Db.CreateCommand("SELECT count(*) FROM execution_steps WHERE node_id='edit'");Assert.Equal(0L,(long)(await steps.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task DrainingAndQuarantinedIdentitiesCannotStartNewSourceWrites()
    {
        await using var f=await Fixture.CreateAsync();await f.QueueAsync();
        await using(var drain=f.Database.Db.CreateCommand("UPDATE cluster_nodes SET draining=true")) await drain.ExecuteNonQueryAsync();
        Assert.Null(await f.Writes.ClaimAsync(f.Worker,default));
        await using(var resume=f.Database.Db.CreateCommand("UPDATE cluster_nodes SET draining=false")) await resume.ExecuteNonQueryAsync();
        var claim=(await f.Writes.ClaimAsync(f.Worker,default))!;
        await using(var quarantine=f.Database.Db.CreateCommand("UPDATE cluster_nodes SET quarantined=true")) await quarantine.ExecuteNonQueryAsync();
        Assert.False(await f.Writes.AuthorizeAsync(f.Execution,f.Worker,claim.ClaimId,f.Directory,default));
    }

    [Fact]
    public async Task SharedSourceLockRejectsAnotherWorkerAndNeverDeletesItsLock()
    {
        var root=Path.Combine(Path.GetTempPath(),"source-lock-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var original=Path.Combine(root,"source.mkv");await File.WriteAllTextAsync(original,"source");
            using(var first=SharedSourceWriteLock.Acquire(original))
            {
                Assert.Throws<SafeMediaPaths.MediaLockBusyException>(()=>SharedSourceWriteLock.Acquire(original));
                Assert.Throws<SafeMediaPaths.MediaLockBusyException>(()=>SharedSourceWriteLock.Acquire(Path.ChangeExtension(original,".mp4")));
                Assert.True(File.Exists(Path.Combine(root,"source.encoding-write.lock")));Assert.Equal("source",await File.ReadAllTextAsync(original));
            }
            Assert.False(File.Exists(Path.Combine(root,"source.encoding-write.lock")));
            await File.WriteAllTextAsync(Path.Combine(root,"source.encoding-write.lock"),"owner interrupted");
            Assert.Throws<SafeMediaPaths.MediaLockBusyException>(()=>SharedSourceWriteLock.Acquire(original));
            Assert.Equal("owner interrupted",await File.ReadAllTextAsync(Path.Combine(root,"source.encoding-write.lock")));
        }
        finally {Directory.Delete(root,true);}
    }
}
