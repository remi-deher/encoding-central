using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Workers;
using System.Data.Common;
using System.Text.Json;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Workers;

namespace Encoding.Api.Executions;

public sealed class RuntimeRecoveryService(DbDataSource dataSource,ILogger<RuntimeRecoveryService> logger,
    WorkerGroupRepository? workerGroups = null, RuntimeEligibility? eligibility = null, WorkerPresenceOptions? presence = null) : BackgroundService
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private readonly TimeSpan offlineAfter=presence?.OfflineAfter??TimeSpan.FromSeconds(30);
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try
            {

                await ProcessOnceAsync(ct);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { break; }
            catch(Exception exception) { logger.LogWarning("Reprise du registre momentanément indisponible : {Type}.",exception.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(1),ct); } catch(OperationCanceledException) when(ct.IsCancellationRequested) { break; }
        }
    }

    /// <summary>Délai avant de déclarer qu'aucun worker du central ne peut jamais exécuter une exécution neuve (ADR 0024).</summary>
    private static readonly TimeSpan FreshPlacementGrace=TimeSpan.FromMinutes(2);

    // Les reprises héritées ont déjà rendu leur bail : leur fermeture ne dépend plus de son expiration.
    private static readonly string ExpiredWorkPredicate = $"""
        ((e.state='cancelling' AND (w.lease_until IS NULL OR w.lease_until<=(strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z')))
        OR (e.state='running' AND w.worker_id IS NOT NULL AND w.lease_until IS NOT NULL AND w.lease_until<=(strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z'))
        OR (e.state IN ('queued','running','cancelling') AND w.worker_id IS NULL AND w.handoff_pending AND w.resume_count>0))
        """;

    private async Task<bool> HasExpiredWorkAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = Sql.Command($"SELECT EXISTS(SELECT 1 FROM executions e JOIN execution_work w ON w.execution_id=e.id WHERE {ExpiredWorkPredicate})", connection);
        return await command.ScalarBoolAsync(ct);
    }

    /// <summary>
    /// Closes cancelled work. An attempt whose assigned worker lost its lease fails (<c>worker_perdu</c>) and is never reassigned
    /// under the same execution ID (ADR 0044): locks and reservations are released, artifacts and source are kept. A new execution
    /// is created only by an operator request or by the bounded <c>nodes-retry-on-worker-loss</c> rule of the flow.
    /// Pending automatic resumptions persisted by older versions are also closed without an automatic retry.
    /// An explicit relay or wait with no historic resumption is not concerned.
    /// </summary>
    public async Task<int> ProcessOnceAsync(CancellationToken ct)
    {
        // This is only an admission hint: all predicates are rechecked inside the writer transaction.
        if (!await HasExpiredWorkAsync(ct) && !await RuntimeRepository.HasUnavailableSourcesAsync(dataSource, offlineAfter, ct))
            return await FailUnavailableTargetsAsync(ct);
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        await RuntimeRepository.FailUnavailableSourcesAsync(c, tx, offlineAfter, ct);
        var expired=new List<(Guid Id,Guid? Worker,int Revision,string State,int Attempt,string Flow,bool HasNonIdempotentStep,bool Uncertain,bool LegacyResume)>();
        await using(var cmd=Sql.Command($"""
          SELECT e.id,e.worker_id,e.revision,e.state,e.attempt,e.flow_definition,
            EXISTS(SELECT 1 FROM execution_steps s WHERE s.execution_id=e.id AND NOT s.idempotent),
            EXISTS(SELECT 1 FROM execution_steps s WHERE s.execution_id=e.id AND s.completed_at IS NULL AND NOT s.idempotent),
            w.worker_id IS NULL AND w.handoff_pending AND w.resume_count>0
          FROM executions e JOIN execution_work w ON w.execution_id=e.id
          WHERE {ExpiredWorkPredicate}
          ORDER BY e.updated_at LIMIT 100
          """,c,tx))
        await using(var r=await cmd.ExecuteReaderAsync(ct))
        { while(await r.ReadAsync(ct)) expired.Add((r.GetGuid(0),r.IsDBNull(1)?null:r.GetGuid(1),r.GetInt32(2),r.GetString(3),r.GetInt32(4),r.GetString(5),
            r.GetBoolean(6),r.GetBoolean(7),r.GetBoolean(8))); }
        foreach(var item in expired)
        {
            var cancelled=item.State=="cancelling";
            var detail=cancelled
                ? "Annulation terminée après expiration du bail ; la source est conservée."
                : item.LegacyResume
                    ? "worker_perdu : reprise automatique héritée clôturée après mise à niveau, sans réattribution. Les effets antérieurs doivent être vérifiés avant une relance explicite en nouvelle exécution."
                : item.Uncertain
                    ? "worker_perdu : le worker a perdu son bail pendant une étape à effet externe. Le résultat est incertain ; la tentative est en erreur et une relance explicite doit créer une nouvelle exécution."
                : "worker_perdu : le worker a perdu son bail pendant cette tentative. Elle est en erreur, sans réattribution ; une relance explicite doit créer une nouvelle exécution.";
            if(await ExecutionRepository.RecordInTransactionAsync(item.Id,new(Guid.NewGuid(),item.Revision,cancelled?"cancelled":"failed",item.Worker,null,detail),c,tx,ct))
            {
                await RuntimeRepository.ReleaseAsync(item.Id,c,tx,ct);
                if(!cancelled && !item.LegacyResume && !item.HasNonIdempotentStep && item.Worker is {} previous)
                {
                    try
                    {
                        var flow=JsonSerializer.Deserialize<FlowDefinition>(item.Flow,new JsonSerializerOptions(JsonSerializerDefaults.Web));
                        var policy=flow is null?null:RuntimeRetryPolicy.Resolve(flow);
                        if(policy is not null && item.Attempt<=policy.MaximumRetries)
                        {
                            var child=await ExecutionRepository.RetryInTransactionAsync(item.Id,policy.Selector,previous,
                                $"Nouvelle tentative {item.Attempt+1} créée par la règle du flow après perte du worker.",c,tx,ct);
                            if(child is not null) logger.LogInformation("L’exécution {ExecutionId} crée la tentative bornée {RetryId} après perte du worker {WorkerId}.",item.Id,child,previous);
                        }
                    }
                    catch(ArgumentException exception)
                    {
                        logger.LogWarning("La règle de relance du flow de {ExecutionId} est invalide : {Type}.",item.Id,exception.GetType().Name);
                    }
                }
            }
        }
        await tx.CommitAsync(ct);
        return expired.Count+await FailUnavailableTargetsAsync(ct);
    }

    private async Task<int> FailUnavailableTargetsAsync(CancellationToken ct)
    {
        if(workerGroups is null || eligibility is null) return 0;
        var targets=new List<(Guid Id,Guid? Actor,int Revision,string State,string Flow,string Selector,Guid? Previous,
            Guid Library,Guid? Source,string? Checkpoint,DateTimeOffset Updated,FlowPlacementPlan? Plan)>();
        await using(var c=await dataSource.OpenConnectionAsync(ct))
        await using(var cmd=Sql.Command($"""
          SELECT e.id,e.worker_id,e.revision,e.state,e.flow_definition,COALESCE(w.handoff_selector,''),
            w.previous_worker_id,w.library_id,w.source_worker_id,
            (SELECT checkpoint FROM execution_steps s WHERE s.execution_id=e.id AND s.completed_at IS NOT NULL ORDER BY s.completed_revision DESC LIMIT 1),e.updated_at,w.placement_plan
          FROM executions e JOIN execution_work w ON w.execution_id=e.id
          WHERE w.worker_id IS NULL AND w.available_at<=(strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z')
            AND (w.handoff_selector IS NOT NULL
                  AND ((e.state='running' AND w.handoff_pending) OR (e.state='queued' AND e.retry_of IS NOT NULL))
              OR w.handoff_selector IS NULL AND e.state='queued' AND e.retry_of IS NULL AND w.lease_until IS NULL
                  AND e.queued_at<(strftime('%Y-%m-%d %H:%M:%f', 'now', '-' || (@freshGrace) || ' seconds') || '000Z'))
          ORDER BY e.updated_at LIMIT 100
          """,c))
        {
        cmd.Parameters.AddWithValue("freshGrace",FreshPlacementGrace.TotalSeconds);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        { while(await r.ReadAsync(ct)) targets.Add((r.GetGuid(0),r.IsDBNull(1)?null:r.GetGuid(1),r.GetInt32(2),r.GetString(3),r.GetString(4),r.GetString(5),
            r.IsDBNull(6)?null:r.GetGuid(6),r.GetGuid(7),r.IsDBNull(8)?null:r.GetGuid(8),r.IsDBNull(9)?null:r.GetString(9),r.GetFieldValue<DateTimeOffset>(10),
            r.IsDBNull(11)?null:JsonSerializer.Deserialize<FlowPlacementPlan>(r.GetString(11),Json))); }
        }

        var failed=0;
        foreach(var item in targets)
        {
            var flow=JsonSerializer.Deserialize<FlowDefinition>(item.Flow,Json);
            // Exécution neuve (sans sélecteur) : seule l'impossibilité statique la fait échouer, une absence de présence ne compte pas.
            var fresh=item.Selector.Length==0;
            var selector=fresh?new RuntimeHandoffSelector("eligible"):JsonSerializer.Deserialize<RuntimeHandoffSelector>(item.Selector,Json);
            var checkpoint=item.Checkpoint is null?null:JsonSerializer.Deserialize<RuntimeCheckpoint>(item.Checkpoint,Json);
            if(flow is null || selector is null) continue;
            if(await HasEligibleWorkerAsync(item,flow,selector,checkpoint,fresh,ct)) continue;

            await using var c=await dataSource.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
            var detail=fresh?"placement_impossible: "+(item.Plan?.Reason??"Aucun worker du central ne peut exécuter ce flow (capacité absente, désignation de worker impossible ou aucun accès à la bibliothèque)."):
                "Aucun worker en ligne ne correspond à la destination de ce relais ou de cette relance ; l’exécution échoue.";
            if(await ExecutionRepository.RecordInTransactionAsync(item.Id,new(Guid.NewGuid(),item.Revision,"failed",item.Actor,null,detail),c,tx,ct))
            {
                await RuntimeRepository.ReleaseAsync(item.Id,c,tx,ct);
                await tx.CommitAsync(ct);
                failed++;
                logger.LogInformation("L’exécution {ExecutionId} est en erreur : aucun worker ne satisfait sa cible.",item.Id);
            }
        }
        return failed;
    }

    private async Task<bool> HasEligibleWorkerAsync(
        (Guid Id,Guid? Actor,int Revision,string State,string Flow,string Selector,Guid? Previous,Guid Library,Guid? Source,string? Checkpoint,DateTimeOffset Updated,FlowPlacementPlan? Plan) item,
        FlowDefinition flow,RuntimeHandoffSelector selector,RuntimeCheckpoint? checkpoint,bool fresh,CancellationToken ct)
    {
        // Un worker momentanément hors ligne, en drainage ou hors plage horaire reste un destinataire possible :
        // les relais, reprises et relances attendent le retour du worker sans mettre la tâche en échec.
        var offline=TimeSpan.FromDays(36500);
        var candidates=new List<(Guid Id,WorkerToolCapability[] Capabilities,int Capacity,bool ScheduleEnabled,string? Timezone,string? Slots,WorkerInventory? Inventory)>();
        await using(var c=await dataSource.OpenConnectionAsync(ct))
        await using(var cmd=Sql.Command("""
          SELECT n.id,n.capabilities,
            COALESCE((SELECT p.max_concurrent_tasks FROM worker_processing_settings p WHERE p.worker_id=n.id),n.max_concurrent_tasks),
            COALESCE((SELECT p.schedule_enabled FROM worker_processing_settings p WHERE p.worker_id=n.id),false),
            (SELECT p.schedule_timezone FROM worker_processing_settings p WHERE p.worker_id=n.id AND p.schedule_enabled),
            (SELECT p.schedule_slots FROM worker_processing_settings p WHERE p.worker_id=n.id AND p.schedule_enabled),n.inventory
          FROM workers n WHERE n.deleted_at IS NULL AND n.accepts_work AND EXISTS(SELECT 1 FROM json_each(n.roles) WHERE value = 'worker')
            AND (@fresh OR NOT n.draining AND NOT n.quarantined) AND n.incompatible_reason IS NULL
            AND n.last_seen_at>(strftime('%Y-%m-%d %H:%M:%f', 'now', @offlineAgo) || '000Z')
          """,c))
        {
            cmd.Parameters.AddWithValue("offlineAgo", -offline); cmd.Parameters.AddWithValue("fresh",fresh);
            await using var r=await cmd.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) candidates.Add((r.GetGuid(0),JsonSerializer.Deserialize<WorkerToolCapability[]>(r.GetString(1),Json)??[],r.GetInt32(2),r.GetBoolean(3),
                r.IsDBNull(4)?null:r.GetString(4),r.IsDBNull(5)?null:r.GetString(5),r.IsDBNull(6)?null:JsonSerializer.Deserialize<WorkerInventory>(r.GetString(6),Json)));
        }
        // Aucun worker déclaré : le central n'est pas encore configuré, ce n'est pas une impossibilité du flow.
        if(fresh && candidates.Count==0) return true;
        if(item.Plan?.Strategy==FlowPlacementPlan.StrategyImpossible) return false;
        var entry=checkpoint?.NextNodeId??flow.Nodes.Single(node=>node.Component.PluginId=="base" && node.Component.ComponentId.StartsWith("base-input-",StringComparison.Ordinal)).Id;
        var needsWrite=eligibility!.RequiresWriteAccess(flow,entry,checkpoint is not null);
        foreach(var candidate in candidates)
        {
            var plannedTarget=item.Plan?.WorkerAssignments?.TryGetValue(entry,out var workerTarget)==true?workerTarget:item.Plan?.AssignedWorkerId;
            if(plannedTarget is not null && candidate.Id!=plannedTarget.Value) continue;
            if(candidate.Id==item.Previous || candidate.Capacity<=0) continue;
            var identity=await workerGroups!.GetWorkerIdentityAsync(candidate.Id,ct);
            if(!selector.Accepts(candidate.Id,item.Previous??Guid.Empty,identity)) continue;
            if(!fresh && candidate.ScheduleEnabled && candidate.Timezone is not null && candidate.Slots is not null
                && !new WorkerSchedule(true,candidate.Timezone,candidate.Slots).IsOpen(DateTimeOffset.UtcNow)) continue;
            if(!eligibility.CanRun(flow,candidate.Capabilities,candidate.Id,entry,checkpoint is not null,checkpoint?.Context,identity,candidate.Inventory,item.Plan?.WorkerAssignments)) continue;
            // Un worker occupé (baux actifs au maximum de sa capacité) reste un destinataire valide : il prend la tâche en file dès qu'il se libère.
            await using var c=await dataSource.OpenConnectionAsync(ct);
            await using(var access=Sql.Command("""
              SELECT EXISTS(
                SELECT 1 FROM library_worker_accesses a JOIN workers n ON n.id=a.worker_id
                LEFT JOIN library_worker_accesses s ON s.library_id=a.library_id AND s.worker_id=a.transfer_source_worker_id
                LEFT JOIN workers sn ON sn.id=s.worker_id
                WHERE a.library_id=@library AND a.worker_id=@worker AND a.is_enabled AND a.can_read
                  AND ((a.mode='direct' AND a.local_path IS NOT NULL AND (NOT @write OR a.can_write))
                    OR (a.mode='transfer' AND s.mode='direct' AND s.is_enabled AND s.can_read AND (NOT @write OR s.can_write)
                        AND sn.deleted_at IS NULL AND sn.last_seen_at>(strftime('%Y-%m-%d %H:%M:%f', 'now', @offlineAgo) || '000Z')))
                  AND n.deleted_at IS NULL AND n.last_seen_at>(strftime('%Y-%m-%d %H:%M:%f', 'now', @offlineAgo) || '000Z'))
              """,c))
            {
                access.Parameters.AddWithValue("library",item.Library);access.Parameters.AddWithValue("worker",candidate.Id);
                access.Parameters.AddWithValue("write",needsWrite);access.Parameters.AddWithValue("offlineAgo", -offline);
                if(!await access.ScalarBoolAsync(ct)) continue;
            }
            if(item.Source is {} originalSource)
            {
                await using var original=Sql.Command("""
                  SELECT EXISTS(SELECT 1 FROM library_worker_accesses a JOIN workers n ON n.id=a.worker_id
                    WHERE a.library_id=@library AND a.worker_id=@source AND a.mode='direct' AND a.is_enabled AND a.can_read
                      AND (NOT @write OR a.can_write) AND a.local_path IS NOT NULL AND n.deleted_at IS NULL
                      AND n.last_seen_at>(strftime('%Y-%m-%d %H:%M:%f', 'now', @offlineAgo) || '000Z'))
                  """,c);
                original.Parameters.AddWithValue("library",item.Library);original.Parameters.AddWithValue("source",originalSource);
                original.Parameters.AddWithValue("write",needsWrite);original.Parameters.AddWithValue("offlineAgo", -offline);
                if(!await original.ScalarBoolAsync(ct)) continue;
            }
            return true;
        }
        return false;
    }
}
