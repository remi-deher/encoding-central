using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Api.Workers;
using Encoding.Api.Plugins;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Workers;

namespace Encoding.Api.Executions;

public sealed partial class RuntimeRepository
{
    public async Task<bool> PrepareSourceAsync(Guid id,Guid worker,long epoch,CancellationToken ct)
    {
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        if(!await FenceAsync(id,worker,epoch,c,tx,ct)) return false;
        await using var command=Sql.Command("UPDATE execution_work SET source_preparation_requested=true WHERE execution_id=@id",c,tx);
        command.Parameters.AddWithValue("id",id); await command.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct); return true;
    }

    internal static async Task<bool> FenceAsync(Guid id,Guid actor,long epoch,DbConnection c,DbTransaction tx,CancellationToken ct,bool source=false)
    {
        await using var command=Sql.Command($"""
          SELECT w.execution_id FROM execution_work w JOIN executions e ON e.id=w.execution_id
          JOIN media_reservations m ON m.execution_id=w.execution_id AND m.lease_epoch=w.lease_epoch
          WHERE w.execution_id=@id AND w.lease_epoch=@epoch AND w.lease_until>(strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z') AND m.lease_until>(strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z')
          AND (CASE WHEN @source THEN w.source_worker_id ELSE w.worker_id END)=@actor AND e.state IN ('running','cancelling')
          """,c,tx);
        command.Parameters.AddWithValue("id",id); command.Parameters.AddWithValue("actor",actor); command.Parameters.AddWithValue("epoch",epoch); command.Parameters.AddWithValue("source",source);
        return await command.ExecuteScalarAsync(ct) is not null;
    }
    internal static async Task ReleaseAsync(Guid id,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        await using var cmd=Sql.Command("DELETE FROM media_reservations WHERE execution_id=@id; DELETE FROM runtime_source_write_waits WHERE execution_id=@id; DELETE FROM disk_locks WHERE execution_id=@id; UPDATE execution_work SET lease_until=NULL,handoff_pending=false WHERE execution_id=@id",
            c,tx);
        cmd.Parameters.AddWithValue("id",id); await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<RuntimeLeaseStatus?> RenewAsync(Guid id,Guid worker,long epoch,CancellationToken ct, IReadOnlyList<RuntimeStepCommand>? commands = null)
    {
        using var priority = Encoding.Api.Infrastructure.Data.Sqlite.SqliteWriter.Prioritize();
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        if(!await FenceAsync(id,worker,epoch,c,tx,ct)) return null;
        if (!await RuntimeCommandJournal.StoreAsync(id, commands, c, tx, ct)) return null;
        await using var update=Sql.Command("""
          UPDATE execution_work SET lease_until=(strftime('%Y-%m-%d %H:%M:%f', 'now', '+180 seconds') || '000Z') WHERE execution_id=@id;
          UPDATE media_reservations SET lease_until=(strftime('%Y-%m-%d %H:%M:%f', 'now', '+180 seconds') || '000Z') WHERE execution_id=@id;
          UPDATE disk_locks SET lease_until=(strftime('%Y-%m-%d %H:%M:%f', 'now', '+180 seconds') || '000Z') WHERE execution_id=@id;
          SELECT e.state='cancelling',e.revision,w.lease_until FROM executions e JOIN execution_work w ON w.execution_id=e.id WHERE e.id=@id
          """,c,tx);
        update.Parameters.AddWithValue("id",id); await using var r=await update.ExecuteReaderAsync(ct); await r.ReadAsync(ct);
        var result=new RuntimeLeaseStatus(r.GetBoolean(0),r.GetInt32(1),r.GetFieldValue<DateTimeOffset>(2)); await r.DisposeAsync(); await tx.CommitAsync(ct); return result;
    }

    public async Task<bool> ReportSourceOperationsAsync(Guid id,Guid source,RuntimeFence request,CancellationToken ct)
    {
        using var priority=Encoding.Api.Infrastructure.Data.Sqlite.SqliteWriter.Prioritize();
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        if(!await FenceAsync(id,source,request.Epoch,c,tx,ct,true)) return false;
        if(!await RuntimeCommandJournal.StoreAsync(id,request.Commands,c,tx,ct,allowCompletedOperations:true)) return false;
        await tx.CommitAsync(ct); return true;
    }

    /// <summary>Publie les métadonnées d'origine dès le sondage ; ne remplace jamais une valeur déjà enregistrée.</summary>
    public async Task<FlowPlacementPlan?> RecordSourceMetadataAsync(Guid id,Guid worker,long epoch,JsonElement metadata,CancellationToken ct)
    {
        using var priority = Encoding.Api.Infrastructure.Data.Sqlite.SqliteWriter.Prioritize();
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        if(!await FenceAsync(id,worker,epoch,c,tx,ct)) return null;
        bool firstMetadata;
        await using(var previous=Sql.Command("SELECT before_metadata IS NULL FROM execution_work WHERE execution_id=@id",c,tx))
        { previous.Parameters.AddWithValue("id",id); firstMetadata=await previous.ScalarBoolAsync(ct); }
        await using(var update=Sql.Command("UPDATE execution_work SET before_metadata=COALESCE(before_metadata,@before) WHERE execution_id=@id",c,tx))
        {
            update.Parameters.AddWithValue("id",id); update.Parameters.AddWithValue("before",SqlKind.Json,metadata.GetRawText());
            await update.ExecuteNonQueryAsync(ct);
        }
        FlowDefinition? flow=null; Guid libraryId=Guid.Empty; FlowPlacementPlan? plan=null;
        await using(var read=Sql.Command("SELECT e.flow_definition,w.library_id,w.placement_plan FROM executions e JOIN execution_work w ON w.execution_id=e.id WHERE e.id=@id",c,tx))
        {
            read.Parameters.AddWithValue("id",id); await using var row=await read.ExecuteReaderAsync(ct);
            if(!await row.ReadAsync(ct)) return null;
            flow=JsonSerializer.Deserialize<FlowDefinition>(row.GetString(0),Json); libraryId=row.GetGuid(1);
            if(!row.IsDBNull(2)) plan=JsonSerializer.Deserialize<FlowPlacementPlan>(row.GetString(2),Json);
        }
        if(flow is not null && (plan is null || firstMetadata))
        {
            var candidates=await LoadPlacementCandidatesAsync(libraryId,c,tx,ct,id);
            plan=planner.Plan(flow,metadata,candidates,DateTimeOffset.UtcNow);
            await using var save=Sql.Command("UPDATE execution_work SET placement_plan=@plan WHERE execution_id=@id",c,tx);
            save.Parameters.AddWithValue("id",id); save.Parameters.AddWithValue("plan",SqlKind.Json,JsonSerializer.Serialize(plan,Json));
            await save.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct); return plan;
    }

    public async Task<FlowPlacementPlan?> PersistPlacementPlanAsync(Guid id,Guid worker,long epoch,CancellationToken ct)
    {
        using var priority=Encoding.Api.Infrastructure.Data.Sqlite.SqliteWriter.Prioritize();
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        if(!await FenceAsync(id,worker,epoch,c,tx,ct)) return null;
        FlowDefinition? flow=null; Guid libraryId=Guid.Empty; FlowPlacementPlan? plan=null; JsonElement? metadata=null;
        await using(var read=Sql.Command("SELECT e.flow_definition,w.library_id,w.placement_plan,w.before_metadata FROM executions e JOIN execution_work w ON w.execution_id=e.id WHERE e.id=@id",
            c,tx))
        {
            read.Parameters.AddWithValue("id",id); await using var row=await read.ExecuteReaderAsync(ct);
            if(!await row.ReadAsync(ct)) return null;
            flow=JsonSerializer.Deserialize<FlowDefinition>(row.GetString(0),Json); libraryId=row.GetGuid(1);
            if(!row.IsDBNull(2)) plan=JsonSerializer.Deserialize<FlowPlacementPlan>(row.GetString(2),Json);
            if(!row.IsDBNull(3)) metadata=JsonSerializer.Deserialize<JsonElement>(row.GetString(3));
        }
        if(plan is null && flow is not null)
        {
            var candidates=await LoadPlacementCandidatesAsync(libraryId,c,tx,ct,id);
            plan=planner.Plan(flow,metadata,candidates,DateTimeOffset.UtcNow);
            await using var save=Sql.Command("UPDATE execution_work SET placement_plan=@plan WHERE execution_id=@id AND placement_plan IS NULL",c,tx);
            save.Parameters.AddWithValue("id",id); save.Parameters.AddWithValue("plan",SqlKind.Json,JsonSerializer.Serialize(plan,Json));
            await save.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct); return plan;
    }

    public async Task<bool> AuthorizeSourceWriteAsync(Guid id,Guid worker,long epoch,string root,CancellationToken ct)
    {
        await using(var check=dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM execution_work w JOIN executions e ON e.id=w.execution_id WHERE w.execution_id=@id AND w.worker_id=@worker AND w.source_worker_id=@worker AND w.lease_epoch=@epoch AND w.lease_until>(strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z') AND e.state='running' AND EXISTS(SELECT 1 FROM library_worker_accesses a WHERE a.library_id=w.library_id AND a.worker_id=@worker AND a.is_enabled AND a.can_write AND a.mode='direct' AND a.local_path=@root))"))
        {
            check.Parameters.AddWithValue("id",id);check.Parameters.AddWithValue("worker",worker);check.Parameters.AddWithValue("epoch",epoch);check.Parameters.AddWithValue("root",root);
            if(!await check.ScalarBoolAsync(ct)) return false;
        }
        await using var c=await dataSource.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        if(!await FenceAsync(id,worker,epoch,c,tx,ct)) return false;
        if(sourceWrites is not null && !await sourceWrites.NodeCanWriteAsync(worker,c,tx,ct)) return false;
        await using var step=Sql.Command("SELECT s.flow_node_id FROM execution_steps s JOIN executions e ON e.id=s.execution_id JOIN execution_work w ON w.execution_id=e.id WHERE s.execution_id=@id AND s.completed_at IS NULL AND w.source_worker_id=@worker AND EXISTS(SELECT 1 FROM library_worker_accesses a WHERE a.library_id=w.library_id AND a.worker_id=@worker AND a.is_enabled AND a.can_write AND a.mode='direct' AND a.local_path=@root) AND EXISTS(SELECT 1 FROM json_each(e.flow_definition,'$.nodes') n WHERE json_extract(n.value,'$.id')=s.flow_node_id AND json_extract(n.value,'$.component.componentId')='mkv-edit-in-place')",
            c,tx);
        step.Parameters.AddWithValue("id",id);step.Parameters.AddWithValue("worker",worker);step.Parameters.AddWithValue("root",root);var node=await step.ExecuteScalarAsync(ct) as string;
        if(node is null) return false;
        await tx.CommitAsync(ct);return true;
    }

    public async Task<StartedRuntimeStep?> StartStepAsync(Guid id,Guid worker,StartRuntimeStep request,CancellationToken ct) =>
        (await TryStartStepAsync(id,worker,request,ct)).Step;

    /// <summary>Démarre l'étape ; en cas de refus, <c>Refusal</c> porte le motif (déjà journalisé).</summary>
    public async Task<(StartedRuntimeStep? Step,StepRefusal? Refusal)> TryStartStepAsync(Guid id,Guid worker,StartRuntimeStep request,CancellationToken ct)
    {
        (StartedRuntimeStep?,StepRefusal?) Fail(string code)=>(null,Refuse(code,"steps/start",id,worker,request.NodeId,request.Epoch));
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        if(!await FenceAsync(id,worker,request.Epoch,c,tx,ct)) return Fail(await DiagnoseFenceAsync(id,worker,request.Epoch,c,tx,ct));
        var flow=await LoadRunningFlowAsync(id,c,tx,ct);
        if(flow is null) return Fail(StepRefusalCodes.ExecutionNotRunning);
        var node=flow.Nodes.SingleOrDefault(n=>n.Id==request.NodeId); if(node is null) return Fail(StepRefusalCodes.NodeUnknown);
        await using(var prior=Sql.Command("SELECT checkpoint FROM execution_steps WHERE execution_id=@id AND completed_at IS NOT NULL ORDER BY completed_revision DESC LIMIT 1",c,tx))
        {
            prior.Parameters.AddWithValue("id",id); var json=await prior.ExecuteScalarAsync(ct) as string;
            var expected=json is null ? flow.Nodes.Single(n=>components.Resolve(n.Component)!.Kind=="input").Id : JsonSerializer.Deserialize<RuntimeCheckpoint>(json,Json)!.NextNodeId;
            if(expected!=node.Id) return Fail(StepRefusalCodes.StepOutOfOrder);
        }
        await using var start=Sql.Command("""
          INSERT INTO execution_steps(execution_id,flow_node_id,invocation_id,idempotent) VALUES(@id,@node,@invocation,@idempotent) ON CONFLICT DO NOTHING;
          SELECT invocation_id,checkpoint FROM execution_steps WHERE execution_id=@id AND flow_node_id=@node
          """,c,tx);
        start.Parameters.AddWithValue("id",id); start.Parameters.AddWithValue("node",request.NodeId); start.Parameters.AddWithValue("invocation",Guid.NewGuid());
        start.Parameters.AddWithValue("idempotent",RuntimeStepEffects.IsReplayable(node.Component.PluginId,node.Component.ComponentId));
        await using var r=await start.ExecuteReaderAsync(ct); await r.ReadAsync(ct);
        var result=new StartedRuntimeStep(r.GetGuid(0),r.IsDBNull(1)?null:JsonSerializer.Deserialize<RuntimeCheckpoint>(r.GetString(1),Json));
        await r.DisposeAsync(); await tx.CommitAsync(ct); return (result,null);
    }

    internal static bool ValidCommandLog(RuntimeCommandLog command) =>
        (command.ProgressPercent is null or >= 0 and <= 100) && command.Id!=Guid.Empty && command.Command is not null && command.Command.Length<=16384
        && command.StandardOutput is not null && command.StandardOutput.Length<=32768
        && command.StandardError is not null && command.StandardError.Length<=32768
        && (command.Kind is null or "command" or "operation") && (command.BytesProcessed is null or >= 0)
        && (command.FinishedAt is null || command.FinishedAt>=command.StartedAt);
    public async Task<RuntimeSourceState> SourceAsync(Guid id,CancellationToken ct)
    {
        await using var cmd=dataSource.CreateCommand("SELECT source_sha FROM execution_work WHERE execution_id=@id");cmd.Parameters.AddWithValue("id",id);
        var source=await cmd.ExecuteScalarAsync(ct) as string;
        await using var inputs=dataSource.CreateCommand("SELECT input_path,sha256 FROM execution_inputs WHERE execution_id=@id");
        inputs.Parameters.AddWithValue("id",id); await using var r=await inputs.ExecuteReaderAsync(ct);
        var map=new Dictionary<string,string>(); while(await r.ReadAsync(ct)) map.Add(r.GetString(0),r.GetString(1));
        return new(source,map);
    }

    private async Task<bool> ValidVideoEncodingsAsync(FlowDefinition flow,Guid executionId,Guid worker,string currentNodeId,
        IReadOnlyList<ResolvedVideoEncoding>? resolutions,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        foreach(var resolution in resolutions??[])
        {
            Guid? executionWorker;
            var currentNode=flow.Nodes.SingleOrDefault(item=>item.Id==currentNodeId);
            if(resolution.FlowNodeId==currentNodeId || currentNode?.Component.PluginId=="base"
                && currentNode.Component.ComponentId is "base-execute-processing" or FfmpegBuilderPolicy.Execute) executionWorker=worker;
            else
            {
                await using var owner=Sql.Command("SELECT completed_worker_id FROM execution_steps WHERE execution_id=@id AND flow_node_id=@node AND completed_at IS NOT NULL",c,tx);
                owner.Parameters.AddWithValue("id",executionId); owner.Parameters.AddWithValue("node",resolution.FlowNodeId);
                executionWorker=await owner.ScalarGuidAsync(ct) is {} workerId?workerId:null;
                if(executionWorker!=resolution.WorkerId)
                {
                    await using var plannedOwner=Sql.Command("SELECT completed_worker_id FROM execution_steps WHERE execution_id=@id AND completed_at IS NOT NULL AND EXISTS(SELECT 1 FROM json_each(checkpoint->'context'->'videoEncodings') stored, json_each(@resolution) wanted WHERE json(stored.value)=json(wanted.value)) ORDER BY completed_revision DESC LIMIT 1",
                        c,tx);
                    plannedOwner.Parameters.AddWithValue("id",executionId); plannedOwner.Parameters.AddWithValue("resolution",SqlKind.Json,JsonSerializer.Serialize(new[]{resolution},Json));
                    executionWorker=await plannedOwner.ScalarGuidAsync(ct) is {} plannedWorkerId?plannedWorkerId:null;
                }
            }
            if(WorkerClause(resolution,executionWorker) is {} workerClause) return RefuseVideoEncoding(workerClause,resolution,executionId,worker,currentNodeId);
            var node=flow.Nodes.SingleOrDefault(item=>item.Id==resolution.FlowNodeId);
            if(NodeClause(node) is {} nodeClause) return RefuseVideoEncoding(nodeClause,resolution,executionId,worker,currentNodeId);
            var profileValue=node!.Configuration["encodingProfile"];
            var profile=profileValue.Deserialize<EncodingProfileSnapshot>(Json);
            var codec=node.Configuration.TryGetValue("videoCodec",out var codecValue)?codecValue.GetString():"hevc";
            var retryWithCpu=node.Configuration.TryGetValue("retryWithCpu",out var retryFlag)&&retryFlag.ValueKind==JsonValueKind.True;
            var encoder=VideoEncoderCatalog.Find(resolution.EncoderId);
            var target=profile is null||string.IsNullOrEmpty(codec)?null:MediaCodecPolicy.AcceptedEncodingTarget(profile,codec,resolution.EncoderId,retryWithCpu);
            var preset=encoder?.Presets.FirstOrDefault(item=>item.Id==target?.Preset)?.Value;
            if(FieldClause(resolution,profile,encoder,target,codec,preset) is {} fieldClause) return RefuseVideoEncoding(fieldClause,resolution,executionId,worker,currentNodeId);
        }
        return true;
    }

    private bool RefuseVideoEncoding(string clause,ResolvedVideoEncoding resolution,Guid executionId,Guid worker,string currentNodeId)
    {
        logger?.LogWarning("Encodage vidéo refusé, clause {Clause} — FlowNodeId {FlowNodeId}, EncoderId {EncoderId}, exécution {ExecutionId}, nœud courant {NodeId}, worker {WorkerId}",
            clause,resolution.FlowNodeId,resolution.EncoderId,executionId,currentNodeId,worker);
        return false;
    }

    /// <summary>Clause « worker », « version_ffmpeg » ou « resolved_at » qui échoue (diagnostic seul, mêmes tests et même ordre que la validation).</summary>
    internal static string? WorkerClause(ResolvedVideoEncoding resolution,Guid? executionWorker)
    {
        if(executionWorker!=resolution.WorkerId) return $"worker : attendu {executionWorker?.ToString()??"aucun"}, reçu {resolution.WorkerId}";
        if(string.IsNullOrWhiteSpace(resolution.FfmpegVersion)) return "version_ffmpeg : vide";
        if(resolution.FfmpegVersion.Length>128) return $"version_ffmpeg : longueur {resolution.FfmpegVersion.Length} > 128";
        return resolution.ResolvedAt==default?"resolved_at : valeur par défaut":null;
    }

    /// <summary>Clause « node » (nœud inconnu, mauvais plugin ou composant) ou « profile » (encodingProfile absent ou non objet) qui échoue.</summary>
    internal static string? NodeClause(FlowNodeDefinition? node)
    {
        if(node is null) return "node : nœud introuvable dans le flow";
        if(node.Component.PluginId!="base" && node.Component.PluginId!="video") return $"node : plugin {node.Component.PluginId} (attendu base ou video)";
        if(node.Component.ComponentId!="base-transcode-video" && node.Component.ComponentId!="video-transcode")
            return $"node : composant {node.Component.ComponentId} (attendu base-transcode-video ou video-transcode)";
        if(!node.Configuration.TryGetValue("encodingProfile",out var profileValue)) return "profile : encodingProfile absent de la configuration du nœud";
        return profileValue.ValueKind!=JsonValueKind.Object?$"profile : encodingProfile de type {profileValue.ValueKind} (attendu Object)":null;
    }

    /// <summary>Clause « profile », « encoder », « target » ou champ précis (attendu / reçu) qui échoue.</summary>
    internal static string? FieldClause(ResolvedVideoEncoding r,EncodingProfileSnapshot? profile,VideoEncoderDescriptor? encoder,
        EncodingProfileTarget? target,string? codec,string? preset)
    {
        if(profile is null) return "profile : encodingProfile non désérialisable";
        if(encoder is null) return $"encoder : {r.EncoderId} absent du catalogue";
        if(target is null) return $"target : aucune cible acceptée pour le profil {profile.Name}, codec {codec}, encodeur {r.EncoderId}";
        if(r.Codec!=codec) return $"codec : attendu {codec}, reçu {r.Codec}";
        if(encoder.Codec!=codec) return $"codec_encodeur : attendu {codec}, encodeur {encoder.Id} fournit {encoder.Codec}";
        if(r.ProfileId!=profile.Id) return $"profile_id : attendu {profile.Id}, reçu {r.ProfileId}";
        if(r.ProfileVersion!=profile.Version) return $"profile_version : attendu {profile.Version}, reçu {r.ProfileVersion}";
        if(r.ProfileName!=profile.Name) return $"profile_name : attendu {profile.Name}, reçu {r.ProfileName}";
        if(r.Acceleration!=encoder.Acceleration) return $"acceleration : attendu {encoder.Acceleration}, reçu {r.Acceleration}";
        if(r.QualityMode!=encoder.QualityMode) return $"quality_mode : attendu {encoder.QualityMode}, reçu {r.QualityMode}";
        if(r.QualityOption!=encoder.QualityOption) return $"quality_option : attendu {encoder.QualityOption}, reçu {r.QualityOption}";
        if(r.Quality!=target.Quality) return $"quality : attendu {target.Quality}, reçu {r.Quality}";
        if(r.PresetId!=target.Preset) return $"preset_id : attendu {target.Preset}, reçu {r.PresetId}";
        if(r.Priority!=target.Priority) return $"priority : attendu {target.Priority}, reçu {r.Priority}";
        var presetOption=string.IsNullOrEmpty(encoder.PresetOption)?null:encoder.PresetOption;
        if(r.PresetOption!=presetOption) return $"preset_option : attendu {presetOption??"null"}, reçu {r.PresetOption??"null"}";
        return r.PresetValue!=preset?$"preset_value : attendu {preset??"null"}, reçu {r.PresetValue??"null"}":null;
    }

    /// <summary>Lecture seule : un worker à accès direct en ligne, autre que celui qui réclame, peut-il exécuter ce segment ?</summary>
    private async Task<bool> CapableDirectWorkerExistsAsync(Guid library,bool write,
        (Guid Id,Guid Library,string Path,string Key,FlowDefinition Flow,int Attempt,int Revision,long Epoch,RuntimeCheckpoint? Checkpoint,
            RuntimeHandoffSelector? Selector,Guid? PreviousWorker,Guid? Source,byte[]? Settings,RuntimeWaitProbe? Probe,DateTimeOffset QueuedAt,
            FlowPlacementPlan? PlacementPlan,JsonElement? BeforeMetadata) item,
        Guid claimant,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        var direct=new List<(Guid Id,IReadOnlyList<WorkerToolCapability> Capabilities,WorkerInventory? Inventory)>();
        await using(var read=Sql.Command("""
          SELECT dn.id,dn.capabilities,dn.inventory FROM library_worker_accesses da JOIN workers dn ON dn.id=da.worker_id
          WHERE da.library_id=@library AND da.mode='direct' AND da.is_enabled AND da.can_read AND da.local_path IS NOT NULL AND (NOT @write OR da.can_write)
            AND dn.id<>@claimant AND dn.id IS DISTINCT FROM @previous AND dn.deleted_at IS NULL AND dn.last_seen_at>(strftime('%Y-%m-%d %H:%M:%f', 'now', @offlineAgo) || '000Z') AND dn.accepts_work AND EXISTS(SELECT 1 FROM json_each(dn.roles) WHERE value = 'worker')
            -- Un worker direct saturé ne retient pas le travail : sa file ne doit pas laisser les autres workers inactifs.
            AND (SELECT count(*) FROM execution_work w JOIN executions e ON e.id=w.execution_id WHERE w.worker_id=dn.id AND w.lease_until>(strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z') AND e.state IN ('running','cancelling'))
                <COALESCE((SELECT p.max_concurrent_tasks FROM worker_processing_settings p WHERE p.worker_id=dn.id),dn.max_concurrent_tasks)
          """,c,tx))
        {
            read.Parameters.AddWithValue("library",library); read.Parameters.AddWithValue("write",write);
            read.Parameters.AddWithValue("claimant",claimant); read.Parameters.AddWithValue("previous",
                item.PreviousWorker is {} previous?previous:DBNull.Value); read.Parameters.AddWithValue("offlineAgo", -presence.OfflineAfter);
            await using var r=await read.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
                direct.Add((r.GetGuid(0),JsonSerializer.Deserialize<WorkerToolCapability[]>(r.GetString(1),Json)??[],r.IsDBNull(2)?null:JsonSerializer.Deserialize<WorkerInventory>(r.GetString(2),Json)));
        }
        foreach(var node in direct)
        {
            var identity=await workerGroups.GetWorkerIdentityAsync(node.Id,ct);
            if(identity is not null
                && eligibility.CanRun(item.Flow,node.Capabilities,node.Id,item.Checkpoint?.NextNodeId,item.Checkpoint is not null,item.Checkpoint?.Context,
                    identity,node.Inventory,item.PlacementPlan?.WorkerAssignments)) return true;
        }
        return false;
    }
}
