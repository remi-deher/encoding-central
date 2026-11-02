using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Executions;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed partial class ClusterTaskTransferTests
{
    private static async Task<ClusterTaskEvent> CheckpointAsync(Site site,Guid id)
    {
        await using var read=site.Db.Db.CreateCommand("SELECT body FROM task_checkpoints WHERE execution_id=@id");read.Parameters.AddWithValue("id",id);
        var signed=JsonSerializer.Deserialize<SignedTaskOwnershipPlan>((string)(await read.ExecuteScalarAsync())!,TaskCheckpointRepository.Json)!;
        return JsonSerializer.Deserialize<ClusterTaskEvent>(signed.Data,TaskCheckpointRepository.Json)! with {EventId=Guid.NewGuid()};
    }
    private static async Task QueueAsync(Site target,Site signer,ClusterTaskEvent value,string? body=null)
    {
        await using var c=await target.Db.Db.OpenConnectionAsync();await using var tx=await c.BeginTransactionAsync();
        await ClusterReconciliationRepository.SetRegisterInTransactionAsync(TaskCheckpointRepository.Prefix+value.EventId.ToString("N"),body??signer.Checkpoints.Sign(value),c,tx,default);await tx.CommitAsync();
    }
    private static async Task ChangeAsync(Site site,string sql,Guid id)
    {await using var cmd=site.Db.Db.CreateCommand(sql);cmd.Parameters.AddWithValue("id",id);await cmd.ExecuteNonQueryAsync();}
    private static async Task<long> CountAsync(Site site,string table)
    {await using var cmd=site.Db.Db.CreateCommand("SELECT count(*) FROM "+table);return Convert.ToInt64(await cmd.ExecuteScalarAsync());}

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorizedCheckpointIsStoredInertlyIncludingQuarantineAndRepeatedDelivery(bool quarantine)
    {
        await using var pair=await Pair.CreateAsync();var id=await pair.A.EnqueueAsync();await pair.A.AddCheckpointAsync(id,[1,2,3]);await pair.SyncAsync(pair.A,pair.B);
        var e=await CheckpointAsync(pair.A,id);
        await ChangeAsync(pair.B,"DELETE FROM task_checkpoints WHERE execution_id=@id",id);
        if(quarantine) await ChangeAsync(pair.B,"UPDATE task_ownership SET state='quarantined' WHERE execution_id=@id",id);
        await QueueAsync(pair.B,pair.A,e);
        await ChangeAsync(pair.B,"UPDATE task_coordination_inbox SET attempts=101701 WHERE register_key LIKE 'tasks/v2/%' AND @id IS NOT NULL",id);
        Assert.Equal(1,await pair.B.Projection.ProjectAsync(default));
        Assert.Equal(0,await CountAsync(pair.B,"task_coordination_inbox"));Assert.Equal(1,await CountAsync(pair.B,"task_checkpoints"));
        Assert.Equal(0,await CountAsync(pair.B,"execution_work"));
        Assert.Equal(quarantine?"quarantined":"active",(await pair.B.Ownership.GetAsync(id,default))!.State);
        if(quarantine)
        {
            var diagnostic=Assert.Single((await pair.B.Projection.StatusAsync(default)).Events);
            Assert.Equal("retained",diagnostic.Disposition);Assert.Equal("quarantined_checkpoint_retained",diagnostic.Reason);
            var json=await pair.Wire.Servers["b.test"].GetStringAsync("/api/v1/cluster/tasks/status");
            Assert.Equal(1,JsonSerializer.Deserialize<ClusterTaskClusterStatus>(json,TaskCheckpointRepository.Json)!.Coordination!.Retained);
            Assert.DoesNotContain(e.Snapshot!,json);Assert.DoesNotContain("snapshot",json,StringComparison.OrdinalIgnoreCase);
            // The cache remains useful if the pre-existing projection later resolves the quarantine.
            await ChangeAsync(pair.B,"UPDATE task_ownership SET state='active' WHERE execution_id=@id",id);
            Assert.Equal(e.Revision,(await CheckpointAsync(pair.B,id)).Revision);
        }
        await QueueAsync(pair.B,pair.A,e);await pair.B.Projection.ProjectAsync(default);
        Assert.Equal(1,await CountAsync(pair.B,"task_checkpoints"));Assert.Equal(0,await CountAsync(pair.B,"execution_work"));
        Assert.True(await pair.B.Ownership.PrepareClaimsAsync(default));
    }

    [Fact]
    public async Task MissingOwnershipBacksOffWithoutLosingEventAndDoesNotStarveTreatableEvents()
    {
        await using var pair=await Pair.CreateAsync();var id=await pair.A.EnqueueAsync();await pair.A.AddCheckpointAsync(id,[1,3,5]);
        var e=await CheckpointAsync(pair.A,id);await QueueAsync(pair.B,pair.A,e);
        Assert.Equal(1,await pair.B.Projection.ProjectAsync(default));
        var status=await pair.B.Projection.StatusAsync(default);Assert.Equal(1,status.Pending);Assert.Equal(1,Assert.Single(status.Events).Attempts);
        for(var i=0;i<12;i++) Assert.Equal(0,await pair.B.Projection.ProjectAsync(default));
        var restarted=new TaskCoordinationProjection(pair.B.Db.Db,pair.B.Identity,pair.B.Checkpoints,pair.Clock);
        Assert.Equal(0,await restarted.ProjectAsync(default));
        Assert.False(await pair.B.Ownership.PrepareClaimsAsync(default));
        Assert.Contains("pending_coordination_events",await pair.B.Ownership.ReadinessBlockersAsync(default));
        var observation=new ClusterTaskEvent(2,Guid.NewGuid(),pair.Cluster,pair.A.Identity.PeerId,"observation",Guid.Empty,0,Guid.Empty,SubjectPeerId:pair.B.Identity.PeerId,RecordedAt:pair.Clock.GetUtcNow(),Sequence:50);
        await QueueAsync(pair.B,pair.A,observation);Assert.Equal(1,await pair.B.Projection.ProjectAsync(default));
        Assert.Equal(1,await CountAsync(pair.B,"task_peer_observations"));Assert.Equal(1,(await pair.B.Projection.StatusAsync(default)).Pending);
        await pair.SyncAsync(pair.A,pair.B); // The creation authority arrives after its checkpoint.
        pair.Clock.Advance();await pair.B.Projection.ProjectAsync(default);
        Assert.Equal(0,(await pair.B.Projection.StatusAsync(default)).Pending);Assert.Equal(1,await CountAsync(pair.B,"task_checkpoints"));
        // A cleared inbox alone cannot replace fresh authenticated catch-up.
        await pair.B.Ownership.InitializeAsync(default);pair.B.Health.Disable("test partition");
        Assert.False(await pair.B.Ownership.PrepareClaimsAsync(default));
        pair.B.Health.Enable();pair.B.Health.ConfirmCatchUp("http://a.test/",pair.A.Identity.PeerId,true);
        Assert.True(await pair.B.Ownership.PrepareClaimsAsync(default));
    }

    [Theory]
    [InlineData("old")]
    [InlineData("author")]
    [InlineData("signature")]
    [InlineData("cluster")]
    [InlineData("previous")]
    [InlineData("revoked")]
    public async Task ObsoleteOrUnauthorizedCheckpointsAreRejectedWithoutImport(string failure)
    {
        await using var pair=await Pair.CreateAsync();var id=await pair.A.EnqueueAsync();await pair.A.AddCheckpointAsync(id,[2,4,6]);await pair.SyncAsync(pair.A,pair.B);
        var e=await CheckpointAsync(pair.A,id);await ChangeAsync(pair.B,"DELETE FROM task_checkpoints WHERE execution_id=@id; UPDATE task_ownership SET state='quarantined' WHERE execution_id=@id",id);
        e=failure switch {"old"=>e with {Epoch=0},"author"=>e with {AuthorPeerId=pair.B.Identity.PeerId},"cluster"=>e with {ClusterId=Guid.NewGuid()},"previous"=>e with {PreviousEventId=Guid.NewGuid()},_=>e};
        if(failure=="revoked")
        {
            await using var revoked=pair.B.Db.Db.CreateCommand("UPDATE cluster_peer_admissions SET revoked_at=clock_timestamp() WHERE peer_id=@peer");
            revoked.Parameters.AddWithValue("peer",pair.A.Identity.PeerId);await revoked.ExecuteNonQueryAsync();
        }
        await QueueAsync(pair.B,failure=="author"?pair.B:pair.A,e,failure=="signature"?pair.B.Checkpoints.Sign(e):null);
        await pair.B.Projection.ProjectAsync(default);Assert.Equal(0,await CountAsync(pair.B,"task_checkpoints"));
        Assert.Equal(0,(await pair.B.Projection.StatusAsync(default)).Pending);Assert.Equal("rejected",Assert.Single((await pair.B.Projection.StatusAsync(default)).Events).Disposition);
        Assert.Equal("quarantined",(await pair.B.Ownership.GetAsync(id,default))!.State);Assert.Equal(0,await CountAsync(pair.B,"execution_work"));
    }

    [Theory]
    [InlineData("activate")]
    [InlineData("recover")]
    public async Task QuarantinedTransitionsAreParkedAndReverifiedAfterProjectionChanges(string kind)
    {
        await using var pair=await Pair.CreateAsync();var id=await pair.A.EnqueueAsync();await pair.SyncAsync(pair.A,pair.B);
        var owner=(await pair.B.Ownership.GetAsync(id,default))!;
        await ChangeAsync(pair.B,"UPDATE task_ownership SET state='quarantined' WHERE execution_id=@id",id);
        Guid previous;
        await using(var read=pair.B.Db.Db.CreateCommand("SELECT last_event_id FROM task_ownership WHERE execution_id=@id")) {read.Parameters.AddWithValue("id",id);previous=(await read.ScalarGuidAsync(default))!.Value;}
        var neutralization=Guid.NewGuid();
        if(kind=="recover")
        {
            await using var n=pair.B.Db.Db.CreateCommand("INSERT INTO task_neutralizations(peer_id,event_id,author_peer_id,recorded_at,reason) VALUES(@peer,@event,@author,@at,'stopped for test')");
            n.Parameters.AddWithValue("peer",pair.A.Identity.PeerId);n.Parameters.AddWithValue("event",neutralization);n.Parameters.AddWithValue("author",pair.B.Identity.PeerId);n.Parameters.AddWithValue("at",pair.Clock.GetUtcNow()-TimeSpan.FromSeconds(140));await n.ExecuteNonQueryAsync();
        }
        var e=new ClusterTaskEvent(2,Guid.NewGuid(),pair.Cluster,kind=="activate"?pair.A.Identity.PeerId:pair.B.Identity.PeerId,kind,id,2,previous,TargetPeerId:pair.B.Identity.PeerId,TransferId:Guid.NewGuid(),RecordedAt:pair.Clock.GetUtcNow(),NeutralizationId:neutralization);
        await QueueAsync(pair.B,kind=="activate"?pair.A:pair.B,e);await pair.B.Projection.ProjectAsync(default);
        var status=await pair.B.Projection.StatusAsync(default);Assert.Equal(0,status.Pending);Assert.Equal(1,status.Parked);Assert.Equal("quarantined_transition_parked",Assert.Single(status.Events).Reason);
        for(var i=0;i<5;i++) Assert.Equal(0,await pair.B.Projection.ProjectAsync(default));
        Assert.Equal(owner.Epoch,(await pair.B.Ownership.GetAsync(id,default))!.Epoch);Assert.Equal(0,await CountAsync(pair.B,"execution_work"));
        Assert.True(await pair.B.Ownership.PrepareClaimsAsync(default));
        // Simulate only the existing projection's resolution, not a new automatic unquarantine mechanism.
        await ChangeAsync(pair.B,"UPDATE task_ownership SET state='active' WHERE execution_id=@id",id);
        await pair.B.Projection.ProjectAsync(default);
        status=await pair.B.Projection.StatusAsync(default);Assert.Equal(0,status.Parked);Assert.Equal(1,status.Pending); // Missing durable transfer receipt still blocks activation.
        Assert.False(await pair.B.Ownership.PrepareClaimsAsync(default));Assert.Equal(0,await CountAsync(pair.B,"execution_work"));
        Assert.Equal(owner.Epoch,(await pair.B.Ownership.GetAsync(id,default))!.Epoch);
    }

    [Fact]
    public async Task ParkedActivationRetainsItsDurableReceiptAndCanApplyOnlyAfterQuarantineResolves()
    {
        await using var pair=await Pair.CreateAsync();var id=await pair.A.EnqueueAsync();await pair.A.AddCheckpointAsync(id,[5,6,7]);await pair.SyncAsync(pair.A,pair.B);
        pair.Wire.BeforeAcknowledgement=()=>ChangeAsync(pair.B,"UPDATE task_ownership SET state='quarantined' WHERE execution_id=@id",id);
        var moved=await pair.A.Coordinator.MoveAsync(id,pair.B.Identity.PeerId,default);Assert.True(moved.Success,moved.Detail);
        Assert.Equal(1,(await pair.B.Projection.StatusAsync(default)).Parked);Assert.Equal(0,await CountAsync(pair.B,"execution_work"));
        Assert.Equal(1,(await pair.B.Ownership.GetAsync(id,default))!.Epoch);
        await ChangeAsync(pair.B,"UPDATE task_ownership SET state='active' WHERE execution_id=@id",id);
        await pair.B.Projection.ProjectAsync(default);
        Assert.Equal(0,(await pair.B.Projection.StatusAsync(default)).Parked);Assert.Equal(0,(await pair.B.Projection.StatusAsync(default)).Pending);
        Assert.Equal(pair.B.Identity.PeerId,(await pair.B.Ownership.GetAsync(id,default))!.OwnerPeerId);
        Assert.Equal(2,(await pair.B.Ownership.GetAsync(id,default))!.Epoch);Assert.Equal(1,await CountAsync(pair.B,"execution_work"));
    }

    [Theory]
    [InlineData(0,1)]
    [InlineData(1,2)]
    [InlineData(5,32)]
    [InlineData(6,60)]
    [InlineData(101701,60)]
    public void CoordinationRetryDelayIsBoundedWithoutExpiringTheDependency(long attempts,int seconds)
        =>Assert.Equal(TimeSpan.FromSeconds(seconds),TaskCoordinationProjection.RetryDelay(attempts));

    [Fact]
    public async Task FiveHighAttemptCheckpointsDrainNormallyWithoutLiftingConcurrentTaskQuarantine()
    {
        await using var pair=await Pair.CreateAsync();var losers=new List<Guid>();
        for(var i=0;i<5;i++)
        {
            await pair.B.EnqueueAsync($"duplicate-{i}.mkv");var id=await pair.A.EnqueueAsync($"duplicate-{i}.mkv");
            losers.Add(id);await pair.A.AddCheckpointAsync(id,[7,8,9]);
        }
        await pair.SyncAsync(pair.A,pair.B);await pair.SyncAsync(pair.B,pair.A);
        var previouslyRetained=(await pair.B.Projection.StatusAsync(default)).Retained;
        foreach(var id in losers)
        {
            Assert.Equal("quarantined",(await pair.B.Ownership.GetAsync(id,default))!.State);
            var saved=await CheckpointAsync(pair.A,id);
            await ChangeAsync(pair.B,"DELETE FROM task_checkpoints WHERE execution_id=@id",id);
            await QueueAsync(pair.B,pair.A,saved);
        }
        var loser=losers[0];
        await ChangeAsync(pair.B,"UPDATE task_coordination_inbox SET attempts=101701 WHERE @id IS NOT NULL",loser);
        await pair.B.Ownership.InitializeAsync(default);
        Assert.True(await pair.B.Ownership.PrepareClaimsAsync(default));
        var status=await pair.B.Projection.StatusAsync(default);Assert.Equal(0,status.Pending);Assert.Equal(previouslyRetained+5,status.Retained);
        Assert.Equal(5,await CountAsync(pair.B,"task_checkpoints"));
        foreach(var id in losers) Assert.Equal("quarantined",(await pair.B.Ownership.GetAsync(id,default))!.State);
        await using var active=pair.B.Db.Db.CreateCommand($"SELECT {TaskOwnershipRepository.ActiveSql("@id")}");active.Parameters.AddWithValue("id",loser);Assert.False(await active.ScalarBoolAsync(default));
        // Draining the five events cannot override a local neutralization.
        await using(var n=pair.B.Db.Db.CreateCommand("INSERT INTO task_neutralizations(peer_id,event_id,author_peer_id,recorded_at,reason) VALUES(@peer,@event,@author,clock_timestamp(),'test stopped')"))
        {n.Parameters.AddWithValue("peer",pair.B.Identity.PeerId);n.Parameters.AddWithValue("event",Guid.NewGuid());n.Parameters.AddWithValue("author",pair.A.Identity.PeerId);await n.ExecuteNonQueryAsync();}
        await pair.B.Ownership.InitializeAsync(default);Assert.False(await pair.B.Ownership.PrepareClaimsAsync(default));
        Assert.Contains("local_peer_neutralized",await pair.B.Ownership.ReadinessBlockersAsync(default));
    }

    [Fact]
    public async Task ReadyServerStillCannotClaimQuarantinedLocalWork()
    {
        await using var pair=await Pair.CreateAsync();var id=await pair.A.EnqueueAsync();await ChangeAsync(pair.A,"UPDATE task_ownership SET state='quarantined' WHERE execution_id=@id",id);
        Assert.True(await pair.A.Ownership.PrepareClaimsAsync(default));Assert.Null(await pair.A.Runtime.ClaimAsync(pair.A.Node,default));
        await using var allowed=pair.A.Db.Db.CreateCommand($"SELECT {TaskOwnershipRepository.ActiveSql("@id")}");allowed.Parameters.AddWithValue("id",id);Assert.False(await allowed.ScalarBoolAsync(default));
        Assert.False((await pair.A.Coordinator.MoveAsync(id,pair.B.Identity.PeerId,default)).Success);
    }
}
