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

    /// <summary>Reprises automatiques d'une même exécution après perte du worker (ADR 0004).</summary>
    public const int MaximumResumes=2;
    private static readonly TimeSpan ResumeGrace=TimeSpan.FromMinutes(2);
    /// <summary>Délai avant de déclarer qu'aucun worker du central ne peut jamais exécuter une exécution neuve (ADR 0024).</summary>
    private static readonly TimeSpan FreshPlacementGrace=TimeSpan.FromMinutes(2);

    private static readonly string ExpiredWorkPredicate = $"""
        ((e.state='cancelling' AND (w.lease_until IS NULL OR w.lease_until<=(strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z')))
        OR (e.state='running' AND w.worker_id IS NOT NULL AND w.lease_until IS NOT NULL AND w.lease_until<=(strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z')))
        """;

    private async Task<bool> HasExpiredWorkAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = Sql.Command($"SELECT EXISTS(SELECT 1 FROM executions e JOIN execution_work w ON w.execution_id=e.id WHERE {ExpiredWorkPredicate})", connection);
        return await command.ScalarBoolAsync(ct);
    }

    /// <summary>
    /// Closes cancelled work. An attempt whose assigned worker lost its lease is resumed at its last durable checkpoint by another
    /// eligible worker (same execution ID, bounded) when every unfinished step is replayable; otherwise it fails and must be retried
    /// as a new execution. An explicit relay or wait (worker_id = NULL) is claimed again under the same execution ID.
    /// </summary>
    public async Task<int> ProcessOnceAsync(CancellationToken ct)
    {
        // This is only an admission hint: all predicates are rechecked inside the writer transaction.
        if (!await HasExpiredWorkAsync(ct) && !await RuntimeRepository.HasUnavailableSourcesAsync(dataSource, offlineAfter, ct))
            return await FailUnavailableTargetsAsync(ct);
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        await RuntimeRepository.FailUnavailableSourcesAsync(c, tx, offlineAfter, ct);
        var expired=new List<(Guid Id,Guid? Worker,int Revision,string State,int Attempt,string Flow,bool HasNonIdempotentStep,bool Uncertain,bool Waiting,int Resumes)>();
        await using(var cmd=Sql.Command($"""
          SELECT e.id,e.worker_id,e.revision,e.state,e.attempt,e.flow_definition,
            EXISTS(SELECT 1 FROM execution_steps s WHERE s.execution_id=e.id AND NOT s.idempotent),
            EXISTS(SELECT 1 FROM execution_steps s WHERE s.execution_id=e.id AND s.completed_at IS NULL AND NOT s.idempotent),
            EXISTS(SELECT 1 FROM execution_waits x WHERE x.execution_id=e.id AND x.state='waiting'),w.resume_count
          FROM executions e JOIN execution_work w ON w.execution_id=e.id
          WHERE {ExpiredWorkPredicate}
          ORDER BY e.updated_at LIMIT 100
          """,c,tx))
        await using(var r=await cmd.ExecuteReaderAsync(ct))
        { while(await r.ReadAsync(ct)) expired.Add((r.GetGuid(0),r.IsDBNull(1)?null:r.GetGuid(1),r.GetInt32(2),r.GetString(3),r.GetInt32(4),r.GetString(5),
            r.GetBoolean(6),r.GetBoolean(7),r.GetBoolean(8),r.GetInt32(9))); }
        foreach(var item in expired)
        {
            var cancelled=item.State=="cancelling";
            if(!cancelled && await TryResumeAsync(item.Id,item.Worker,item.Revision,item.Attempt,item.Flow,item.Uncertain,item.Waiting,item.Resumes,c,tx,ct)) continue;
            var detail=cancelled
                ? "Annulation terminée après expiration du bail ; la source est conservée."
                : item.Uncertain
                    ? "Le worker a perdu sa connexion pendant une étape à effet externe. Le résultat est incertain ; la tâche est en erreur et nécessite une relance explicite."
                : item.Resumes>=MaximumResumes
                    ? $"Le worker a perdu sa connexion et la tâche a déjà été reprise {MaximumResumes} fois ; elle est en erreur et nécessite une relance explicite."
                : "Le worker a perdu sa connexion pendant cette tentative. La tâche est en erreur ; une relance doit créer une nouvelle tentative.";
            if(await ExecutionRepository.RecordInTransactionAsync(item.Id,new(Guid.NewGuid(),item.Revision,cancelled?"cancelled":"failed",item.Worker,null,detail),c,tx,ct))
            {
                await RuntimeRepository.ReleaseAsync(item.Id,c,tx,ct);
                if(!cancelled && !item.HasNonIdempotentStep && item.Worker is {} previous)
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

    /// <summary>
    /// Rend l'exécution à la file des relais : même identité, époque suivante, dernier checkpoint durable. Le worker suivant est choisi
    /// par les règles d'attribution habituelles (capacités, accès au média, limites, charge). Refusée si une étape à effet externe est
    /// inachevée, si une attente est en cours, si la borne est atteinte ou si le flow a sa propre règle de relance.
    /// </summary>
    private async Task<bool> TryResumeAsync(Guid id,Guid? worker,int revision,int attempt,string flowJson,bool uncertain,bool waiting,int resumes,
        System.Data.Common.DbConnection c,System.Data.Common.DbTransaction tx,CancellationToken ct)
    {
        if(worker is null || uncertain || waiting || resumes>=MaximumResumes) return false;
        try
        {
            var flow=JsonSerializer.Deserialize<FlowDefinition>(flowJson,Json);
            if(flow is null || RuntimeRetryPolicy.Resolve(flow) is not null) return false;
        }
        catch(ArgumentException) { return false; }
        var detail=$"Le worker a perdu sa connexion ; reprise {resumes+1}/{MaximumResumes} depuis le dernier checkpoint par un autre worker éligible.";
        if(!await ExecutionRepository.RecordInTransactionAsync(id,new(Guid.NewGuid(),revision,"running",worker,null,detail),c,tx,ct)) return false;
        // La destination du dernier relais (worker, groupe, étiquettes) reste la règle de la reprise : un encodage confié à .51 ne reprend pas ailleurs.
        RuntimeHandoffSelector? previousSelector=null;
        await using(var read=Sql.Command("SELECT handoff_selector FROM execution_work WHERE execution_id=@id",c,tx))
        {
            read.Parameters.AddWithValue("id",id);
            if(await read.ExecuteScalarAsync(ct) is string stored) previousSelector=JsonSerializer.Deserialize<RuntimeHandoffSelector>(stored,Json);
        }
        var resumeSelector=(previousSelector??new RuntimeHandoffSelector("eligible")) with { Resumed=true };
        await using var yield=Sql.Command("""
          UPDATE execution_work SET handoff_pending=true,handoff_selector=@selector,previous_worker_id=@worker,
            worker_id=NULL,lease_until=NULL,lease_epoch=lease_epoch+1,resume_count=resume_count+1 WHERE execution_id=@id;
          UPDATE media_reservations SET lease_epoch=(SELECT lease_epoch FROM execution_work WHERE execution_id=@id),lease_until='9999-12-31 23:59:59.999999Z' WHERE execution_id=@id;
          DELETE FROM disk_locks WHERE execution_id=@id;
          """,c,tx);
        yield.Parameters.AddWithValue("id",id); yield.Parameters.AddWithValue("worker",worker.Value);
        yield.Parameters.AddWithValue("selector",SqlKind.Json,JsonSerializer.Serialize(resumeSelector,Json));
        await yield.ExecuteNonQueryAsync(ct);
        logger.LogInformation("L’exécution {ExecutionId} est reprise au dernier checkpoint après perte du worker {WorkerId} (reprise {Resume}/{Maximum}).",id,worker,resumes+1,MaximumResumes);
        return true;
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
            // Un worker qui redémarre doit pouvoir revenir avant qu'une reprise soit déclarée impossible.
            if(selector.IsResume && DateTimeOffset.UtcNow-item.Updated<ResumeGrace) continue;
            if(await HasEligibleWorkerAsync(item,flow,selector,checkpoint,fresh,ct)) continue;

            await using var c=await dataSource.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
            var detail=fresh?"placement_impossible: "+(item.Plan?.Reason??"Aucun worker du central ne peut exécuter ce flow (capacité absente, désignation de worker impossible ou aucun accès à la bibliothèque)."):
                selector.IsResume?"Aucun worker éligible n’a pu reprendre cette tâche après la perte de son worker (capacités, accès au média ou limites) ; l’exécution échoue.":"Aucun worker en ligne ne correspond à la destination de ce relais ou de cette relance ; l’exécution échoue.";
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
            if(candidate.Id==item.Previous && !selector.IsResume || candidate.Capacity<=0) continue;
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
