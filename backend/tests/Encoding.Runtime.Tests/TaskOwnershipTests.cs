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
        internal Guid Library=Guid.NewGuid();
        private readonly string root=Path.Combine(Path.GetTempPath(),"ownership-"+Guid.NewGuid().ToString("N"));
        internal static async Task<Fixture> CreateAsync()
        {
            var f=new Fixture();f.Db=await TestDatabase.CreateSqliteAsync("ownership");await DatabaseMigrator.ApplyAsync(f.Db.Db,Harness.RepositoryRoot(),default);
            f.Identity=new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["PluginSettings:KeyDirectory"]=f.root}).Build(),new EphemeralDataProtectionProvider());
            f.Ownership=new(f.Db.Db,f.Identity);
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
    public async Task CreationCommitsWithTheTaskAndNamesTheLocalServerAsOwner()
    {
        await using var f=await Fixture.CreateAsync();var rolledBack=await f.AddAsync(false);Assert.Null(await f.Ownership.GetAsync(rolledBack,default));
        var id=await f.AddAsync();var owner=await f.Ownership.GetAsync(id,default);Assert.Equal(f.Identity.PeerId,owner!.OwnerPeerId);Assert.Equal(1,owner.Epoch);Assert.Equal("active",owner.State);
    }

    [Fact]
    public async Task LegacyWorkWithoutOwnershipIsAttachedToTheLocalServer()
    {
        await using var f=await Fixture.CreateAsync();
        var id=await f.AddAsync(assign:false);Assert.Null(await f.Ownership.GetAsync(id,default));
        Assert.True(await f.Ownership.PrepareClaimsAsync(default));
        Assert.Equal(f.Identity.PeerId,(await f.Ownership.GetAsync(id,default))!.OwnerPeerId);
    }

    [Fact]
    public async Task LargeLaunchLeavesNoUnownedWork()
    {
        await using var f=await Fixture.CreateAsync();var ids=new List<Guid>();
        for(var i=0;i<130;i++) ids.Add(await f.AddAsync(assign:false));
        await using(var c=await f.Db.Db.OpenConnectionAsync())
        await using(var tx=await c.BeginTransactionAsync()) {await f.Ownership.CreateAsync(ids,c,tx,default);await tx.CommitAsync();}
        await using var count=f.Db.Db.CreateCommand("SELECT count(*) FROM task_ownership");Assert.Equal(130L,Convert.ToInt64(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task ChangingTheServerIdentityReassignsActiveTasks()
    {
        await using var f = await Fixture.CreateAsync();
        var id = await f.AddAsync();

        // Créer une nouvelle identité représentant un nouveau serveur maître
        var newRoot = Path.Combine(Path.GetTempPath(), "new-master-" + Guid.NewGuid().ToString("N"));
        using var newIdentity = new ClusterPeerIdentityStore(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["PluginSettings:KeyDirectory"] = newRoot }).Build(),
            new EphemeralDataProtectionProvider());

        var newOwnershipRepo = new TaskOwnershipRepository(f.Db.Db, newIdentity);

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
