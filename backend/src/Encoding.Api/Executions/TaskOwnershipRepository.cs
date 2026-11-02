using System.Data.Common;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Executions;

namespace Encoding.Api.Executions;

/// <summary>
/// Propriété des tâches. Le central est l'unique serveur ([ADR 0036](../../../../docs/architecture/adr/0036-separation-stricte-central-worker.md)) :
/// chaque tâche est créée au nom de son identité locale, sans événement signé ni projection venue d'un autre serveur.
/// </summary>
public sealed class TaskOwnershipRepository(DbDataSource db,ClusterPeerIdentityStore identity)
{
    internal bool IsLocalPeer(Guid peer)=>peer==identity.PeerId;
    internal const int MaximumAssignments=128;

    // Shared fence for every local lease and source-write path. Legacy test repositories have no bound server identity.
    internal static string ActiveSql(string execution,
        bool control=false)=>$"(NOT EXISTS(SELECT 1 FROM task_server_identity) OR EXISTS(SELECT 1 FROM task_ownership own JOIN task_server_identity srv ON srv.peer_id=own.owner_peer_id WHERE own.execution_id={execution} AND own.state IN ({(control?"'active','frozen','quarantined'":"'active'")}) AND own.cluster_id=(SELECT id FROM cluster_identity WHERE singleton=true) AND srv.ready=1 AND NOT EXISTS(SELECT 1 FROM task_neutralizations stopped WHERE stopped.peer_id=srv.peer_id AND stopped.released_at IS NULL)))";
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var c=await db.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await BindIdentityAsync(c,tx,ct);
        await using var ready=Sql.Command("UPDATE task_server_identity SET ready=1",c,tx);await ready.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        await PrepareClaimsAsync(ct);
    }

    /// <summary>Rattache à ce serveur le travail local qui n'a pas encore de propriété (bases créées avant la propriété des tâches).</summary>
    internal async Task<bool> PrepareClaimsAsync(CancellationToken ct)
    {
        await using var c=await db.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await BindIdentityAsync(c,tx,ct);
        var ids=new List<Guid>();
        await using(var read=Sql.Command("SELECT w.execution_id FROM execution_work w WHERE NOT EXISTS(SELECT 1 FROM task_ownership o WHERE o.execution_id=w.execution_id) ORDER BY w.execution_id LIMIT 128",
            c,tx))
        await using(var r=await read.ExecuteReaderAsync(ct)) while(await r.ReadAsync(ct)) ids.Add(r.GetGuid(0));
        if(ids.Count>0) await CreateAsync(ids,c,tx,ct);
        await tx.CommitAsync(ct);return true;
    }

    internal async Task CreateAsync(IReadOnlyList<Guid> ids,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        await BindIdentityAsync(c,tx,ct);
        Guid cluster;
        await using(var read=Sql.Command("SELECT id FROM cluster_identity WHERE singleton=true",c,tx)) cluster=(await read.ScalarGuidAsync(ct))!.Value;
        foreach(var id in ids)
        {
            await using var insert=Sql.Command("""
                INSERT INTO task_ownership(execution_id,cluster_id,owner_peer_id,epoch,last_event_id,library_id,media_key,queued_at,state)
                SELECT w.execution_id,@cluster,@owner,1,@event,w.library_id,w.media_key,e.queued_at,'active'
                FROM execution_work w JOIN executions e ON e.id=w.execution_id
                WHERE w.execution_id=@id AND NOT EXISTS(SELECT 1 FROM task_ownership o WHERE o.execution_id=w.execution_id)
                """,c,tx);
            insert.Parameters.AddWithValue("id",id);insert.Parameters.AddWithValue("cluster",cluster);
            insert.Parameters.AddWithValue("owner",identity.PeerId);insert.Parameters.AddWithValue("event",Guid.NewGuid());
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task BindIdentityAsync(DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        Guid? previousPeer = null;
        await using (var read = Sql.Command("SELECT peer_id FROM task_server_identity WHERE singleton=1", c, tx))
        await using (var r = await read.ExecuteReaderAsync(ct))
        {
            if (await r.ReadAsync(ct) && !r.IsDBNull(0) && Guid.TryParse(r.GetString(0), out var parsed))
                previousPeer = parsed;
        }

        await using var bind = Sql.Command("INSERT INTO task_server_identity(singleton,peer_id) VALUES(1,@peer) ON CONFLICT(singleton) DO UPDATE SET peer_id=excluded.peer_id", c, tx);
        bind.Parameters.AddWithValue("peer", identity.PeerId);
        await bind.ExecuteNonQueryAsync(ct);

        if (previousPeer.HasValue && previousPeer.Value != identity.PeerId)
        {
            await using var reassign = Sql.Command("UPDATE task_ownership SET owner_peer_id=@peer WHERE owner_peer_id=@previous AND state IN ('active','frozen','quarantined')", c, tx);
            reassign.Parameters.AddWithValue("peer", identity.PeerId);
            reassign.Parameters.AddWithValue("previous", previousPeer.Value);
            await reassign.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<TaskOwnershipStatus?> GetAsync(Guid id,CancellationToken ct)
    {
        await using var cmd=db.CreateCommand("SELECT owner_peer_id,epoch,state FROM task_ownership WHERE execution_id=@id");cmd.Parameters.AddWithValue("id",
            id);await using var r=await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)?new(id,r.GetGuid(0),r.GetInt64(1),r.GetString(2)):null;
    }
}
