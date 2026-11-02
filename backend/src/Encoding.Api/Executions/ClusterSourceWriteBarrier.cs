using System.Data.Common;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;

namespace Encoding.Api.Executions;

/// <summary>Connectivity is a prerequisite, not distributed media ownership. Filesystem exclusion is checked by the writer.</summary>
public sealed class ClusterSourceWriteBarrier(DbDataSource db,ClusterReconciliationHealth health,ClusterPeerIdentityStore identity,
    ClusterReconciliationService? synchronization=null,ILogger<ClusterSourceWriteBarrier>? logger=null,TaskCoordinationProjection? coordination=null)
{
    private readonly SemaphoreSlim refresh=new(1,1);
    internal async Task<bool> VerifyAsync(CancellationToken ct)
    {
        // No writer admission is held during network exchange. Only destructive operations use this barrier.
        if(synchronization is null || !await IsClusteredAsync(ct)) {if(coordination is not null) await coordination.ProjectAsync(ct);return true;}
        await refresh.WaitAsync(ct);
        try
        {
            var endpoints=new List<string>();
            await using(var c=await db.OpenConnectionAsync(ct))
            await using(var peers=Sql.Command("SELECT DISTINCT a.peer_id FROM cluster_peer_admissions a JOIN cluster_identity i ON i.id=a.cluster_id LEFT JOIN cluster_nodes n ON n.id=a.node_id WHERE a.revoked_at IS NULL AND a.peer_id<>@local AND NOT EXISTS(SELECT 1 FROM task_neutralizations stopped WHERE stopped.peer_id=a.peer_id AND stopped.released_at IS NULL) AND (a.node_id IS NULL OR n.deleted_at IS NULL AND 'server'=ANY(n.roles))",
                c))
            {
                peers.Parameters.AddWithValue("local",identity.PeerId);
                await using var rows=await peers.ExecuteReaderAsync(ct);
                while(await rows.ReadAsync(ct))
                {
                    var endpoint=health.AuthenticatedEndpoint(rows.GetGuid(0));if(endpoint is null) return false;
                    endpoints.Add(endpoint);
                }
            }
            // Do not scan stale discovery aliases in the write-critical path.
            await synchronization.RefreshForSourceWritesAsync(endpoints.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),ct);
                if(coordination is not null) await coordination.ProjectAsync(ct);return true;
        }
        catch(Exception exception) when(exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger?.LogWarning("Écriture source différée : synchronisation préalable indisponible ({Type}).",exception.GetType().Name);
            return false;
        }
        finally { refresh.Release(); }
    }
    public async Task<bool> IsClusteredAsync(CancellationToken ct)
    {
        await using var c=await db.OpenConnectionAsync(ct);return await IsClusteredAsync(c,null,ct);
    }
    internal async Task<bool> IsClusteredAsync(DbConnection c,DbTransaction? tx,CancellationToken ct)
    {
        await using var cmd=Sql.Command("SELECT EXISTS(SELECT 1 FROM cluster_peer_admissions a JOIN cluster_identity i ON i.id=a.cluster_id LEFT JOIN cluster_nodes n ON n.id=a.node_id WHERE a.revoked_at IS NULL AND a.peer_id<>@local AND NOT EXISTS(SELECT 1 FROM task_neutralizations stopped WHERE stopped.peer_id=a.peer_id AND stopped.released_at IS NULL) AND (a.node_id IS NULL OR n.deleted_at IS NULL AND 'server'=ANY(n.roles)))",
            c,tx);
        cmd.Parameters.AddWithValue("local",identity.PeerId);return await cmd.ScalarBoolAsync(ct);
    }
    internal async Task<bool> SupportsGuardAsync(Guid node,DbConnection c,DbTransaction? tx,CancellationToken ct)
    {
        await using(var actor=Sql.Command("SELECT EXISTS(SELECT 1 FROM cluster_nodes WHERE id=@node AND deleted_at IS NULL AND NOT quarantined AND incompatible_reason IS NULL)",c,tx))
        {actor.Parameters.AddWithValue("node",node);if(!await actor.ScalarBoolAsync(ct)) return false;}
        if(!await IsClusteredAsync(c,tx,ct)) return true;
        await using var cmd=Sql.Command("SELECT EXISTS(SELECT 1 FROM cluster_nodes n, json_each(n.capabilities) cap WHERE n.id=@node AND n.deleted_at IS NULL AND json_extract(cap.value,'$.toolId')=@cap)",
            c,tx);
        cmd.Parameters.AddWithValue("node",node);cmd.Parameters.AddWithValue("cap",Encoding.Contracts.Executions.SourceWriteProtocol.Capability);return await cmd.ScalarBoolAsync(ct);
    }
    public async Task<string?> CheckAsync(CancellationToken ct)
    {
        await using var c=await db.OpenConnectionAsync(ct);
        return await CheckAsync(c,null,ct);
    }
    internal async Task<string?> CheckAsync(DbConnection c,DbTransaction? tx,CancellationToken ct)
    {
        await using(var ownership=Sql.Command($"SELECT EXISTS(SELECT 1 FROM task_server_identity WHERE ready=0) OR {TaskOwnershipRepository.BlockingPendingSql} OR EXISTS(SELECT 1 FROM task_neutralizations stopped JOIN task_server_identity s ON s.peer_id=stopped.peer_id WHERE stopped.released_at IS NULL)",
            c,tx))
            if(await ownership.ScalarBoolAsync(ct)) return "Écriture source différée : propriété des tâches à réconcilier.";
        await using var cmd=Sql.Command("""
          SELECT DISTINCT a.peer_id FROM cluster_peer_admissions a
          JOIN cluster_identity i ON i.id=a.cluster_id
          LEFT JOIN cluster_nodes n ON n.id=a.node_id
          WHERE a.revoked_at IS NULL AND a.peer_id<>@local AND NOT EXISTS(SELECT 1 FROM task_neutralizations stopped WHERE stopped.peer_id=a.peer_id AND stopped.released_at IS NULL)
            AND (a.node_id IS NULL OR (n.deleted_at IS NULL AND 'server'=ANY(n.roles)))
          """,c,tx);
        cmd.Parameters.AddWithValue("local",identity.PeerId);
        await using var r=await cmd.ExecuteReaderAsync(ct);var peers=new List<Guid>();
        while(await r.ReadAsync(ct)) peers.Add(r.GetGuid(0));
        if(peers.Count==0) return null; // An autonomous installation does not wait for a nonexistent peer.
        await r.DisposeAsync();
        await using(var pending=Sql.Command("SELECT EXISTS(SELECT 1 FROM cluster_sync_pending_projections p WHERE NOT EXISTS(SELECT 1 FROM cluster_projection_diagnostics d WHERE d.event_id=p.event_id))",
            c,tx))
            if(await pending.ScalarBoolAsync(ct)) return "Écriture sur la source différée : des projections du journal restent à réconcilier.";
        if(!health.GetStatus().Enabled || peers.Any(peer=>!health.HasFreshCatchUp(peer,TimeSpan.FromSeconds(45))))
            return "Écriture sur la source différée : attendre les échanges signés et le rattrapage des serveurs du cluster.";
        if(peers.Any(peer=>!health.HasFreshSourceWriteSupport(peer,TimeSpan.FromSeconds(45))))
            return "Écriture sur la source différée : protection des sources à confirmer sur tous les serveurs (mise à jour ou liaison à vérifier).";
        return null;
    }
}
