using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Contracts.Executions;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class TaskOwnershipTests
{
    private sealed class Fixture:IAsyncDisposable
    {
        internal TestDatabase Db=null!;
        internal ClusterPeerIdentityStore Identity=null!;
        internal TaskOwnershipRepository Ownership=null!;
        internal ClusterReconciliationHealth Health=new();
        internal Guid Library=Guid.NewGuid();
        private readonly string root=Path.Combine(Path.GetTempPath(),"ownership-"+Guid.NewGuid().ToString("N"));
        internal static async Task<Fixture> CreateAsync()
        {
            var f=new Fixture();f.Db=await TestDatabase.CreateSqliteAsync("ownership");await DatabaseMigrator.ApplyAsync(f.Db.Db,Harness.RepositoryRoot(),default);
            f.Identity=new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["PluginSettings:KeyDirectory"]=f.root}).Build(),new EphemeralDataProtectionProvider());
            f.Ownership=new(f.Db.Db,f.Identity,f.Health);
            await using var library=f.Db.Db.CreateCommand("INSERT INTO libraries(id,name) VALUES(@id,'test')");library.Parameters.AddWithValue("id",f.Library);await library.ExecuteNonQueryAsync();
            await f.Ownership.InitializeAsync(default);return f;
        }
        internal async Task<Guid> AddAsync(bool commit=true,bool assign=true)
        {
            var id=Guid.NewGuid();await using var c=await Db.Db.OpenConnectionAsync();await using var tx=await c.BeginTransactionAsync();
            await using var cmd=Sql.Command("INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt) VALUES(@id,'a.mkv','test','test',1,'{}','queued',1); INSERT INTO execution_work(execution_id,library_id,media_path,media_key) VALUES(@id,@library,'a.mkv',@key)",c,tx);
            cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("library",Library);cmd.Parameters.AddWithValue("key",id.ToString());await cmd.ExecuteNonQueryAsync();
            if(assign) await Ownership.CreateAsync([id],c,tx,default);if(commit) await tx.CommitAsync();return id;
        }
        internal async Task AdmitAsync(Fixture other)
        {
            await using var cmd=Db.Db.CreateCommand("INSERT INTO cluster_peer_admissions(cluster_id,peer_id,public_key,approved_by_peer_id,key_fingerprint,issued_at,signature) SELECT id,@peer,@key,@peer,'test',clock_timestamp(),'test' FROM cluster_identity");
            cmd.Parameters.AddWithValue("peer",other.Identity.PeerId);cmd.Parameters.AddWithValue("key",other.Identity.PublicKey);await cmd.ExecuteNonQueryAsync();
        }
        public async ValueTask DisposeAsync(){Identity.Dispose();await Db.DisposeAsync();if(Directory.Exists(root)) Directory.Delete(root,true);}
    }
    [Fact]
    public async Task KnownOwnershipPreventsRecreatingWorkBeforeExecutionHistoryArrives()
    {
        await using var f = await Fixture.CreateAsync();
        var id = await f.AddAsync();
        await using (var history = f.Db.Db.CreateCommand("DELETE FROM execution_work WHERE execution_id=@id; DELETE FROM executions WHERE id=@id; UPDATE task_ownership SET media_key='A.MKV',owner_peer_id=@remote WHERE execution_id=@id"))
        {
            history.Parameters.AddWithValue("id", id); history.Parameters.AddWithValue("remote", Guid.NewGuid());
            await history.ExecuteNonQueryAsync();
        }
        await using var c = await f.Db.Db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var flow = new Encoding.Contracts.Flows.FlowDefinition(1, Guid.NewGuid(), 1, "Synthetic", "video", [], []);
        Assert.False(await RuntimeRepository.EnqueueInTransactionAsync(new(Guid.NewGuid(), f.Library, "a.mkv", flow.Id, 1), flow, c, tx, default));
    }

    [Fact]
    public async Task QuarantinedHistoryIsPreservedWithoutCountingItAsQueuedWork()
    {
        await using var f = await Fixture.CreateAsync();
        var id = await f.AddAsync();
        await using (var update = f.Db.Db.CreateCommand("UPDATE task_ownership SET state='quarantined' WHERE execution_id=@id"))
        { update.Parameters.AddWithValue("id",id); await update.ExecuteNonQueryAsync(); }
        var overview = await new ExecutionRepository(f.Db.Db).OverviewAsync(default);
        Assert.Equal(0,overview.QueuedCount);
        Assert.Equal(1,overview.QuarantinedCount);
        Assert.Equal(id,Assert.Single(overview.Executions).Id);
    }

    [Fact]
    public async Task CreationAndSignedPlanCommitTogether()
    {
        await using var f=await Fixture.CreateAsync();var rolledBack=await f.AddAsync(false);Assert.Null(await f.Ownership.GetAsync(rolledBack,default));
        var id=await f.AddAsync();var owner=await f.Ownership.GetAsync(id,default);Assert.Equal(f.Identity.PeerId,owner!.OwnerPeerId);Assert.Equal(1,owner.Epoch);
        Assert.True(await f.Ownership.PrepareClaimsAsync(default));
        await using var count=f.Db.Db.CreateCommand("SELECT count(*) FROM cluster_sync_registers WHERE register_key LIKE 'tasks/v1/events/%'");Assert.Equal(1L,Convert.ToInt64(await count.ExecuteScalarAsync()));
    }
    [Fact]
    public async Task PlanReplicatesWithAuthorProofAndCannotBecomeLocalWork()
    {
        await using var source=await Fixture.CreateAsync();await using var target=await Fixture.CreateAsync();
        var cluster=(await new ClusterIdentityRepository(source.Db.Db).GetAsync(default)).Id;
        await new ClusterIdentityRepository(target.Db.Db).AdoptAsync(cluster,"test",default);await target.AdmitAsync(source);
        var id=await source.AddAsync();var journal=new ClusterReconciliationRepository(source.Db.Db);
        var changes=await journal.GetEventsAfterAsync(cluster,new Dictionary<Guid,long>(),32,default);
        await new ClusterReconciliationRepository(target.Db.Db).ApplyIncomingAsync(changes,default);Assert.Equal(1,await target.Ownership.ProjectAsync(default));
        Assert.Equal(source.Identity.PeerId,(await target.Ownership.GetAsync(id,default))!.OwnerPeerId);
        await using var work=target.Db.Db.CreateCommand("SELECT count(*) FROM execution_work");Assert.Equal(0L,Convert.ToInt64(await work.ExecuteScalarAsync()));
    }
    [Fact]
    public async Task RestartMustCatchUpBeforeLegacyQueueCanBeClaimed()
    {
        await using var local=await Fixture.CreateAsync();await using var peer=await Fixture.CreateAsync();await local.AdmitAsync(peer);
        var id=await local.AddAsync(assign:false);Assert.False(await local.Ownership.PrepareClaimsAsync(default));Assert.Null(await local.Ownership.GetAsync(id,default));
        Assert.Contains("peer_catch_up_unconfirmed:" + peer.Identity.PeerId.ToString("D"), await local.Ownership.ReadinessBlockersAsync(default));
        local.Health.Enable();local.Health.ConfirmCatchUp("http://peer/",peer.Identity.PeerId,true);
        Assert.True(await local.Ownership.PrepareClaimsAsync(default));Assert.Equal(local.Identity.PeerId,(await local.Ownership.GetAsync(id,default))!.OwnerPeerId);
        Assert.Empty(await local.Ownership.ReadinessBlockersAsync(default));
        // An established owner keeps its local queue during a later partition.
        local.Health.Fail("http://peer/","partition");Assert.True(await local.Ownership.PrepareClaimsAsync(default));
        await local.Ownership.InitializeAsync(default);Assert.False(await local.Ownership.PrepareClaimsAsync(default));
    }
    [Fact]
    public async Task ForgedAuthorCannotCreateOwnership()
    {
        await using var f=await Fixture.CreateAsync();var id=Guid.NewGuid();var plan=new TaskOwnershipPlan(1,Guid.NewGuid(),(await new ClusterIdentityRepository(f.Db.Db).GetAsync(default)).Id,f.Identity.PeerId,[new(id,f.Library,"A",DateTimeOffset.UtcNow,f.Identity.PeerId)]);
        var data=JsonSerializer.Serialize(plan,TaskOwnershipRepository.Json);var signed=new SignedTaskOwnershipPlan(data,Convert.ToBase64String(new byte[384]));
        await new ClusterReconciliationRepository(f.Db.Db).SetRegisterAsync(TaskOwnershipRepository.Prefix+plan.EventId.ToString("N"),JsonSerializer.Serialize(signed,TaskOwnershipRepository.Json),default);
        await f.Ownership.ProjectAsync(default);Assert.Null(await f.Ownership.GetAsync(id,default));
        await using var bad=f.Db.Db.CreateCommand("SELECT state FROM task_ownership_events");Assert.Equal("rejected",await bad.ExecuteScalarAsync());
    }
    [Fact]
    public async Task AdmissionCanArriveAfterItsPlanWithoutLosingOwnership()
    {
        await using var source=await Fixture.CreateAsync();await using var target=await Fixture.CreateAsync();
        var cluster=(await new ClusterIdentityRepository(source.Db.Db).GetAsync(default)).Id;
        await new ClusterIdentityRepository(target.Db.Db).AdoptAsync(cluster,"test",default);
        var id=await source.AddAsync();var changes=await new ClusterReconciliationRepository(source.Db.Db).GetEventsAfterAsync(cluster,new Dictionary<Guid,long>(),32,default);
        await new ClusterReconciliationRepository(target.Db.Db).ApplyIncomingAsync(changes,default);
        Assert.Equal(1,await target.Ownership.ProjectAsync(default));Assert.Null(await target.Ownership.GetAsync(id,default));
        Assert.False(await target.Ownership.PrepareClaimsAsync(default));
        await target.AdmitAsync(source);Assert.Equal(1,await target.Ownership.ProjectAsync(default));
        Assert.Equal(source.Identity.PeerId,(await target.Ownership.GetAsync(id,default))!.OwnerPeerId);
    }
    [Fact]
    public async Task ReplayingAnAcceptedPlanDoesNotChangeItsAuthority()
    {
        await using var f=await Fixture.CreateAsync();var id=await f.AddAsync();string key,body;
        await using(var read=f.Db.Db.CreateCommand("SELECT register_key,value FROM cluster_sync_registers WHERE register_key LIKE 'tasks/v1/events/%'"))
        await using(var r=await read.ExecuteReaderAsync()) {Assert.True(await r.ReadAsync());key=r.GetString(0);body=r.GetString(1);}
        await new ClusterReconciliationRepository(f.Db.Db).SetRegisterAsync(key,body,default);await f.Ownership.ProjectAsync(default);
        Assert.Equal("active",(await f.Ownership.GetAsync(id,default))!.State);
        await using var count=f.Db.Db.CreateCommand("SELECT count(*) FROM task_ownership_events");Assert.Equal(1L,Convert.ToInt64(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task ConflictingCreationPlansFenceTheExecution()
    {
        await using var f=await Fixture.CreateAsync();var id=await f.AddAsync();Assert.True(await f.Ownership.PrepareClaimsAsync(default));
        var plan=new TaskOwnershipPlan(1,Guid.NewGuid(),(await new ClusterIdentityRepository(f.Db.Db).GetAsync(default)).Id,f.Identity.PeerId,[new(id,f.Library,id.ToString(),DateTimeOffset.UtcNow,f.Identity.PeerId)]);
        var data=JsonSerializer.Serialize(plan,TaskOwnershipRepository.Json);
        var signed=new SignedTaskOwnershipPlan(data,Convert.ToBase64String(f.Identity.Sign(System.Text.Encoding.UTF8.GetBytes(data))));
        await new ClusterReconciliationRepository(f.Db.Db).SetRegisterAsync(TaskOwnershipRepository.Prefix+plan.EventId.ToString("N"),JsonSerializer.Serialize(signed,TaskOwnershipRepository.Json),default);
        await f.Ownership.ProjectAsync(default);Assert.Equal("quarantined",(await f.Ownership.GetAsync(id,default))!.State);
        await using var active=f.Db.Db.CreateCommand($"SELECT {TaskOwnershipRepository.ActiveSql("@id")}");active.Parameters.AddWithValue("id",id);Assert.False(await active.ScalarBoolAsync(default));
    }

    [Fact]
    public async Task LaterRegisterOverwriteCannotEraseAnEarlierSignedPlanDuringCatchUp()
    {
        await using var source=await Fixture.CreateAsync();await using var target=await Fixture.CreateAsync();
        var cluster=(await new ClusterIdentityRepository(source.Db.Db).GetAsync(default)).Id;
        await new ClusterIdentityRepository(target.Db.Db).AdoptAsync(cluster,"test",default);await target.AdmitAsync(source);
        var id=await source.AddAsync();string key;
        await using(var read=source.Db.Db.CreateCommand("SELECT register_key FROM cluster_sync_registers WHERE register_key LIKE 'tasks/v1/events/%'")) key=(string)(await read.ExecuteScalarAsync())!;
        var journal=new ClusterReconciliationRepository(source.Db.Db);await journal.SetRegisterAsync(key,"{}",default);
        var changes=await journal.GetEventsAfterAsync(cluster,new Dictionary<Guid,long>(),32,default);
        await new ClusterReconciliationRepository(target.Db.Db).ApplyIncomingAsync(changes,default);
        Assert.Equal(2,await target.Ownership.ProjectAsync(default));Assert.Equal(source.Identity.PeerId,(await target.Ownership.GetAsync(id,default))!.OwnerPeerId);
    }

    [Fact]
    public async Task LargeLaunchUsesBoundedPlansWithoutLeavingUnownedWork()
    {
        await using var f=await Fixture.CreateAsync();var ids=new List<Guid>();
        for(var i=0;i<130;i++) ids.Add(await f.AddAsync(assign:false));
        await using(var update=f.Db.Db.CreateCommand("UPDATE execution_work SET media_key=media_key || @padding"))
        {update.Parameters.AddWithValue("padding",new string('x',2000));await update.ExecuteNonQueryAsync();}
        await using(var c=await f.Db.Db.OpenConnectionAsync())
        await using(var tx=await c.BeginTransactionAsync()) {await f.Ownership.CreateAsync(ids,c,tx,default);await tx.CommitAsync();}
        await using var count=f.Db.Db.CreateCommand("SELECT count(*) FROM task_ownership");Assert.Equal(130L,Convert.ToInt64(await count.ExecuteScalarAsync()));
        await using var plans=f.Db.Db.CreateCommand("SELECT value FROM cluster_sync_registers WHERE register_key LIKE 'tasks/v1/events/%'");
        await using var rows=await plans.ExecuteReaderAsync();var number=0;
        while(await rows.ReadAsync()) {Assert.True(rows.GetString(0).Length<=64*1024);number++;}
        Assert.True(number>1);
    }

    [Fact]
    public async Task AdoptDatabaseFromPreviousMaster_ReassignsActiveTasksAndDoesNotThrow()
    {
        await using var f = await Fixture.CreateAsync();
        var id = await f.AddAsync();

        // Créer une nouvelle identité représentant un nouveau serveur maître
        var newRoot = Path.Combine(Path.GetTempPath(), "new-master-" + Guid.NewGuid().ToString("N"));
        using var newIdentity = new ClusterPeerIdentityStore(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["PluginSettings:KeyDirectory"] = newRoot }).Build(),
            new EphemeralDataProtectionProvider());

        var newOwnershipRepo = new TaskOwnershipRepository(f.Db.Db, newIdentity, f.Health);

        // L'initialisation doit réussir sans lever "L'identité serveur ne correspond pas à la file locale."
        await newOwnershipRepo.InitializeAsync(default);

        // Vérifier que task_server_identity a été rebindé au nouveau master
        await using var checkServer = f.Db.Db.CreateCommand("SELECT peer_id FROM task_server_identity WHERE singleton=1");
        var boundPeer = await checkServer.ExecuteScalarAsync();
        Assert.Equal(newIdentity.PeerId.ToString("D"), boundPeer?.ToString());

        // Vérifier que la tâche active a été réassignée au nouveau master
        await using var checkTask = f.Db.Db.CreateCommand("SELECT owner_peer_id FROM task_ownership WHERE execution_id=@id");
        checkTask.Parameters.AddWithValue("id", id);
        var ownerPeer = await checkTask.ExecuteScalarAsync();
        Assert.Equal(newIdentity.PeerId.ToString("D"), ownerPeer?.ToString());

        if (Directory.Exists(newRoot)) Directory.Delete(newRoot, true);
    }
}
