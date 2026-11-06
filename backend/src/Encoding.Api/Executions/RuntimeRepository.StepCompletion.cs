using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Plugins;

namespace Encoding.Api.Executions;

// Complétion d'une étape (steps/complete) : une validation nommée par famille de refus. Chaque refus renvoie un code stable
// (StepRefusalCodes), journalisé en Warning avec l'exécution, le nœud, le worker et l'époque, et renvoyé dans le corps du 409.
public sealed partial class RuntimeRepository
{
    public async Task<bool> CompleteStepAsync(Guid id,Guid worker,CompleteRuntimeStep request,CancellationToken ct) =>
        await TryCompleteStepAsync(id,worker,request,ct) is null;

    /// <summary>Complète l'étape ; retourne <c>null</c> si elle est acceptée, sinon le motif du refus (déjà journalisé).</summary>
    public async Task<StepRefusal?> TryCompleteStepAsync(Guid id,Guid worker,CompleteRuntimeStep request,CancellationToken ct)
    {
        var commands=request.Commands??[];
        if(ValidateCheckpointShape(request.Checkpoint,commands) is {} shape) return Refuse(shape,"steps/complete",id,worker,request.Checkpoint?.NodeId,request.Epoch);
        var cp=request.Checkpoint;
        var serialized=JsonSerializer.Serialize(cp,Json);
        if(serialized.Length>1048576) return Refuse(StepRefusalCodes.CheckpointTooLarge,"steps/complete",id,worker,cp.NodeId,request.Epoch);
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        StepRefusal? Fail(string code)=>Refuse(code,"steps/complete",id,worker,cp.NodeId,request.Epoch);
        // A lost handoff response can be retried after its lease has already been revoked.
        // Only the exact checkpoint, actor and completed epoch can acknowledge this duplicate.
        if(await AcknowledgeDuplicateAsync(id,worker,request,serialized,c,tx,ct) is {} same) return same?null:Fail(StepRefusalCodes.DuplicateCheckpointDiffers);
        if(!await FenceAsync(id,worker,request.Epoch,c,tx,ct)) return Fail(await DiagnoseFenceAsync(id,worker,request.Epoch,c,tx,ct));
        var flow=await LoadRunningFlowAsync(id,c,tx,ct);
        if(flow is null) return Fail(StepRefusalCodes.ExecutionNotRunning);
        if(!await ValidVideoEncodingsAsync(flow,id,worker,cp.NodeId,cp.Context.VideoEncodings,c,tx,ct)) return Fail(StepRefusalCodes.VideoEncodingInvalid);
        if(await ValidateSourceAsync(flow,id,worker,cp,c,tx,ct) is {} source) return Fail(source);
        var node=flow.Nodes.SingleOrDefault(n=>n.Id==cp.NodeId);
        if(node is null) return Fail(StepRefusalCodes.NodeUnknown);
        var (handoff,handoffCode)=await ResolveHandoffAsync(node,id,worker,cp,c,tx,ct);
        if(handoffCode is not null) return Fail(handoffCode);
        var (wait,waitCode)=ResolveWait(node,cp,handoff);
        if(waitCode is not null) return Fail(waitCode);
        var component=components.Resolve(node.Component)!;
        if(ValidateOutput(flow,node,component,cp) is {} output) return Fail(output);
        var (finalLogs,logsCode)=await MergeStepLogsAsync(id,cp,commands,c,tx,ct);
        if(logsCode is not null) return Fail(logsCode);
        if(await PersistCompletionAsync(id,worker,request,serialized,finalLogs!,c,tx,ct) is {} persisted) return Fail(persisted);
        var detail=handoff is null?$"Étape terminée : {Encoding.Contracts.Text.Mojibake.Repair(node.Name??component.Name)}.":handoff.Mode=="node"
            ?$"Relais en attente vers le worker {handoff.TargetWorkerId:D}.":"Relais en attente d'un autre worker éligible.";
        if (node.Component.ComponentId == MediaIntegrityPolicy.Component && ExecutionRepository.ReadIntegrityDiagnostic(cp) is { } diagnostic)
            detail += " " + diagnostic.Code + " : " + diagnostic.Message;
        if(wait is not null) detail=ExecutionWaitRepository.Describe(wait);
        if(!await ExecutionRepository.RecordInTransactionAsync(id,new(request.EventId,request.ExpectedRevision,"running",worker,null,detail),c,tx,ct))
            return Fail(StepRefusalCodes.RevisionConflict);
        if(handoff is not null) await YieldToHandoffAsync(id,worker,request.Epoch,handoff,c,tx,ct);
        if(wait is not null) await ExecutionWaitRepository.BeginAsync(id,node.Id,wait,request.Epoch,c,tx,ct);
        await tx.CommitAsync(ct); return null;
    }

    private StepRefusal Refuse(string code,string operation,Guid id,Guid worker,string? nodeId,long epoch)
    {
        logger?.LogWarning("Étape refusée ({Operation}) : motif {Reason} — exécution {ExecutionId}, nœud {NodeId}, worker {WorkerId}, époque {Epoch}",
            operation,code,id,nodeId,worker,epoch);
        return new StepRefusal(code);
    }

    /// <summary>Forme du checkpoint et des journaux de commandes, sans accès base ; <c>null</c> si valide.</summary>
    internal static string? ValidateCheckpointShape(RuntimeCheckpoint? cp,IReadOnlyList<RuntimeCommandLog> commands)
    {
        if(cp is null || string.IsNullOrWhiteSpace(cp.NodeId) || cp.InvocationId==Guid.Empty) return StepRefusalCodes.CheckpointMissing;
        var context=cp.Context;
        if(context is null || context.Variables is null || context.Plan is null) return StepRefusalCodes.ContextIncomplete;
        if(context.VideoEncodings is { Count: > 64 }) return StepRefusalCodes.VideoEncodingsLimit;
        if(!(RuntimeArtifactStore.ValidDigest(context.CurrentSha256) || context.CurrentSha256=="" && context.DirectSource is { IsValid:true }))
            return StepRefusalCodes.SourceDigestInvalid;
        if(context.Extension is null || !System.Text.RegularExpressions.Regex.IsMatch(context.Extension,"^\\.[a-zA-Z0-9]{1,10}$")) return StepRefusalCodes.ExtensionInvalid;
        if(commands.Count>64) return StepRefusalCodes.CommandsLimit;
        if(commands.Any(command=>!ValidCommandLog(command))) return StepRefusalCodes.CommandLogInvalid;
        if(commands.Select(command=>command.Id).Distinct().Count()!=commands.Count) return StepRefusalCodes.CommandIdDuplicate;
        return JsonSerializer.SerializeToUtf8Bytes(commands,Json).Length>524288?StepRefusalCodes.CommandsTooLarge:null;
    }

    /// <summary>Un renvoi perdu d'un relais déjà complété : vrai si le checkpoint est identique, faux s'il diffère, <c>null</c> si l'étape n'est pas complétée.</summary>
    private static async Task<bool?> AcknowledgeDuplicateAsync(Guid id,Guid worker,CompleteRuntimeStep request,string serialized,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        var cp=request.Checkpoint;
        await using var duplicate=Sql.Command("""
          SELECT s.checkpoint=@checkpoint FROM execution_steps s JOIN execution_work w ON w.execution_id=s.execution_id
          JOIN executions e ON e.id=w.execution_id
          WHERE s.execution_id=@id AND s.flow_node_id=@node AND s.invocation_id=@invocation
            AND s.completed_at IS NOT NULL AND s.completed_epoch=@epoch AND s.completed_worker_id=@worker
          """,c,tx);
        duplicate.Parameters.AddWithValue("id",id); duplicate.Parameters.AddWithValue("node",cp.NodeId);
        duplicate.Parameters.AddWithValue("invocation",cp.InvocationId); duplicate.Parameters.AddWithValue("epoch",request.Epoch);
        duplicate.Parameters.AddWithValue("worker",worker); duplicate.Parameters.AddWithValue("checkpoint",SqlKind.Json,serialized);
        return await duplicate.ScalarNullableBoolAsync(ct);
    }

    /// <summary>Précise pourquoi <see cref="FenceAsync"/> a refusé (appelé seulement après un refus, une requête de lecture).</summary>
    internal static async Task<string> DiagnoseFenceAsync(Guid id,Guid actor,long epoch,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        await using var command=Sql.Command("""
          SELECT e.state,w.worker_id,w.lease_epoch,w.lease_until>(strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z'),
            (SELECT count(*) FROM media_reservations m WHERE m.execution_id=e.id AND m.lease_epoch=w.lease_epoch
              AND m.lease_until>(strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z'))
          FROM executions e LEFT JOIN execution_work w ON w.execution_id=e.id WHERE e.id=@id
          """,c,tx);
        command.Parameters.AddWithValue("id",id);
        await using var row=await command.ExecuteReaderAsync(ct);
        if(!await row.ReadAsync(ct)) return StepRefusalCodes.ExecutionUnknown;
        if(row.GetString(0) is not ("running" or "cancelling")) return StepRefusalCodes.ExecutionNotActive;
        if(row.IsDBNull(1) || row.GetGuid(1)!=actor) return StepRefusalCodes.LeaseWrongWorker;
        if(row.GetInt64(2)!=epoch) return StepRefusalCodes.LeaseEpochMismatch;
        if(row.IsDBNull(3) || row.GetInt64(3)==0) return StepRefusalCodes.LeaseExpired;
        return StepRefusalCodes.ReservationLost;
    }

    private static async Task<FlowDefinition?> LoadRunningFlowAsync(Guid id,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        await using var cmd=Sql.Command("SELECT flow_definition FROM executions WHERE id=@id AND state='running'",c,tx);
        cmd.Parameters.AddWithValue("id",id);
        return await cmd.ExecuteScalarAsync(ct) is string json?JsonSerializer.Deserialize<FlowDefinition>(json,Json)!:null;
    }

    /// <summary>Le fichier courant (source directe ou artefact) et les artefacts annexes doivent exister ; <c>null</c> si valide.</summary>
    private async Task<string?> ValidateSourceAsync(FlowDefinition flow,Guid id,Guid worker,RuntimeCheckpoint cp,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        if(cp.Context.DirectSource is not null)
        {
            var inspected=flow.Nodes.SingleOrDefault(node=>node.Id==cp.NodeId);
            if(cp.Context.CurrentSha256!="" || cp.Handoff is not null || cp.Wait is not null || cp.Context.Plan.Count>0
                || inspected is null || !(DirectMediaInspection.Allows(inspected.Component.PluginId,inspected.Component.ComponentId,
                    FlowOperationConfiguration.Resolve(inspected.Component.ComponentId,inspected.Configuration),cp.Context.Plan.Count,
                    FfmpegBuilderPolicy.Active(cp.Context.Variables))
                    || DirectMediaInspection.AllowsMetadataOnly(components.Resolve(inspected.Component)))) return StepRefusalCodes.DirectSourceNotAllowed;
            await using var direct=Sql.Command("SELECT EXISTS(SELECT 1 FROM execution_work WHERE execution_id=@id AND worker_id=@worker AND source_worker_id=@worker AND NOT source_preparation_requested AND source_sha IS NULL)",
                c,tx);
            direct.Parameters.AddWithValue("id",id); direct.Parameters.AddWithValue("worker",worker);
            if(!await direct.ScalarBoolAsync(ct)) return StepRefusalCodes.DirectSourceStateInvalid;
        }
        else if(!await ArtifactExistsAsync(id,cp.Context.CurrentSha256,c,tx,ct)) return StepRefusalCodes.ArtifactMissing;
        // Additional files produced by a prior step must remain available after the workspace changes.
        foreach(var variable in cp.Context.Variables.Where(pair=>pair.Key.StartsWith("artifact.",StringComparison.Ordinal)))
        {
            if(variable.Value.ValueKind!=JsonValueKind.Object || !variable.Value.TryGetProperty("sha256",out var digest)
                || digest.ValueKind!=JsonValueKind.String || !RuntimeArtifactStore.ValidDigest(digest.GetString()!)
                || !variable.Value.TryGetProperty("name",out var name) || name.ValueKind!=JsonValueKind.String
                || string.IsNullOrWhiteSpace(name.GetString()) || name.GetString()!.Length>255
                || name.GetString()!.Contains('/') || name.GetString()!.Contains('\\')) return StepRefusalCodes.ExtraArtifactInvalid;
            if(!await ArtifactExistsAsync(id,digest.GetString()!,c,tx,ct)) return StepRefusalCodes.ExtraArtifactMissing;
        }
        return null;
    }

    private static async Task<bool> ArtifactExistsAsync(Guid id,string sha,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        await using var artifact=Sql.Command("SELECT EXISTS(SELECT 1 FROM execution_artifacts WHERE execution_id=@id AND sha256=@sha)",c,tx);
        artifact.Parameters.AddWithValue("id",id); artifact.Parameters.AddWithValue("sha",sha);
        return await artifact.ScalarBoolAsync(ct);
    }

    private async Task<(RuntimeHandoffSelector? Handoff,string? Code)> ResolveHandoffAsync(FlowNodeDefinition node,Guid id,Guid worker,
        RuntimeCheckpoint cp,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        if(RuntimeHandoffPolicy.IsHandoff(node))
        {
            await using var settings=Sql.Command("SELECT plugin_configuration_snapshot FROM execution_work WHERE execution_id=@id",c,tx);
            settings.Parameters.AddWithValue("id",id);
            var snapshot=await settings.ExecuteScalarAsync(ct) as byte[];
            RuntimeHandoffSelector handoff;
            try { handoff=RuntimeHandoffPolicy.Resolve(node,snapshot is null?null:pluginConfigurations.ReadSnapshot(snapshot).GetValueOrDefault("nodes")); }
            catch(ArgumentException) { return (null,StepRefusalCodes.HandoffPolicyInvalid); }
            if(cp.Handoff!=handoff || cp.PlacementHandoff || cp.NextNodeId is null || handoff.Mode=="node" && handoff.TargetWorkerId==worker)
                return (null,StepRefusalCodes.HandoffMismatch);
            return (handoff,null);
        }
        if(cp.Handoff is null) return cp.PlacementHandoff?(null,StepRefusalCodes.PlacementHandoffUnexpected):(null,null);
        if(!cp.PlacementHandoff || cp.NextNodeId is null || cp.Handoff.Mode!="node" || cp.Handoff.TargetWorkerId is not { } target)
            return (null,StepRefusalCodes.PlacementHandoffInvalid);
        await using var savedPlan=Sql.Command("SELECT placement_plan FROM execution_work WHERE execution_id=@id",c,tx);
        savedPlan.Parameters.AddWithValue("id",id);
        var planJson=await savedPlan.ExecuteScalarAsync(ct) as string;
        var plan=planJson is null?null:JsonSerializer.Deserialize<FlowPlacementPlan>(planJson,Json);
        if(plan?.WorkerAssignments?.TryGetValue(cp.NextNodeId,out var planned)!=true || planned!=target || target==worker)
            return (null,StepRefusalCodes.PlacementHandoffInvalid);
        return (cp.Handoff,null);
    }

    private static (RuntimeWait? Wait,string? Code) ResolveWait(FlowNodeDefinition node,RuntimeCheckpoint cp,RuntimeHandoffSelector? handoff)
    {
        if(!RuntimeWaitPolicy.IsWait(node)) return cp.Wait is not null?(null,StepRefusalCodes.WaitUnexpected):(null,null);
        RuntimeWait wait;
        try { wait=RuntimeWaitPolicy.Resolve(node); }
        catch(ArgumentException) { return (null,StepRefusalCodes.WaitPolicyInvalid); }
        if(cp.Wait!=wait || cp.NextNodeId is null || cp.OutputId!="resumed" || handoff is not null) return (null,StepRefusalCodes.WaitMismatch);
        return (wait,null);
    }

    /// <summary>La sortie déclarée doit exister sur le composant, et la connexion suivante correspondre au graphe ; <c>null</c> si valide.</summary>
    private static string? ValidateOutput(FlowDefinition flow,FlowNodeDefinition node,FlowComponentDescriptor component,RuntimeCheckpoint cp)
    {
        if(component.Kind=="terminal" ? cp.OutputId is not null || cp.TerminalResult is not("success" or "unchanged" or "failed" or "deferred")
            : cp.TerminalResult is not null || !component.Outputs.Any(o=>o.Id==cp.OutputId)) return StepRefusalCodes.OutputInvalid;
        return cp.NextNodeId!=flow.Connections.SingleOrDefault(e=>e.SourceNodeId==node.Id && e.SourceOutputId==cp.OutputId)?.TargetNodeId
            ?StepRefusalCodes.NextNodeMismatch:null;
    }

    private static async Task<(string? Serialized,string? Code)> MergeStepLogsAsync(Guid id,RuntimeCheckpoint cp,IReadOnlyList<RuntimeCommandLog> commands,
        DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        RuntimeCommandLog[] existingLogs;
        await using(var readLogs=Sql.Command("SELECT command_logs FROM execution_steps WHERE execution_id=@id AND flow_node_id=@node AND invocation_id=@invocation AND completed_at IS NULL",
            c,tx))
        {
            readLogs.Parameters.AddWithValue("id",id); readLogs.Parameters.AddWithValue("node",cp.NodeId); readLogs.Parameters.AddWithValue("invocation",cp.InvocationId);
            await using var row=await readLogs.ExecuteReaderAsync(ct);
            if(!await row.ReadAsync(ct)) return (null,StepRefusalCodes.StepNotStarted);
            if(!RuntimeCommandLogCodec.TryDeserialize(row.GetString(0),out existingLogs)) return (null,StepRefusalCodes.StepLogsInvalid);
        }
        var serialized=JsonSerializer.Serialize(RuntimeCommandJournal.Merge(existingLogs,commands),Json);
        return System.Text.Encoding.UTF8.GetByteCount(serialized)>524288?(null,StepRefusalCodes.JournalTooLarge):(serialized,null);
    }

    /// <summary>Marque l'étape complétée puis enregistre les métadonnées ; retourne un code si l'étape n'était plus ouverte.</summary>
    private static async Task<string?> PersistCompletionAsync(Guid id,Guid worker,CompleteRuntimeStep request,string serialized,string commandLogs,
        DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        var cp=request.Checkpoint;
        await using var update=Sql.Command("""
          UPDATE execution_steps SET completed_at=(strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z'),completed_revision=@completed_revision,checkpoint=@checkpoint,command_logs=@commands,
            completed_epoch=@epoch,completed_worker_id=@worker
          WHERE execution_id=@id AND flow_node_id=@node AND invocation_id=@invocation AND completed_at IS NULL RETURNING flow_node_id
          """,c,tx);
        update.Parameters.AddWithValue("completed_revision",request.ExpectedRevision+1); update.Parameters.AddWithValue("id",id);
        update.Parameters.AddWithValue("node",cp.NodeId); update.Parameters.AddWithValue("invocation",cp.InvocationId);
        update.Parameters.AddWithValue("checkpoint",SqlKind.Json,serialized);
        update.Parameters.AddWithValue("epoch",request.Epoch); update.Parameters.AddWithValue("worker",worker);
        update.Parameters.AddWithValue("commands",SqlKind.Json,commandLogs);
        update.Parameters.AddWithValue("before",SqlKind.Json,(object?)cp.Context.BeforeMetadata?.GetRawText()??DBNull.Value);
        update.Parameters.AddWithValue("after",SqlKind.Json,(object?)cp.Context.CurrentMetadata?.GetRawText()??DBNull.Value);
        if(await update.ExecuteScalarAsync(ct) is null) return StepRefusalCodes.StepAlreadyCompleted;
        await using var metadata=Sql.Command("UPDATE execution_work SET before_metadata=@before,after_metadata=@after WHERE execution_id=@id",c,tx);
        metadata.Parameters.AddWithValue("id",id);
        metadata.Parameters.AddWithValue("before",SqlKind.Json,(object?)cp.Context.BeforeMetadata?.GetRawText()??DBNull.Value);
        metadata.Parameters.AddWithValue("after",SqlKind.Json,(object?)cp.Context.CurrentMetadata?.GetRawText()??DBNull.Value);
        await metadata.ExecuteNonQueryAsync(ct);
        return null;
    }

    private static async Task YieldToHandoffAsync(Guid id,Guid worker,long epoch,RuntimeHandoffSelector handoff,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        await using var yield=Sql.Command("""
          UPDATE execution_work SET handoff_pending=true,handoff_selector=@selector,previous_worker_id=@worker,
            worker_id=NULL,lease_until=NULL,lease_epoch=lease_epoch+1 WHERE execution_id=@id;
          UPDATE media_reservations SET lease_epoch=@epoch+1,lease_until='9999-12-31 23:59:59.999999Z' WHERE execution_id=@id;
          DELETE FROM disk_locks WHERE execution_id=@id;
          """,c,tx);
        yield.Parameters.AddWithValue("id",id); yield.Parameters.AddWithValue("worker",worker); yield.Parameters.AddWithValue("epoch",epoch);
        yield.Parameters.AddWithValue("selector",SqlKind.Json,JsonSerializer.Serialize(handoff,Json));
        await yield.ExecuteNonQueryAsync(ct);
    }
}
