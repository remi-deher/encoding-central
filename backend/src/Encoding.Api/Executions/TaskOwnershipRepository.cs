using System.Data.Common;
using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Executions;

namespace Encoding.Api.Executions;

/// <summary>Creation authority is signed by its initiating server, independently of the mesh relay identity.</summary>
public sealed class TaskOwnershipRepository(DbDataSource db,ClusterPeerIdentityStore identity,ClusterReconciliationHealth? health=null,
    TaskCheckpointRepository? checkpoints=null,TaskCoordinationProjection? coordination=null)
{
    internal bool IsLocalPeer(Guid peer)=>peer==identity.PeerId;
    internal const string Prefix="tasks/v1/events/";
    internal const int MaximumAssignments=128;
    internal static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    // Shared fence for every local lease and source-write path. Legacy test repositories have no bound server identity.
    internal static string ActiveSql(string execution,
        bool control=false)=>$"(NOT EXISTS(SELECT 1 FROM task_server_identity) OR EXISTS(SELECT 1 FROM task_ownership own JOIN task_server_identity srv ON srv.peer_id=own.owner_peer_id WHERE own.execution_id={execution} AND own.state IN ({(control?"'active','frozen','quarantined'":"'active'")}) AND own.cluster_id=(SELECT id FROM cluster_identity WHERE singleton=true) AND srv.ready=1 AND NOT EXISTS(SELECT 1 FROM task_neutralizations stopped WHERE stopped.peer_id=srv.peer_id AND stopped.released_at IS NULL)))";
    internal const string BlockingPendingSql = """
        EXISTS(SELECT 1 FROM task_ownership_inbox)
        OR EXISTS(SELECT 1 FROM task_coordination_inbox)
        OR EXISTS(SELECT 1 FROM cluster_sync_pending_projections p WHERE NOT EXISTS(SELECT 1 FROM cluster_projection_diagnostics d WHERE d.event_id=p.event_id))
        """;
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var c=await db.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);await BindIdentityAsync(c,tx,ct);
        await using var reset=Sql.Command("UPDATE task_server_identity SET ready=0",c,tx);await reset.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);
    }
    public async Task<IReadOnlyList<string>> ReadinessBlockersAsync(CancellationToken ct)
    {
        var result = new List<string>();
        await using var c = await db.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.Snapshot, ct);
        foreach (var (predicate, code) in new[] {
            ("EXISTS(SELECT 1 FROM cluster_sync_pending_projections p WHERE NOT EXISTS(SELECT 1 FROM cluster_projection_diagnostics d WHERE d.event_id=p.event_id))", "pending_entity_projections"),
            ("EXISTS(SELECT 1 FROM task_ownership_inbox)", "pending_ownership_events"),
            ("EXISTS(SELECT 1 FROM task_coordination_inbox)", "pending_coordination_events") })
        {
            await using var pending = Sql.Command($"SELECT {predicate}", c, tx);
            if (await pending.ScalarBoolAsync(ct)) result.Add(code);
        }
        await using (var stopped = Sql.Command("SELECT EXISTS(SELECT 1 FROM task_neutralizations WHERE peer_id=@local AND released_at IS NULL)", c, tx))
        {
            stopped.Parameters.AddWithValue("local", identity.PeerId);
            if (await stopped.ScalarBoolAsync(ct)) result.Add("local_peer_neutralized");
        }
        bool ready;
        await using (var state = Sql.Command("SELECT EXISTS(SELECT 1 FROM task_server_identity WHERE ready=1)", c, tx))
            ready = await state.ScalarBoolAsync(ct);
        if (!ready)
        await using (var peers = Sql.Command("""
          SELECT a.peer_id,
            CASE WHEN n.id IS NOT NULL THEN n.last_seen_at<=clock_timestamp()-interval '45 seconds'
            ELSE EXISTS(
              SELECT 1 FROM cluster_nodes rn
              WHERE rn.deleted_at IS NULL AND 'server'=ANY(rn.roles)
                AND NOT EXISTS(SELECT 1 FROM cluster_peer_admissions la WHERE la.peer_id=@local AND la.node_id=rn.id AND la.revoked_at IS NULL)
                AND rn.last_seen_at<=clock_timestamp()-interval '45 seconds'
            ) END
          FROM cluster_peer_admissions a JOIN cluster_identity i ON i.id=a.cluster_id LEFT JOIN cluster_nodes n ON n.id=a.node_id
          WHERE a.revoked_at IS NULL AND a.peer_id<>@local
            AND NOT EXISTS(SELECT 1 FROM task_neutralizations stopped WHERE stopped.peer_id=a.peer_id AND stopped.released_at IS NULL)
            AND (a.node_id IS NULL OR n.deleted_at IS NULL AND 'server'=ANY(n.roles))
          """, c, tx))
        {
            peers.Parameters.AddWithValue("local", identity.PeerId);
            await using var reader = await peers.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var peer = reader.GetGuid(0);
                var nodeOffline = reader.GetBoolean(1);
                if (!nodeOffline && (health is null || !health.HasFreshCatchUp(peer, TimeSpan.FromSeconds(45))))
                    result.Add("peer_catch_up_unconfirmed:" + peer.ToString("D"));
            }
        }
        return result;
    }

    internal async Task<bool> PrepareClaimsAsync(CancellationToken ct)
    {
        await ProjectAsync(ct);
        if(coordination is not null) await coordination.ProjectAsync(ct);
        await using(var hint=db.CreateCommand("SELECT EXISTS(SELECT 1 FROM task_server_identity WHERE ready=1) AND NOT EXISTS(SELECT 1 FROM task_ownership_inbox) AND NOT EXISTS(SELECT 1 FROM task_coordination_inbox) AND NOT EXISTS(SELECT 1 FROM task_neutralizations stopped JOIN task_server_identity s ON stopped.peer_id=s.peer_id WHERE stopped.released_at IS NULL) AND NOT EXISTS(SELECT 1 FROM cluster_sync_pending_projections p WHERE NOT EXISTS(SELECT 1 FROM cluster_projection_diagnostics d WHERE d.event_id=p.event_id)) AND NOT EXISTS(SELECT 1 FROM execution_work w WHERE NOT EXISTS(SELECT 1 FROM task_ownership o WHERE o.execution_id=w.execution_id))"))
            if(await hint.ScalarBoolAsync(ct)) return true;
        await using var c=await db.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);await BindIdentityAsync(c,tx,ct);
        await using(var stopped=Sql.Command("SELECT EXISTS(SELECT 1 FROM task_neutralizations WHERE peer_id=@local AND released_at IS NULL)",c,
            tx)) {stopped.Parameters.AddWithValue("local",identity.PeerId);if(await stopped.ScalarBoolAsync(ct)) return false;}
        await using(var pending=Sql.Command($"SELECT {BlockingPendingSql}",c,tx)) if(await pending.ScalarBoolAsync(ct)) return false;
        await using(var ready=Sql.Command("SELECT ready FROM task_server_identity",c,tx))
        {
            if(!await ready.ScalarBoolAsync(ct))
            {
                var peers=new List<(Guid Peer,bool NodeOffline)>();
                await using(var read=Sql.Command("""
                  SELECT a.peer_id,
                    CASE WHEN n.id IS NOT NULL THEN n.last_seen_at<=clock_timestamp()-interval '45 seconds'
                    ELSE EXISTS(
                      SELECT 1 FROM cluster_nodes rn
                      WHERE rn.deleted_at IS NULL AND 'server'=ANY(rn.roles)
                        AND NOT EXISTS(SELECT 1 FROM cluster_peer_admissions la WHERE la.peer_id=@local AND la.node_id=rn.id AND la.revoked_at IS NULL)
                        AND rn.last_seen_at<=clock_timestamp()-interval '45 seconds'
                    ) END
                  FROM cluster_peer_admissions a JOIN cluster_identity i ON i.id=a.cluster_id LEFT JOIN cluster_nodes n ON n.id=a.node_id
                  WHERE a.revoked_at IS NULL AND a.peer_id<>@local
                    AND NOT EXISTS(SELECT 1 FROM task_neutralizations stopped WHERE stopped.peer_id=a.peer_id AND stopped.released_at IS NULL)
                    AND (a.node_id IS NULL OR n.deleted_at IS NULL AND 'server'=ANY(n.roles))
                  """,c,tx))
                {
                    read.Parameters.AddWithValue("local",identity.PeerId);await using var r=await read.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)) peers.Add((r.GetGuid(0),r.GetBoolean(1)));
                }
                if(peers.Any(item=>!item.NodeOffline && (health is null || !health.HasFreshCatchUp(item.Peer,TimeSpan.FromSeconds(45))))) return false;
                await using var allow=Sql.Command("UPDATE task_server_identity SET ready=1",c,tx);await allow.ExecuteNonQueryAsync(ct);
            }
        }
        // Upgrade only this server's pre-existing local work, in bounded chunks; remotely replicated history has no execution_work.
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
        // A launch exceeding one bounded event is partitioned into independently atomic assignment plans.
        foreach(var chunk in ids.Chunk(MaximumAssignments))
        {
            var assignments=new List<TaskAssignment>();
            foreach(var id in chunk)
            {
                await using var read=Sql.Command("SELECT w.library_id,w.media_key,e.queued_at FROM execution_work w JOIN executions e ON e.id=w.execution_id WHERE w.execution_id=@id AND NOT EXISTS(SELECT 1 FROM task_ownership o WHERE o.execution_id=w.execution_id)",
                    c,tx);
                read.Parameters.AddWithValue("id",id);await using var r=await read.ExecuteReaderAsync(ct);
                if(await r.ReadAsync(ct)) assignments.Add(new(id,r.GetGuid(0),r.GetString(1),r.GetFieldValue<DateTimeOffset>(2),identity.PeerId));
            }
            if(assignments.Count==0) continue;
            var plan=new TaskOwnershipPlan(1,Guid.NewGuid(),cluster,identity.PeerId,assignments);
            var data=JsonSerializer.Serialize(plan,Json);
            var envelope=new SignedTaskOwnershipPlan(data,Convert.ToBase64String(identity.Sign(System.Text.Encoding.UTF8.GetBytes(data))));
            var body=JsonSerializer.Serialize(envelope,Json);
            if(body.Length>64*1024)
            {
                if(assignments.Count==1) throw new InvalidOperationException("Plan d’attribution trop volumineux.");
                var half=assignments.Count/2;
                await CreateAsync(assignments.Take(half).Select(a=>a.ExecutionId).ToArray(),c,tx,ct);
                await CreateAsync(assignments.Skip(half).Select(a=>a.ExecutionId).ToArray(),c,tx,ct);
                continue;
            }
            var key=Prefix+plan.EventId.ToString("N");
            await ClusterReconciliationRepository.SetRegisterInTransactionAsync(key,body,c,tx,ct);
            await ApplyPlanAsync(key,body,c,tx,ct);
            if(checkpoints is not null) await checkpoints.MarkDirtyAsync(assignments.Select(a=>a.ExecutionId).ToArray(),c,tx,ct);
        }
    }
    private async Task BindIdentityAsync(DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        Guid? previousPeer = null;
        await using (var read = Sql.Command("SELECT peer_id FROM task_server_identity WHERE singleton=1", c, tx))
        await using (var r = await read.ExecuteReaderAsync(ct))
        {
            if (await r.ReadAsync(ct) && !r.IsDBNull(0))
            {
                if (Guid.TryParse(r.GetString(0), out var parsed))
                    previousPeer = parsed;
            }
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
    public async Task<int> ProjectAsync(CancellationToken ct)
    {
        await using(var hint=db.CreateCommand("SELECT EXISTS(SELECT 1 FROM task_ownership_inbox)")) if(!await hint.ScalarBoolAsync(ct)) return 0;
        await using var c=await db.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await BindIdentityAsync(c,tx,ct);
        var pending=new List<(string Key,string Body)>();
        await using(var read=Sql.Command("SELECT register_key,body FROM task_ownership_inbox ORDER BY origin_id,sequence LIMIT 32",c,tx))
        await using(var r=await read.ExecuteReaderAsync(ct)) while(await r.ReadAsync(ct)) pending.Add((r.GetString(0),r.GetString(1)));
        foreach(var item in pending) await ApplyPlanAsync(item.Key,item.Body,c,tx,ct);
        if(coordination is not null) await TaskCoordinationProjection.DeduplicateAsync(c,tx,ct);
        await tx.CommitAsync(ct);return pending.Count;
    }
    private async Task ApplyPlanAsync(string key,string body,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        TaskOwnershipPlan? plan=null;string? publicKey=null;string detail="Plan d’attribution invalide.";
        try
        {
            var signed=JsonSerializer.Deserialize<SignedTaskOwnershipPlan>(body,Json);
            if(signed?.Data is not null && signed.Data.Length<=ClusterReconciliationRepository.MaximumPayloadCharacters/2)
            {
                plan=JsonSerializer.Deserialize<TaskOwnershipPlan>(signed.Data,Json);
                if(plan is {Version:1} && plan.EventId!=Guid.Empty && key==Prefix+plan.EventId.ToString("N") && plan.AuthorPeerId!=Guid.Empty && plan.Assignments is {Count:>0 and <=MaximumAssignments}
                    && plan.Assignments.All(a=>a is not null && a.ExecutionId!=Guid.Empty && a.LibraryId!=Guid.Empty && a.OwnerPeerId==plan.AuthorPeerId && a.MediaKey is {Length:>0 and <=4096})
                    && plan.Assignments.Select(a=>a.ExecutionId).Distinct().Count()==plan.Assignments.Count)
                {
                    await using var admission=Sql.Command("SELECT public_key FROM cluster_peer_admissions a JOIN cluster_identity i ON i.id=a.cluster_id WHERE a.peer_id=@peer AND a.cluster_id=@cluster AND a.revoked_at IS NULL",
                        c,tx);
                    admission.Parameters.AddWithValue("peer",plan.AuthorPeerId);admission.Parameters.AddWithValue("cluster",plan.ClusterId);
                    publicKey=plan.AuthorPeerId==identity.PeerId?identity.PublicKey:await admission.ExecuteScalarAsync(ct) as string;
                    if(publicKey is null) return; // Admission projections may arrive after their signed plans.
                    await using var cluster=Sql.Command("SELECT id FROM cluster_identity WHERE singleton=true",c,tx);
                    if(await cluster.ScalarGuidAsync(ct)!=plan.ClusterId || publicKey is null
                        || !ClusterPeerIdentityStore.Verify(publicKey,System.Text.Encoding.UTF8.GetBytes(signed.Data),Convert.FromBase64String(signed.Signature))) plan=null;
                }
                else plan=null;
            }
        }
        catch(Exception ex) when(ex is JsonException or FormatException or ArgumentException) {plan=null;}
        if(plan is not null)
        {
            await using var known=Sql.Command("SELECT body FROM task_ownership_events WHERE event_id=@id",c,tx);known.Parameters.AddWithValue("id",plan.EventId);
            if(await known.ExecuteScalarAsync(ct) is string old)
            {
                if(old!=body) {plan=null;detail="Modification d’un événement immuable refusée.";}
                else
                {
                    await using var removeKnown=Sql.Command("DELETE FROM task_ownership_inbox WHERE register_key=@key AND body=@body",c,tx);
                        removeKnown.Parameters.AddWithValue("key",key);removeKnown.Parameters.AddWithValue("body",body);
                        await removeKnown.ExecuteNonQueryAsync(ct);return;
                }
            }
        }
        if(plan is not null)
        {
            foreach(var a in plan.Assignments)
            {
                await using var insert=Sql.Command("INSERT INTO task_ownership(execution_id,cluster_id,owner_peer_id,epoch,last_event_id,library_id,media_key,queued_at,state) VALUES(@id,@cluster,@owner,1,@event,@library,@key,@queued,'active') ON CONFLICT(execution_id) DO NOTHING",
                    c,tx);
                insert.Parameters.AddWithValue("id",a.ExecutionId);insert.Parameters.AddWithValue("cluster",plan.ClusterId);
                    insert.Parameters.AddWithValue("owner",a.OwnerPeerId);insert.Parameters.AddWithValue("event",plan.EventId);
                insert.Parameters.AddWithValue("library",a.LibraryId);insert.Parameters.AddWithValue("key",a.MediaKey);insert.Parameters.AddWithValue("queued",
                    a.QueuedAt);await insert.ExecuteNonQueryAsync(ct);
            }
            foreach(var a in plan.Assignments)
            {
                await using var conflict=Sql.Command("UPDATE task_ownership SET state='quarantined' WHERE execution_id=@id AND last_event_id<>@event",c,tx);
                conflict.Parameters.AddWithValue("id",a.ExecutionId);conflict.Parameters.AddWithValue("event",plan.EventId);await conflict.ExecuteNonQueryAsync(ct);
            }
            await using var record=Sql.Command("INSERT INTO task_ownership_events(event_id,register_key,body,state,detail) VALUES(@event,@key,@body,'applied','Attribution signée.') ON CONFLICT DO NOTHING",
                c,tx);
            record.Parameters.AddWithValue("event",plan.EventId);record.Parameters.AddWithValue("key",key);record.Parameters.AddWithValue("body",body);await record.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var record=Sql.Command("INSERT INTO task_ownership_events(event_id,register_key,body,state,detail) SELECT @event,@key,@body,'rejected',@detail WHERE NOT EXISTS(SELECT 1 FROM task_ownership_events WHERE register_key=@key AND body=@body AND state='rejected') ON CONFLICT DO NOTHING",
                c,tx);
            record.Parameters.AddWithValue("event",Guid.NewGuid());record.Parameters.AddWithValue("key",key);record.Parameters.AddWithValue("body",body);
                record.Parameters.AddWithValue("detail",detail);await record.ExecuteNonQueryAsync(ct);
        }
        await using var remove=Sql.Command("DELETE FROM task_ownership_inbox WHERE register_key=@key AND body=@body",c,tx);remove.Parameters.AddWithValue("key",
            key);remove.Parameters.AddWithValue("body",body);await remove.ExecuteNonQueryAsync(ct);
    }
    internal async Task SaveCheckpointAsync(Guid id,DbConnection c,DbTransaction tx,CancellationToken ct)
    {if(checkpoints is not null) await checkpoints.SaveAsync(id,c,tx,ct);}
    public async Task<TaskOwnershipStatus?> GetAsync(Guid id,CancellationToken ct)
    {
        await using var cmd=db.CreateCommand("SELECT owner_peer_id,epoch,state FROM task_ownership WHERE execution_id=@id");cmd.Parameters.AddWithValue("id",
            id);await using var r=await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)?new(id,r.GetGuid(0),r.GetInt64(1),r.GetString(2)):null;
    }
}
