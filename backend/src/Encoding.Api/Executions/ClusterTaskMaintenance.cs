using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Executions;
using System.Data.Common;
namespace Encoding.Api.Executions;

/// <summary>Independent bounded loops: presence hints never authorize ownership or destructive writes.</summary>
public sealed class ClusterTaskMaintenance(DbDataSource db,ClusterPeerIdentityStore identity,ClusterReconciliationHealth health,TaskCheckpointRepository checkpoints,
 TaskCoordinationProjection projection,ClusterTaskCoordinator coordinator,ClusterTaskTransport transport,RuntimeArtifactStore artifacts,ILogger<ClusterTaskMaintenance> logger):BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct)=>Task.WhenAll(LoopAsync(ObserveAsync,TimeSpan.FromSeconds(15),ct),LoopAsync(CacheAsync,
        TimeSpan.FromSeconds(5),ct),LoopAsync(CaptureDirtyAsync,TimeSpan.FromMilliseconds(250),ct),LoopAsync(HandoffsAsync,TimeSpan.FromSeconds(5),ct));
    private async Task LoopAsync(Func<CancellationToken,Task> work,TimeSpan delay,CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try {await projection.ProjectAsync(ct);await work(ct);}
            catch(OperationCanceledException) when(ct.IsCancellationRequested) {return;}
            catch(Exception ex) {logger.LogWarning("Coordination de tâches différée ({Type}).",ex.GetType().Name);}
            try {await Task.Delay(delay,ct);} catch(OperationCanceledException) when(ct.IsCancellationRequested) {return;}
        }
    }
    internal async Task ObserveAsync(CancellationToken ct)
    {
        var peers=new List<Guid>();Guid cluster;
        await using(var c=await db.OpenConnectionAsync(ct))
        {
            await using(var read=Sql.Command("SELECT id FROM cluster_identity WHERE singleton=true",c)) cluster=(await read.ScalarGuidAsync(ct))!.Value;
            await using(var read=Sql.Command("SELECT peer_id FROM cluster_peer_admissions WHERE cluster_id=@cluster AND revoked_at IS NULL AND peer_id<>@local",c))
            {read.Parameters.AddWithValue("cluster",cluster);read.Parameters.AddWithValue("local",identity.PeerId);
                await using var r=await read.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)) peers.Add(r.GetGuid(0));}
        }
        if(peers.Count==0) return;
        await using(var connection=await db.OpenConnectionAsync(ct))
        await using(var tx=await connection.BeginTransactionAsync(ct))
        {
            foreach(var peer in peers.Take(128))
            {
                await using var seq=Sql.Command("UPDATE task_observation_sequence SET sequence=sequence+1 WHERE singleton=1 RETURNING sequence",connection,tx);
                    var sequence=Convert.ToInt64(await seq.ExecuteScalarAsync(ct));
                await coordinator.AppendAsync(new(2,Guid.NewGuid(),cluster,identity.PeerId,"observation",Guid.Empty,0,Guid.Empty,SubjectPeerId:peer,
                    RecordedAt:DateTimeOffset.UtcNow,Sequence:sequence,Reachable:health.HasFreshAuthenticated(peer,TimeSpan.FromSeconds(45))),connection,tx,ct);
            }
            await tx.CommitAsync(ct);
        }
        await HealTwoServerClusterAsync(ct);
    }

    internal async Task HealTwoServerClusterAsync(CancellationToken ct)
    {
        await projection.ProjectAsync(ct);
        var readmitPeers=new List<Guid>();
        await using(var c=await db.OpenConnectionAsync(ct))
        await using(var read=Sql.Command("SELECT peer_id FROM task_neutralizations WHERE author_peer_id=@local AND released_at IS NULL",c))
        {
            read.Parameters.AddWithValue("local",identity.PeerId);
            await using var r=await read.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
            {
                var peer=r.GetGuid(0);
                if(health.HasFreshCatchUp(peer,TimeSpan.FromSeconds(45))) readmitPeers.Add(peer);
            }
        }
        foreach(var peer in readmitPeers)
            await coordinator.ReadmitAsync(peer,ct);

        await using(var stopped=db.CreateCommand("SELECT EXISTS(SELECT 1 FROM task_neutralizations WHERE peer_id=@local AND released_at IS NULL)"))
        {stopped.Parameters.AddWithValue("local",identity.PeerId);if(await stopped.ScalarBoolAsync(ct)) return;}

        var serverPeers=new List<(Guid Peer,bool NodeOffline,bool Neutralized)>();
        await using(var c=await db.OpenConnectionAsync(ct))
        await using(var read=Sql.Command("""
          SELECT DISTINCT a.peer_id,
            CASE WHEN n.id IS NOT NULL THEN n.last_seen_at<=clock_timestamp()-interval '90 seconds'
            ELSE EXISTS(
              SELECT 1 FROM cluster_nodes rn
              WHERE rn.deleted_at IS NULL AND 'server'=ANY(rn.roles)
                AND NOT EXISTS(SELECT 1 FROM cluster_peer_admissions la WHERE la.peer_id=@local AND la.node_id=rn.id AND la.revoked_at IS NULL)
                AND rn.last_seen_at<=clock_timestamp()-interval '90 seconds'
            ) END,
            EXISTS(SELECT 1 FROM task_neutralizations s WHERE s.peer_id=a.peer_id AND s.released_at IS NULL)
          FROM cluster_peer_admissions a
          JOIN cluster_identity i ON i.id=a.cluster_id
          LEFT JOIN cluster_nodes n ON n.id=a.node_id
          WHERE a.revoked_at IS NULL AND a.peer_id<>@local
            AND (a.node_id IS NULL OR n.deleted_at IS NULL AND 'server'=ANY(n.roles))
          """,c))
        {
            read.Parameters.AddWithValue("local",identity.PeerId);
            await using var r=await read.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
            {
                var peerId=r.GetGuid(0);
                var offline=!r.IsDBNull(1)&&Convert.ToInt64(r.GetValue(1))!=0;
                var neutralized=!r.IsDBNull(2)&&Convert.ToInt64(r.GetValue(2))!=0;
                serverPeers.Add((peerId,offline,neutralized));
            }
        }
        if(serverPeers.Count!=1) return;
        var remote=serverPeers[0];
        if(!remote.Neutralized && remote.NodeOffline && !health.HasFreshAuthenticated(remote.Peer,TimeSpan.FromSeconds(45)))
        {
            bool hasBlockedWork;
            await using(var c=await db.OpenConnectionAsync(ct))
            await using(var check=Sql.Command("""
              SELECT EXISTS(
                SELECT 1 FROM task_ownership o LEFT JOIN executions e ON e.id=o.execution_id
                WHERE o.owner_peer_id=@peer AND o.state IN ('active','frozen') AND (e.id IS NULL OR e.state IN ('queued','running'))
              ) OR EXISTS(
                SELECT 1 FROM task_quarantines q JOIN task_ownership winner ON winner.execution_id=q.winner_id
                WHERE winner.owner_peer_id=@peer
              ) OR EXISTS(
                SELECT 1 FROM runtime_source_write_waits
              ) OR EXISTS(
                SELECT 1 FROM pending_source_writes WHERE state IN ('waiting','claimed')
              )
              """,c))
            {
                check.Parameters.AddWithValue("peer",remote.Peer);
                hasBlockedWork=await check.ScalarBoolAsync(ct);
            }
            if(hasBlockedWork)
                await coordinator.NeutralizeAsync(remote.Peer,new(true,"Neutralisation automatique : second nœud serveur hors ligne et injoignable."),ct);
        }

        var recoverable=new List<Guid>();
        await using(var c=await db.OpenConnectionAsync(ct))
        await using(var read=Sql.Command("""
          SELECT o.execution_id
          FROM task_ownership o
          JOIN task_neutralizations n ON n.peer_id=o.owner_peer_id AND n.released_at IS NULL AND n.author_peer_id=@local
          LEFT JOIN executions e ON e.id=o.execution_id
          WHERE o.owner_peer_id<>@local AND o.state='active' AND (e.id IS NULL OR e.state IN ('queued','running'))
          ORDER BY o.queued_at,o.execution_id LIMIT 16
          """,c))
        {
            read.Parameters.AddWithValue("local",identity.PeerId);
            await using var r=await read.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) recoverable.Add(r.GetGuid(0));
        }
        foreach(var id in recoverable)
            await coordinator.RecoverAsync(id,ct);
    }
    internal async Task CaptureDirtyAsync(CancellationToken ct)
    {
        // Fixed predicate reused by SELECT/DELETE — not user input.
        const string ObsoleteDirtyCheckpointPredicate =
            "EXISTS(SELECT 1 FROM executions e WHERE e.id=d.execution_id AND e.finished_at IS NOT NULL) OR EXISTS(SELECT 1 FROM task_ownership o WHERE o.execution_id=d.execution_id AND (o.owner_peer_id<>@local OR o.state NOT IN ('active','frozen')))";
        await using(var hint=db.CreateCommand("SELECT EXISTS(SELECT 1 FROM task_checkpoint_dirty d WHERE " + ObsoleteDirtyCheckpointPredicate + ")"))
        {
            hint.Parameters.AddWithValue("local",identity.PeerId);
            if(await hint.ScalarBoolAsync(ct)) {await using var clean=db.CreateCommand("DELETE FROM task_checkpoint_dirty AS d WHERE " + ObsoleteDirtyCheckpointPredicate);
                clean.Parameters.AddWithValue("local",identity.PeerId);await clean.ExecuteNonQueryAsync(ct);}
        }
        var ids=new List<Guid>();
        await using(var read=db.CreateCommand("SELECT d.execution_id FROM task_checkpoint_dirty d JOIN execution_work w ON w.execution_id=d.execution_id JOIN task_ownership o ON o.execution_id=d.execution_id JOIN executions e ON e.id=d.execution_id WHERE o.owner_peer_id=@local AND o.state='active' AND EXISTS(SELECT 1 FROM task_server_identity srv WHERE srv.peer_id=o.owner_peer_id AND srv.ready=1) AND e.state IN ('queued','running') ORDER BY d.attempts,d.execution_id LIMIT 16"))
        {read.Parameters.AddWithValue("local",identity.PeerId);await using var r=await read.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)) ids.Add(r.GetGuid(0));}
        foreach(var id in ids)
        {
            await using var c=await db.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
            await using(var attempt=Sql.Command("UPDATE task_checkpoint_dirty SET attempts=attempts+1 WHERE execution_id=@id",c,
                tx)) {attempt.Parameters.AddWithValue("id",id);await attempt.ExecuteNonQueryAsync(ct);}
            await checkpoints.SaveAsync(id,c,tx,ct);await tx.CommitAsync(ct);
        }
    }
    private async Task CacheAsync(CancellationToken ct)
    {
        var candidates=new List<(Guid Id,Guid Peer,string Body)>();
        await using(var read=db.CreateCommand("SELECT p.execution_id,p.author_peer_id,p.body FROM task_checkpoints p JOIN task_ownership o ON o.execution_id=p.execution_id AND o.epoch=p.epoch LEFT JOIN executions e ON e.id=p.execution_id WHERE p.author_peer_id<>@local AND o.state='active' AND COALESCE(e.state,'queued') IN ('queued','running') AND NOT EXISTS(SELECT 1 FROM task_checkpoint_cached cached WHERE cached.execution_id=p.execution_id AND cached.body=p.body) ORDER BY p.execution_id LIMIT 128"))
        {read.Parameters.AddWithValue("local",identity.PeerId);await using var r=await read.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) candidates.Add((r.GetGuid(0),r.GetGuid(1),r.GetString(2)));}
        var fetched=0;
        foreach(var candidate in candidates)
        {
            if(!health.HasFreshCatchUp(candidate.Peer,TimeSpan.FromSeconds(45))) continue;
            var signed=JsonSerializer.Deserialize<SignedTaskOwnershipPlan>(candidate.Body,TaskCheckpointRepository.Json)!;
                var e=JsonSerializer.Deserialize<ClusterTaskEvent>(signed.Data,TaskCheckpointRepository.Json)!;var value=checkpoints.Decode(e);
            foreach(var blob in value.Artifacts)
            {
                var path=artifacts.PathFor(candidate.Id,blob.Sha256);if(File.Exists(path)&&new FileInfo(path).Length==blob.Bytes) continue;
                await transport.FetchAsync(candidate.Peer,candidate.Id,blob,ct);if(++fetched>=4) return;
            }
            await using var cached=db.CreateCommand("INSERT INTO task_checkpoint_cached(execution_id,body) VALUES(@id,@body) ON CONFLICT(execution_id) DO UPDATE SET body=excluded.body");
                cached.Parameters.AddWithValue("id",candidate.Id);cached.Parameters.AddWithValue("body",candidate.Body);await cached.ExecuteNonQueryAsync(ct);
        }
    }
    internal async Task HandoffsAsync(CancellationToken ct)
    {
        await using(var stopped=db.CreateCommand("SELECT EXISTS(SELECT 1 FROM task_neutralizations WHERE peer_id=@local AND released_at IS NULL)")) {stopped.Parameters.AddWithValue("local",
            identity.PeerId);if(await stopped.ScalarBoolAsync(ct)) return;}
        Guid? id=null;Guid? target=null;
        await using(var read=db.CreateCommand("SELECT w.execution_id,w.handoff_selector->>'targetNodeId' FROM execution_work w JOIN task_ownership o ON o.execution_id=w.execution_id JOIN task_server_identity s ON s.peer_id=o.owner_peer_id WHERE o.state='active' AND s.ready=1 AND w.handoff_pending AND w.handoff_selector->>'mode'='server' ORDER BY w.execution_id LIMIT 1"))
        await using(var r=await read.ExecuteReaderAsync(ct)) if(await r.ReadAsync(ct)) {id=r.GetGuid(0);if(Guid.TryParse(r.GetString(1),out var peer)) target=peer;}
        if(id is not null&&target is not null && health.HasFreshCatchUp(target.Value,TimeSpan.FromSeconds(45)))
        {
            await coordinator.MoveAsync(id.Value,target.Value,ct);
        }
        // A crashed source can safely undo only proposals for which no activation was ever emitted.
        var stale=new List<string>();
        await using(var read=db.CreateCommand("SELECT snapshot FROM task_transfers WHERE source_peer_id=@local AND state='proposed' AND created_at<clock_timestamp()-interval '35 minutes' LIMIT 4"))
        {read.Parameters.AddWithValue("local",identity.PeerId);await using var r=await read.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)) stale.Add(r.GetString(0));}
        foreach(var body in stale)
        {
            var signed=JsonSerializer.Deserialize<SignedTaskOwnershipPlan>(body,TaskCheckpointRepository.Json)!;
                var proposal=JsonSerializer.Deserialize<ClusterTaskEvent>(signed.Data,TaskCheckpointRepository.Json)!;
            await coordinator.AbortExpiredAsync(proposal,ct);
        }
    }
    public async Task<ClusterTaskClusterStatus> StatusAsync(CancellationToken ct)
    {
        var observations=new List<ClusterTaskObservation>();int writes,transfers;
        await using(var read=db.CreateCommand("SELECT observer_peer_id,subject_peer_id,sequence,observed_at,expires_at,reachable FROM task_peer_observations WHERE expires_at>clock_timestamp() ORDER BY observer_peer_id,subject_peer_id"))
        await using(var r=await read.ExecuteReaderAsync(ct)) while(await r.ReadAsync(ct)) observations.Add(new(r.GetGuid(0),r.GetGuid(1),r.GetInt64(2),
            r.GetFieldValue<DateTimeOffset>(3),r.GetFieldValue<DateTimeOffset>(4),r.GetBoolean(5)));
        await using(var read=db.CreateCommand("SELECT (SELECT count(*) FROM runtime_source_write_waits)+(SELECT count(*) FROM pending_source_writes WHERE state IN ('waiting','claimed')),(SELECT count(*) FROM task_transfers WHERE state IN ('proposed','received'))"))
        await using(var r=await read.ExecuteReaderAsync(ct)) {await r.ReadAsync(ct);writes=r.GetInt32(0);transfers=r.GetInt32(1);}
        var isolated=false;var catchingUp=false;
        await using(var read=db.CreateCommand("SELECT a.peer_id FROM cluster_peer_admissions a JOIN cluster_identity i ON i.id=a.cluster_id LEFT JOIN cluster_nodes n ON n.id=a.node_id WHERE a.revoked_at IS NULL AND a.peer_id<>@local AND (a.node_id IS NULL OR n.deleted_at IS NULL AND 'server'=ANY(n.roles)) AND NOT EXISTS(SELECT 1 FROM task_neutralizations s WHERE s.peer_id=a.peer_id AND s.released_at IS NULL)"))
        {read.Parameters.AddWithValue("local",identity.PeerId);await using var r=await read.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) {var peer=r.GetGuid(0);var reachable=health.HasFreshAuthenticated(peer,TimeSpan.FromSeconds(45));isolated|=!reachable;
            catchingUp|=reachable&&!health.HasFreshCatchUp(peer,TimeSpan.FromSeconds(45));}}
        int preparations,caches;
        await using(var read=db.CreateCommand("SELECT (SELECT count(*) FROM task_checkpoint_dirty),(SELECT count(*) FROM task_checkpoints p JOIN task_ownership o ON o.execution_id=p.execution_id AND o.epoch=p.epoch LEFT JOIN executions e ON e.id=p.execution_id WHERE p.author_peer_id<>@local AND o.state='active' AND COALESCE(e.state,'queued') IN ('queued','running') AND NOT EXISTS(SELECT 1 FROM task_checkpoint_cached x WHERE x.execution_id=p.execution_id AND x.body=p.body))"))
        {read.Parameters.AddWithValue("local",identity.PeerId);await using var r=await read.ExecuteReaderAsync(ct);await r.ReadAsync(ct);preparations=r.GetInt32(0);caches=r.GetInt32(1);}
        await using var stopped=db.CreateCommand("SELECT EXISTS(SELECT 1 FROM task_neutralizations WHERE peer_id=@local AND released_at IS NULL)");
            stopped.Parameters.AddWithValue("local",identity.PeerId);
        return new(isolated,writes,transfers,observations,identity.PeerId,preparations,caches,await stopped.ScalarBoolAsync(ct),catchingUp,await projection.StatusAsync(ct));
    }
}
