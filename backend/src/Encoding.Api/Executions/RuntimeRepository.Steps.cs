using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Api.Nodes;
using Encoding.Api.Plugins;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Nodes;

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
          WHERE w.execution_id=@id AND w.lease_epoch=@epoch AND w.lease_until>clock_timestamp() AND m.lease_until>clock_timestamp()
          AND {TaskOwnershipRepository.ActiveSql("e.id")}
          AND (CASE WHEN @source THEN w.source_node_id ELSE w.worker_node_id END)=@actor AND e.state IN ('running','cancelling')
          FOR UPDATE OF e,w,m
          """,c,tx);
        command.Parameters.AddWithValue("id",id); command.Parameters.AddWithValue("actor",actor); command.Parameters.AddWithValue("epoch",epoch); command.Parameters.AddWithValue("source",source);
        return await command.ExecuteScalarAsync(ct) is not null;
    }
    internal static async Task ReleaseAsync(Guid id,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        await using var cmd=Sql.Command("UPDATE pending_source_writes SET state='failed',detail='Écriture annulée avec son exécution.',claim_id=NULL,claim_until=NULL,finished_at=clock_timestamp() WHERE execution_id=@id AND state IN ('waiting','claimed') AND EXISTS(SELECT 1 FROM executions e WHERE e.id=@id AND e.state IN ('cancelling','cancelled','failed')); DELETE FROM media_reservations WHERE execution_id=@id AND NOT EXISTS(SELECT 1 FROM pending_source_writes p WHERE p.execution_id=@id AND p.state IN ('waiting','claimed')); DELETE FROM runtime_source_write_waits WHERE execution_id=@id; DELETE FROM disk_locks WHERE execution_id=@id; UPDATE execution_work SET lease_until=NULL,handoff_pending=false WHERE execution_id=@id",
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
          UPDATE execution_work SET lease_until=clock_timestamp()+interval '180 seconds' WHERE execution_id=@id;
          UPDATE media_reservations SET lease_until=clock_timestamp()+interval '180 seconds' WHERE execution_id=@id;
          UPDATE disk_locks SET lease_until=clock_timestamp()+interval '180 seconds' WHERE execution_id=@id;
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
        await using(var read=Sql.Command("SELECT e.flow_definition::text,w.library_id,w.placement_plan::text FROM executions e JOIN execution_work w ON w.execution_id=e.id WHERE e.id=@id",c,tx))
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
        await using(var read=Sql.Command("SELECT e.flow_definition::text,w.library_id,w.placement_plan::text,w.before_metadata::text FROM executions e JOIN execution_work w ON w.execution_id=e.id WHERE e.id=@id",
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

    private static async Task SuspendSourceWriteAsync(Guid id,Guid worker,string node,long epoch,string reason,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        await using var pause=Sql.Command("""
          INSERT INTO runtime_source_write_waits(execution_id,flow_node_id,reason) VALUES(@id,@node,@reason)
          ON CONFLICT(execution_id) DO UPDATE SET reason=@reason;
          DELETE FROM execution_steps WHERE execution_id=@id AND node_id=@node AND completed_at IS NULL;
          UPDATE execution_work SET worker_node_id=NULL,lease_until=NULL,lease_epoch=lease_epoch+1,available_at=clock_timestamp()+interval '15 seconds' WHERE execution_id=@id;
          UPDATE media_reservations SET lease_epoch=@epoch+1,lease_until='infinity'::timestamptz WHERE execution_id=@id;
          DELETE FROM disk_locks WHERE execution_id=@id;
          """,c,tx);
        pause.Parameters.AddWithValue("id",id);pause.Parameters.AddWithValue("node",node);pause.Parameters.AddWithValue("reason",reason);pause.Parameters.AddWithValue("epoch",epoch);
        await pause.ExecuteNonQueryAsync(ct);
        await using var revision=Sql.Command("SELECT revision FROM executions WHERE id=@id",c,tx);revision.Parameters.AddWithValue("id",id);
        await ExecutionRepository.RecordInTransactionAsync(id,new(Guid.NewGuid(),(await revision.ScalarInt32Async(ct))!.Value,"running",worker,null,reason),c,tx,ct);
    }

    public async Task<bool> AuthorizeSourceWriteAsync(Guid id,Guid worker,long epoch,string root,CancellationToken ct)
    {
        await using(var check=dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM execution_work w JOIN executions e ON e.id=w.execution_id WHERE w.execution_id=@id AND w.worker_node_id=@worker AND w.source_node_id=@worker AND w.lease_epoch=@epoch AND w.lease_until>clock_timestamp() AND e.state='running' AND EXISTS(SELECT 1 FROM library_node_accesses a WHERE a.library_id=w.library_id AND a.node_id=@worker AND a.is_enabled AND a.can_write AND a.mode='direct' AND a.local_path=@root))"))
        {
            check.Parameters.AddWithValue("id",id);check.Parameters.AddWithValue("worker",worker);check.Parameters.AddWithValue("epoch",epoch);check.Parameters.AddWithValue("root",root);
            if(!await check.ScalarBoolAsync(ct)) return false;
        }
        var verified=sourceWrites is null || await sourceWrites.VerifyAsync(ct);
        await using var c=await dataSource.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        if(!await FenceAsync(id,worker,epoch,c,tx,ct)) return false;
        if(sourceWrites is not null && !await sourceWrites.SupportsGuardAsync(worker,c,tx,ct)) return false;
        await using var step=Sql.Command("SELECT s.node_id FROM execution_steps s JOIN executions e ON e.id=s.execution_id JOIN execution_work w ON w.execution_id=e.id WHERE s.execution_id=@id AND s.completed_at IS NULL AND w.source_node_id=@worker AND EXISTS(SELECT 1 FROM library_node_accesses a WHERE a.library_id=w.library_id AND a.node_id=@worker AND a.is_enabled AND a.can_write AND a.mode='direct' AND a.local_path=@root) AND EXISTS(SELECT 1 FROM json_each(e.flow_definition,'$.nodes') n WHERE json_extract(n.value,'$.id')=s.node_id AND json_extract(n.value,'$.component.componentId')='mkv-edit-in-place')",
            c,tx);
        step.Parameters.AddWithValue("id",id);step.Parameters.AddWithValue("worker",worker);step.Parameters.AddWithValue("root",root);var node=await step.ExecuteScalarAsync(ct) as string;
        if(node is null) return false;
        var refusal=!verified?"Écriture source différée : la synchronisation préalable du cluster est indisponible.":sourceWrites is null?null:await sourceWrites.CheckAsync(c,tx,ct);
        if(refusal is not null)
        {
            // Called only by the native editor immediately before its first source mutation.
            await SuspendSourceWriteAsync(id,worker,node,epoch,refusal,c,tx,ct);
            await tx.CommitAsync(ct);return false;
        }
        await tx.CommitAsync(ct);return true;
    }

    public async Task<StartedRuntimeStep?> StartStepAsync(Guid id,Guid worker,StartRuntimeStep request,CancellationToken ct)
    {
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        if(!await FenceAsync(id,worker,request.Epoch,c,tx,ct)) return null;
        FlowDefinition flow;
        await using(var cmd=Sql.Command("SELECT flow_definition::text FROM executions WHERE id=@id AND state='running'",c,tx))
        { cmd.Parameters.AddWithValue("id",id); if(await cmd.ExecuteScalarAsync(ct) is not string json) return null; flow=JsonSerializer.Deserialize<FlowDefinition>(json,Json)!; }
        var node=flow.Nodes.SingleOrDefault(n=>n.Id==request.NodeId); if(node is null) return null;
        await using(var prior=Sql.Command("SELECT checkpoint::text FROM execution_steps WHERE execution_id=@id AND completed_at IS NOT NULL ORDER BY completed_revision DESC LIMIT 1",c,tx))
        {
            prior.Parameters.AddWithValue("id",id); var json=await prior.ExecuteScalarAsync(ct) as string;
            var expected=json is null ? flow.Nodes.Single(n=>components.Resolve(n.Component)!.Kind=="input").Id : JsonSerializer.Deserialize<RuntimeCheckpoint>(json,Json)!.NextNodeId;
            if(expected!=node.Id) return null;
        }
        if(node.Component.ComponentId=="mkv-edit-in-place" && sourceWrites is not null && await sourceWrites.CheckAsync(c,tx,ct) is {} reason)
        {
            await SuspendSourceWriteAsync(id,worker,node.Id,request.Epoch,reason,c,tx,ct);
            await tx.CommitAsync(ct);return null;
        }
        await using var start=Sql.Command("""
          INSERT INTO execution_steps(execution_id,node_id,invocation_id,idempotent) VALUES(@id,@node,@invocation,@idempotent) ON CONFLICT DO NOTHING;
          SELECT invocation_id,checkpoint::text FROM execution_steps WHERE execution_id=@id AND node_id=@node
          """,c,tx);
        start.Parameters.AddWithValue("id",id); start.Parameters.AddWithValue("node",request.NodeId); start.Parameters.AddWithValue("invocation",Guid.NewGuid());
        start.Parameters.AddWithValue("idempotent",RuntimeStepEffects.IsReplayable(node.Component.PluginId,node.Component.ComponentId));
        await using var r=await start.ExecuteReaderAsync(ct); await r.ReadAsync(ct);
        var result=new StartedRuntimeStep(r.GetGuid(0),r.IsDBNull(1)?null:JsonSerializer.Deserialize<RuntimeCheckpoint>(r.GetString(1),Json));
        await r.DisposeAsync(); await tx.CommitAsync(ct); return result;
    }

    public async Task<bool> CompleteStepAsync(Guid id,Guid worker,CompleteRuntimeStep request,CancellationToken ct)
    {
        var cp=request.Checkpoint;
        var commands=request.Commands??[];
        if(cp is null || string.IsNullOrWhiteSpace(cp.NodeId) || cp.InvocationId==Guid.Empty
            || cp.Context is null || cp.Context.Variables is null || cp.Context.Plan is null
            || cp.Context.VideoEncodings is { Count: > 64 }
            || !(RuntimeArtifactStore.ValidDigest(cp.Context.CurrentSha256) || cp.Context.CurrentSha256=="" && cp.Context.DirectSource is { IsValid:true })
            || cp.Context.Extension is null || !System.Text.RegularExpressions.Regex.IsMatch(cp.Context.Extension,"^\\.[a-zA-Z0-9]{1,10}$")
            || commands.Count>64 || commands.Any(command=>!ValidCommandLog(command))
            || commands.Select(command=>command.Id).Distinct().Count()!=commands.Count) return false;
        if(JsonSerializer.SerializeToUtf8Bytes(commands,Json).Length>524288) return false;
        var serialized=JsonSerializer.Serialize(cp,Json); if(serialized.Length>1048576) return false;
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        // A lost handoff response can be retried after its lease has already been revoked.
        // Only the exact checkpoint, actor and completed epoch can acknowledge this duplicate.
        await using(var duplicate=Sql.Command("""
          SELECT s.checkpoint=@checkpoint FROM execution_steps s JOIN execution_work w ON w.execution_id=s.execution_id
          JOIN executions e ON e.id=w.execution_id
          WHERE s.execution_id=@id AND s.node_id=@node AND s.invocation_id=@invocation
            AND s.completed_at IS NOT NULL AND s.completed_epoch=@epoch AND s.completed_worker_node_id=@worker
          FOR UPDATE OF e,w
          """,c,tx))
        {
            duplicate.Parameters.AddWithValue("id",id); duplicate.Parameters.AddWithValue("node",cp.NodeId);
            duplicate.Parameters.AddWithValue("invocation",cp.InvocationId); duplicate.Parameters.AddWithValue("epoch",request.Epoch);
            duplicate.Parameters.AddWithValue("worker",worker); duplicate.Parameters.AddWithValue("checkpoint",SqlKind.Json,serialized);
            if(await duplicate.ScalarNullableBoolAsync(ct) is {} same) return same;
        }
        if(!await FenceAsync(id,worker,request.Epoch,c,tx,ct)) return false;
        FlowDefinition flow;
        await using(var cmd=Sql.Command("SELECT flow_definition::text FROM executions WHERE id=@id AND state='running'",c,tx))
        { cmd.Parameters.AddWithValue("id",id); if(await cmd.ExecuteScalarAsync(ct) is not string json) return false; flow=JsonSerializer.Deserialize<FlowDefinition>(json,Json)!; }
        if(!await ValidVideoEncodingsAsync(flow,id,worker,cp.NodeId,cp.Context.VideoEncodings,c,tx,ct)) return false;
        if(cp.Context.DirectSource is not null)
        {
            var inspected=flow.Nodes.SingleOrDefault(node=>node.Id==cp.NodeId);
            if(cp.Context.CurrentSha256!="" || cp.Handoff is not null || cp.Wait is not null || cp.Context.Plan.Count>0
                || inspected is null || !(DirectMediaInspection.Allows(inspected.Component.PluginId,inspected.Component.ComponentId,
                    FlowOperationConfiguration.Resolve(inspected.Component.ComponentId,inspected.Configuration),cp.Context.Plan.Count,
                    FfmpegBuilderPolicy.Active(cp.Context.Variables))
                    || DirectMediaInspection.AllowsMetadataOnly(components.Resolve(inspected.Component)))) return false;
            await using var direct=Sql.Command("SELECT EXISTS(SELECT 1 FROM execution_work WHERE execution_id=@id AND worker_node_id=@worker AND source_node_id=@worker AND NOT source_preparation_requested AND source_sha IS NULL)",
                c,tx);
            direct.Parameters.AddWithValue("id",id); direct.Parameters.AddWithValue("worker",worker);
            if(!await direct.ScalarBoolAsync(ct)) return false;
        }
        else await using(var artifact=Sql.Command("SELECT EXISTS(SELECT 1 FROM execution_artifacts WHERE execution_id=@id AND sha256=@sha)",c,tx))
        {
            artifact.Parameters.AddWithValue("id",id); artifact.Parameters.AddWithValue("sha",cp.Context.CurrentSha256);
            if(!(await artifact.ScalarBoolAsync(ct))) return false;
        }
        // Additional files produced by a prior step must remain available after the workspace changes.
        foreach(var variable in cp.Context.Variables.Where(pair=>pair.Key.StartsWith("artifact.",StringComparison.Ordinal)))
        {
            if(variable.Value.ValueKind!=JsonValueKind.Object || !variable.Value.TryGetProperty("sha256",out var digest)
                || digest.ValueKind!=JsonValueKind.String || !RuntimeArtifactStore.ValidDigest(digest.GetString()!)
                || !variable.Value.TryGetProperty("name",out var name) || name.ValueKind!=JsonValueKind.String
                || string.IsNullOrWhiteSpace(name.GetString()) || name.GetString()!.Length>255
                || name.GetString()!.Contains('/') || name.GetString()!.Contains('\\')) return false;
            await using var artifact=Sql.Command("SELECT EXISTS(SELECT 1 FROM execution_artifacts WHERE execution_id=@id AND sha256=@sha)",c,tx);
            artifact.Parameters.AddWithValue("id",id); artifact.Parameters.AddWithValue("sha",digest.GetString()!);
            if(!(await artifact.ScalarBoolAsync(ct))) return false;
        }
        var node=flow.Nodes.SingleOrDefault(n=>n.Id==cp.NodeId); if(node is null) return false;
        RuntimeHandoffSelector? handoff=null;
        if(RuntimeHandoffPolicy.IsHandoff(node))
        {
            await using var settings=Sql.Command("SELECT plugin_configuration_snapshot FROM execution_work WHERE execution_id=@id",c,tx);
            settings.Parameters.AddWithValue("id",id);
            var snapshot=await settings.ExecuteScalarAsync(ct) as byte[];
            try { handoff=RuntimeHandoffPolicy.Resolve(node,snapshot is null?null:pluginConfigurations.ReadSnapshot(snapshot).GetValueOrDefault("nodes")); }
            catch(ArgumentException) { return false; }
            if(handoff.Mode=="server" && ownership is null) return false;
            if(cp.Handoff!=handoff || cp.PlacementHandoff || cp.NextNodeId is null || handoff.Mode=="node" && handoff.TargetNodeId==worker) return false;
            // Le serveur visé est déjà le propriétaire : il n'y a rien à céder entre serveurs, seul le worker change.
            // Le relais devient un relais vers un autre worker éligible de ce serveur (qui porte l'accès direct, par exemple).
            if(handoff.Mode=="server" && ownership!.IsLocalPeer(handoff.TargetNodeId!.Value)) handoff=new("eligible");
        }
        else if(cp.Handoff is not null)
        {
            if(!cp.PlacementHandoff || cp.NextNodeId is null || cp.Handoff.Mode!="node" || cp.Handoff.TargetNodeId is not { } target) return false;
            await using var savedPlan=Sql.Command("SELECT placement_plan::text FROM execution_work WHERE execution_id=@id",c,tx);
            savedPlan.Parameters.AddWithValue("id",id);
            var planJson=await savedPlan.ExecuteScalarAsync(ct) as string;
            var plan=planJson is null?null:JsonSerializer.Deserialize<FlowPlacementPlan>(planJson,Json);
            if(plan?.NodeAssignments?.TryGetValue(cp.NextNodeId,out var planned)!=true || planned!=target || target==worker) return false;
            handoff=cp.Handoff;
        }
        else if(cp.PlacementHandoff) return false;
        RuntimeWait? wait=null;
        if(RuntimeWaitPolicy.IsWait(node))
        {
            try { wait=RuntimeWaitPolicy.Resolve(node); }
            catch(ArgumentException) { return false; }
            if(cp.Wait!=wait || cp.NextNodeId is null || cp.OutputId!="resumed" || handoff is not null) return false;
        }
        else if(cp.Wait is not null) return false;
        var component=components.Resolve(node.Component)!;
        if(component.Kind=="terminal" ? cp.OutputId is not null || cp.TerminalResult is not("success" or "unchanged" or "failed" or "deferred")
            : cp.TerminalResult is not null || !component.Outputs.Any(o=>o.Id==cp.OutputId)) return false;
        if(cp.NextNodeId!=flow.Connections.SingleOrDefault(e=>e.SourceNodeId==node.Id && e.SourceOutputId==cp.OutputId)?.TargetNodeId) return false;
        RuntimeCommandLog[] existingLogs;
        await using(var readLogs=Sql.Command("SELECT command_logs FROM execution_steps WHERE execution_id=@id AND node_id=@node AND invocation_id=@invocation AND completed_at IS NULL FOR UPDATE",
            c,tx))
        {
            readLogs.Parameters.AddWithValue("id",id); readLogs.Parameters.AddWithValue("node",cp.NodeId); readLogs.Parameters.AddWithValue("invocation",cp.InvocationId);
            await using var row=await readLogs.ExecuteReaderAsync(ct);
            if(!await row.ReadAsync(ct) || !RuntimeCommandLogCodec.TryDeserialize(row.GetString(0),out existingLogs)) return false;
        }
        var finalCommands=RuntimeCommandJournal.Merge(existingLogs,commands);
        var serializedFinalCommands=JsonSerializer.Serialize(finalCommands,Json);
        if(System.Text.Encoding.UTF8.GetByteCount(serializedFinalCommands)>524288) return false;
        await using var update=Sql.Command("""
          UPDATE execution_steps SET completed_at=clock_timestamp(),completed_revision=@completed_revision,checkpoint=@checkpoint,command_logs=@commands,
            completed_epoch=@epoch,completed_worker_node_id=@worker
          WHERE execution_id=@id AND node_id=@node AND invocation_id=@invocation AND completed_at IS NULL RETURNING node_id
          """,c,tx);
        update.Parameters.AddWithValue("completed_revision",request.ExpectedRevision+1); update.Parameters.AddWithValue("id",id);
            update.Parameters.AddWithValue("node",cp.NodeId); update.Parameters.AddWithValue("invocation",cp.InvocationId);
            update.Parameters.AddWithValue("checkpoint",SqlKind.Json,serialized);
        update.Parameters.AddWithValue("epoch",request.Epoch); update.Parameters.AddWithValue("worker",worker);
        update.Parameters.AddWithValue("commands",SqlKind.Json,serializedFinalCommands);
        update.Parameters.AddWithValue("before",SqlKind.Json,(object?)cp.Context.BeforeMetadata?.GetRawText()??DBNull.Value);
        update.Parameters.AddWithValue("after",SqlKind.Json,(object?)cp.Context.CurrentMetadata?.GetRawText()??DBNull.Value);
        if(await update.ExecuteScalarAsync(ct) is null) return false;
        await using(var metadata=Sql.Command("UPDATE execution_work SET before_metadata=@before,after_metadata=@after WHERE execution_id=@id",c,tx))
        {
            metadata.Parameters.AddWithValue("id",id);
            metadata.Parameters.AddWithValue("before",SqlKind.Json,(object?)cp.Context.BeforeMetadata?.GetRawText()??DBNull.Value);
            metadata.Parameters.AddWithValue("after",SqlKind.Json,(object?)cp.Context.CurrentMetadata?.GetRawText()??DBNull.Value);
            await metadata.ExecuteNonQueryAsync(ct);
        }
        var detail=handoff is null?$"Étape terminée : {Encoding.Contracts.Text.Mojibake.Repair(node.Name??component.Name)}.":handoff.Mode=="node"
            ?$"Relais en attente vers le nœud {handoff.TargetNodeId:D}.":"Relais en attente d'un autre worker éligible.";
        if (node.Component.ComponentId == MediaIntegrityPolicy.Component && ExecutionRepository.ReadIntegrityDiagnostic(cp) is { } diagnostic)
            detail += " " + diagnostic.Code + " : " + diagnostic.Message;
        if(wait is not null) detail=ExecutionWaitRepository.Describe(wait);
        if(!await ExecutionRepository.RecordInTransactionAsync(id,new(request.EventId,request.ExpectedRevision,"running",worker,null,detail),c,tx,ct)) return false;
        if(handoff is not null)
        {
            await using var yield=Sql.Command("""
              UPDATE execution_work SET handoff_pending=true,handoff_selector=@selector,previous_worker_node_id=@worker,
                worker_node_id=NULL,lease_until=NULL,lease_epoch=lease_epoch+1 WHERE execution_id=@id;
              UPDATE media_reservations SET lease_epoch=@epoch+1,lease_until='infinity'::timestamptz WHERE execution_id=@id;
              DELETE FROM disk_locks WHERE execution_id=@id;
              """,c,tx);
            yield.Parameters.AddWithValue("id",id); yield.Parameters.AddWithValue("worker",worker); yield.Parameters.AddWithValue("epoch",request.Epoch);
            yield.Parameters.AddWithValue("selector",SqlKind.Json,JsonSerializer.Serialize(handoff,Json));
            await yield.ExecuteNonQueryAsync(ct);
        }
        if(wait is not null) await ExecutionWaitRepository.BeginAsync(id,node.Id,wait,request.Epoch,c,tx,ct);
        if(ownership is not null) await ownership.SaveCheckpointAsync(id,c,tx,ct);
        await tx.CommitAsync(ct); return true;
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

    private static async Task<bool> ValidVideoEncodingsAsync(FlowDefinition flow,Guid executionId,Guid worker,string currentNodeId,
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
                await using var owner=Sql.Command("SELECT completed_worker_node_id FROM execution_steps WHERE execution_id=@id AND node_id=@node AND completed_at IS NOT NULL",c,tx);
                owner.Parameters.AddWithValue("id",executionId); owner.Parameters.AddWithValue("node",resolution.FlowNodeId);
                executionWorker=await owner.ScalarGuidAsync(ct) is {} nodeId?nodeId:null;
                if(executionWorker!=resolution.WorkerId)
                {
                    await using var plannedOwner=Sql.Command("SELECT completed_worker_node_id FROM execution_steps WHERE execution_id=@id AND completed_at IS NOT NULL AND EXISTS(SELECT 1 FROM json_each(checkpoint->'context'->'videoEncodings') stored, json_each(@resolution) wanted WHERE json(stored.value)=json(wanted.value)) ORDER BY completed_revision DESC LIMIT 1",
                        c,tx);
                    plannedOwner.Parameters.AddWithValue("id",executionId); plannedOwner.Parameters.AddWithValue("resolution",SqlKind.Json,JsonSerializer.Serialize(new[]{resolution},Json));
                    executionWorker=await plannedOwner.ScalarGuidAsync(ct) is {} plannedNodeId?plannedNodeId:null;
                }
            }
            if(executionWorker!=resolution.WorkerId || string.IsNullOrWhiteSpace(resolution.FfmpegVersion)
                || resolution.FfmpegVersion.Length>128 || resolution.ResolvedAt==default) return false;
            var node=flow.Nodes.SingleOrDefault(item=>item.Id==resolution.FlowNodeId);
            if(node is null || (node.Component.PluginId!="base" && node.Component.PluginId!="video")
                || (node.Component.ComponentId!="base-transcode-video" && node.Component.ComponentId!="video-transcode")
                || !node.Configuration.TryGetValue("encodingProfile",out var profileValue)
                || profileValue.ValueKind!=JsonValueKind.Object) return false;
            var profile=profileValue.Deserialize<EncodingProfileSnapshot>(Json);
            var codec=node.Configuration.TryGetValue("videoCodec",out var codecValue)?codecValue.GetString():"hevc";
            var encoder=VideoEncoderCatalog.Find(resolution.EncoderId);
            var target=profile?.Targets.SingleOrDefault(item=>item.EncoderId==resolution.EncoderId);
            var preset=encoder?.Presets.FirstOrDefault(item=>item.Id==target?.Preset)?.Value;
            if(profile is null || encoder is null || target is null || resolution.Codec!=codec || encoder.Codec!=codec
                || resolution.ProfileId!=profile.Id || resolution.ProfileVersion!=profile.Version || resolution.ProfileName!=profile.Name
                || resolution.Acceleration!=encoder.Acceleration || resolution.QualityMode!=encoder.QualityMode
                || resolution.QualityOption!=encoder.QualityOption || resolution.Quality!=target.Quality
                || resolution.PresetId!=target.Preset || resolution.Priority!=target.Priority
                || resolution.PresetOption!=(string.IsNullOrEmpty(encoder.PresetOption)?null:encoder.PresetOption)
                || resolution.PresetValue!=preset) return false;
        }
        return true;
    }

    /// <summary>Lecture seule : un worker à accès direct en ligne, autre que celui qui réclame, peut-il exécuter ce segment ?</summary>
    private async Task<bool> CapableDirectWorkerExistsAsync(Guid library,bool write,
        (Guid Id,Guid Library,string Path,string Key,FlowDefinition Flow,int Attempt,int Revision,long Epoch,RuntimeCheckpoint? Checkpoint,
            RuntimeHandoffSelector? Selector,Guid? PreviousWorker,Guid? Source,byte[]? Settings,RuntimeWaitProbe? Probe,DateTimeOffset QueuedAt,
            FlowPlacementPlan? PlacementPlan,JsonElement? BeforeMetadata) item,
        Guid claimant,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        var direct=new List<(Guid Id,IReadOnlyList<NodeToolCapability> Capabilities,NodeInventory? Inventory)>();
        await using(var read=Sql.Command("""
          SELECT dn.id,dn.capabilities::text,dn.inventory::text FROM library_node_accesses da JOIN cluster_nodes dn ON dn.id=da.node_id
          WHERE da.library_id=@library AND da.mode='direct' AND da.is_enabled AND da.can_read AND da.local_path IS NOT NULL AND (NOT @write OR da.can_write)
            AND dn.id<>@claimant AND dn.id IS DISTINCT FROM @previous AND dn.deleted_at IS NULL AND dn.last_seen_at>clock_timestamp()-@offline AND dn.accepts_work AND 'worker'=ANY(dn.roles)
            -- Un worker direct saturé ne retient pas le travail : sa file ne doit pas laisser les autres nœuds inactifs.
            AND (SELECT count(*) FROM execution_work w JOIN executions e ON e.id=w.execution_id WHERE w.worker_node_id=dn.id AND w.lease_until>clock_timestamp() AND e.state IN ('running','cancelling'))
                <COALESCE((SELECT p.max_concurrent_tasks FROM node_processing_settings p WHERE p.node_id=dn.id),dn.max_concurrent_tasks)
          """,c,tx))
        {
            read.Parameters.AddWithValue("library",library); read.Parameters.AddWithValue("write",write);
            read.Parameters.AddWithValue("claimant",claimant); read.Parameters.AddWithValue("previous",
                item.PreviousWorker is {} previous?previous:DBNull.Value); read.Parameters.AddWithValue("offline",presence.OfflineAfter);
            await using var r=await read.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
                direct.Add((r.GetGuid(0),JsonSerializer.Deserialize<NodeToolCapability[]>(r.GetString(1),Json)??[],r.IsDBNull(2)?null:JsonSerializer.Deserialize<NodeInventory>(r.GetString(2),Json)));
        }
        foreach(var node in direct)
        {
            var identity=await nodeGroups.GetNodeIdentityAsync(node.Id,ct);
            if(identity is not null
                && eligibility.CanRun(item.Flow,node.Capabilities,node.Id,item.Checkpoint?.NextNodeId,item.Checkpoint is not null,item.Checkpoint?.Context,
                    identity,node.Inventory,item.PlacementPlan?.NodeAssignments)) return true;
        }
        return false;
    }
}
