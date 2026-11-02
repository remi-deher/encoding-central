using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using Encoding.Contracts.Executions;

namespace Encoding.Api.Executions;

public sealed class RuntimeTransferRepository(DbDataSource dataSource,ExecutionStreamBroadcaster? broadcaster=null,ClusterSourceWriteBarrier? sourceWrites=null,TaskOwnershipRepository? ownership=null,
    Encoding.Api.Flows.FlowComponentRegistry? components=null)
{
    public async Task<IReadOnlyList<RuntimeTransfer>> ListAsync(Guid node,CancellationToken ct)
    {
        var deferPolicy=sourceWrites is not null && await sourceWrites.IsClusteredAsync(ct);
        await using var cmd=dataSource.CreateCommand("""
          SELECT w.execution_id,w.lease_epoch,a.local_path,w.media_path,w.source_sha,w.result_sha,w.result_extension,w.publication_requested,e.flow_definition::text,a.can_write,w.replacement_grace_hours,w.library_id,
            (SELECT checkpoint::text FROM execution_steps WHERE execution_id=w.execution_id AND completed_at IS NOT NULL ORDER BY completed_revision DESC LIMIT 1),
            w.media_policy<>'beside' AND EXISTS(SELECT 1 FROM library_replacement_policies p WHERE p.library_id=w.library_id AND p.enabled),
            (SELECT node_id FROM execution_steps s WHERE s.execution_id=w.execution_id ORDER BY (s.completed_at IS NULL) DESC,s.started_at DESC LIMIT 1),
            (SELECT invocation_id FROM execution_steps s WHERE s.execution_id=w.execution_id ORDER BY (s.completed_at IS NULL) DESC,s.started_at DESC LIMIT 1)
          FROM execution_work w JOIN executions e ON e.id=w.execution_id
          JOIN library_node_accesses a ON a.library_id=w.library_id AND a.node_id=w.source_node_id
          WHERE w.source_node_id=@node AND w.lease_until>clock_timestamp() AND e.state='running'
          AND a.mode='direct' AND a.is_enabled AND a.can_read AND a.local_path IS NOT NULL
          AND (w.source_sha IS NULL AND w.source_preparation_requested OR (w.publication_requested AND w.published_path IS NULL AND a.can_write))
          ORDER BY e.queued_at LIMIT 20
          """);
        cmd.Parameters.AddWithValue("node",node); await using var r=await cmd.ExecuteReaderAsync(ct); var result=new List<RuntimeTransfer>();
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        while (await r.ReadAsync(ct))
        {
            var flow = System.Text.Json.JsonSerializer.Deserialize<Encoding.Contracts.Flows.FlowDefinition>(r.GetString(8), json)!;
            var inputs = flow.Nodes
                .Where(n => n.Configuration.TryGetValue("sourceFile", out var file)
                    && file.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(file.GetString()))
                .Where(n => !n.Configuration["sourceFile"].GetString()!.StartsWith(Encoding.Contracts.Flows.FlowArtifactReference.Prefix, StringComparison.Ordinal))
                .Select(n => RuntimeRepository.NormalizePath(n.Configuration["sourceFile"].GetString())).Distinct().ToArray();
            var observation = r.IsDBNull(12) ? null : System.Text.Json.JsonSerializer.Deserialize<RuntimeCheckpoint>(r.GetString(12), json)?.Context.DirectSource;
            result.Add(new(r.GetGuid(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.GetBoolean(7), inputs, flow.MediaType,
                r.GetBoolean(9), deferPolicy || r.IsDBNull(10) ? null : r.GetInt32(10), r.GetGuid(11),
                Observation: observation,
                VerifyPublicationAccess: components is not null && flow.Nodes.Any(n => components.Resolve(n.Component)?.RequiresWriteAccess == true),
                VerifyReplacementAccess: r.GetBoolean(13),
                PublicationStepNodeId: r.IsDBNull(14) ? null : r.GetString(14),
                PublicationStepInvocationId: r.IsDBNull(15) ? null : r.GetGuid(15)));
        }
        return result;
    }
    public async Task<bool> ConfirmAsync(Guid id,Guid source,ConfirmRuntimePublication request,CancellationToken ct)
    {
        if(request.PublishedPath is null || request.PublishedPath.Length>2048 || !RuntimeArtifactStore.ValidDigest(request.Sha256)) return false;
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        if(!await RuntimeRepository.FenceAsync(id,source,request.Epoch,c,tx,ct,true)) return false;
        Guid worker; Guid library; string original; string sourceSha; string resultSha; int? graceHours; string mediaPolicy; bool continueFlow;
        await using(var read=Sql.Command("SELECT worker_node_id,library_id,media_path,source_sha,result_sha,replacement_grace_hours,media_policy,continue_after_publication FROM execution_work WHERE execution_id=@id AND result_sha=@sha AND publication_requested AND @path='.encoding-results/'||execution_id::text||'/result'||result_extension",
            c,tx))
        {
            read.Parameters.AddWithValue("id",id); read.Parameters.AddWithValue("sha",request.Sha256); read.Parameters.AddWithValue("path",request.PublishedPath);
            await using var row=await read.ExecuteReaderAsync(ct); if(!await row.ReadAsync(ct)) return false;
            worker=row.GetGuid(0); library=row.GetGuid(1); original=row.GetString(2); sourceSha=row.GetString(3); resultSha=row.GetString(4);
                graceHours=row.IsDBNull(5)?null:row.GetInt32(5); mediaPolicy=row.GetString(6); continueFlow=row.GetBoolean(7);
        }
        var replacement=graceHours is null?null:request.Replacement;
        var deferred=false;
        if(graceHours is not null && replacement is null && sourceWrites is not null && await sourceWrites.IsClusteredAsync(c,tx,ct))
        {
            await PendingSourceWriteRepository.EnqueueAsync(id,source,library,original,request.PublishedPath,sourceSha,resultSha,Path.GetExtension(request.PublishedPath),graceHours.Value,c,tx,ct);
            deferred=true;
        }
        if(replacement is not null && !SourceReplacementRepository.ValidReport(id,original,replacement)) return false;
        var detail=deferred?"Résultat validé et publié sans écrasement ; remplacement de source en attente durable du cluster.":replacement?.Status=="replaced"?"Résultat validé et publié ; source remplacée, sauvegarde conservée pendant la période de grâce."
            :graceHours is not null?"Résultat validé et publié ; remplacement non effectué, fichier source conservé"+(replacement?.Detail is {} reason?" : "+reason:"."):mediaPolicy=="beside"?"Résultat validé et publié à côté de la source (politique du flow) ; fichier source conservé."
            :mediaPolicy=="overwrite"?"Résultat validé et publié ; écrasement demandé par le flow mais la bibliothèque n’a pas de politique de remplacement active : fichier source conservé.":"Résultat validé et publié ; fichier source conservé.";
        // Publication demandée par un bloc de flow (ADR 0003) : l'exécution reste active, le worker reprend le flow sur le fichier publié.
        if(!await ExecutionRepository.RecordInTransactionAsync(id,new(request.EventId,request.ExpectedRevision,continueFlow
            || deferred?"running":"succeeded",worker,continueFlow || deferred?null:100,detail),c,tx,ct)) return false;
        if(replacement is not null) await SourceReplacementRepository.RecordInTransactionAsync(id,library,source,original,sourceSha,resultSha,graceHours!.Value,replacement,c,tx,ct);
        await using var update=Sql.Command("UPDATE execution_work SET published_path=@path WHERE execution_id=@id",c,tx);
        update.Parameters.AddWithValue("id",id); update.Parameters.AddWithValue("path",request.PublishedPath); await update.ExecuteNonQueryAsync(ct);
        await using var advance=Sql.Command("UPDATE executions SET revision=revision+1,updated_at=clock_timestamp() WHERE id=@id RETURNING revision",c,tx);
        advance.Parameters.AddWithValue("id",id);
        var eventRevision=await advance.ScalarInt32Async(ct)??throw new InvalidOperationException("Révision d’exécution absente après publication.");
        await using(var published=Sql.Command("INSERT INTO execution_events(id,execution_id,revision,state,detail) VALUES(@event,@id,@revision,'media.published',@detail)",c,tx))
        {
            published.Parameters.AddWithValue("event",Guid.NewGuid()); published.Parameters.AddWithValue("id",id); published.Parameters.AddWithValue("revision",eventRevision);
            published.Parameters.AddWithValue("detail","Média publié : "+request.PublishedPath+" (SHA-256 : "+request.Sha256+").");
            await published.ExecuteNonQueryAsync(ct);
        }
        if(deferred && !continueFlow)
        {
            await using var pause=Sql.Command("""
              UPDATE execution_work SET worker_node_id=NULL,lease_until=NULL,lease_epoch=lease_epoch+1,available_at='infinity'::timestamptz WHERE execution_id=@id;
              UPDATE media_reservations SET lease_epoch=lease_epoch+1,lease_until='infinity'::timestamptz WHERE execution_id=@id;
              DELETE FROM disk_locks WHERE execution_id=@id;
              """,c,tx);
            pause.Parameters.AddWithValue("id",id);await pause.ExecuteNonQueryAsync(ct);
        }
        else if(!continueFlow) await RuntimeRepository.ReleaseAsync(id,c,tx,ct);
        await tx.CommitAsync(ct); broadcaster?.Publish(id); return true;
    }
    /// <summary>Publication déjà confirmée par le nœud d'accès pour une exécution dont le flow continue (ADR 0003).</summary>
    public async Task<RuntimePublicationState?> PublicationStateAsync(Guid id,CancellationToken ct)
    {
        await using var cmd=dataSource.CreateCommand("SELECT published_path,result_sha,publication_requested FROM execution_work WHERE execution_id=@id");
        cmd.Parameters.AddWithValue("id",id); await using var r=await cmd.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct)) return null;
        var requested=r.GetBoolean(2);
        return r.IsDBNull(0)?new(false,null,r.IsDBNull(1)?null:r.GetString(1),requested):new(true,r.GetString(0),r.IsDBNull(1)?null:r.GetString(1),requested);
    }
    internal static async Task<bool> HasIntegrityProofAsync(Guid id, string sha, DbConnection c, DbTransaction tx, CancellationToken ct)
    {
        await using var readFlow = Sql.Command("SELECT flow_definition FROM executions WHERE id=@id", c, tx);
        readFlow.Parameters.AddWithValue("id", id);
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var flow = System.Text.Json.JsonSerializer.Deserialize<Encoding.Contracts.Flows.FlowDefinition>((string)(await readFlow.ExecuteScalarAsync(ct))!, json)!;
        if (!Encoding.Contracts.Flows.MediaIntegrityPolicy.IsExplicit(flow)) return true;
        var checks = flow.Nodes.Where(node => node.Component.PluginId == "probe"
            && node.Component.ComponentId == Encoding.Contracts.Flows.MediaIntegrityPolicy.Component).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        await using var read = Sql.Command("SELECT node_id,checkpoint FROM execution_steps WHERE execution_id=@id AND completed_at IS NOT NULL", c, tx);
        read.Parameters.AddWithValue("id", id);
        await using var reader = await read.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!checks.Contains(reader.GetString(0)) || reader.IsDBNull(1)) continue;
            var checkpoint = System.Text.Json.JsonSerializer.Deserialize<RuntimeCheckpoint>(reader.GetString(1), json);
            if (checkpoint is { OutputId: "valid" } && checkpoint.Context.CurrentSha256 == sha) return true;
        }
        return false;
    }

    public async Task<bool> FinishAsync(Guid id,Guid worker,FinishRuntimeExecution request,CancellationToken ct,bool sourceFailure=false)
    {
        if(request.State is not(ExecutionStates.Succeeded or ExecutionStates.Failed or ExecutionStates.Cancelled or ExecutionStates.Deferred) || request.Detail is null || request.Detail.Length>4000) return false;
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        if(!await RuntimeRepository.FenceAsync(id,worker,request.Epoch,c,tx,ct,sourceFailure)) return false;
        if (!await RuntimeCommandJournal.StoreAsync(id, request.Commands, c, tx, ct)) return false;
        if(request.State=="cancelled")
        {
            await using var cancellation=Sql.Command("SELECT state='cancelling' FROM executions WHERE id=@id",c,tx); cancellation.Parameters.AddWithValue("id",id);
            if(!(await cancellation.ScalarBoolAsync(ct))) return false;
        }
        if(sourceFailure)
        {
            if(request.State!="failed") return false;
            await using var read=Sql.Command("SELECT worker_node_id FROM execution_work WHERE execution_id=@id",c,tx); read.Parameters.AddWithValue("id",id);
                worker=(await read.ScalarGuidAsync(ct))!.Value;
        }
        if(request.ContinueFlow)
        {
            // Publication au milieu du flow : état « succeeded » avec un résultat, une seule fois, sans fin de flow.
            if(sourceFailure || request.State!=ExecutionStates.Succeeded || request.ResultSha256 is null || request.BeforeMetadata is null || request.AfterMetadata is null) return false;
            await using var already=Sql.Command("SELECT publication_requested FROM execution_work WHERE execution_id=@id",c,tx); already.Parameters.AddWithValue("id",id);
            if(await already.ScalarBoolAsync(ct)) return false;
        }
        else if(request.State is ExecutionStates.Succeeded or ExecutionStates.Deferred)
        {
            await using var reached=Sql.Command("SELECT checkpoint::text FROM execution_steps WHERE execution_id=@id AND completed_at IS NOT NULL ORDER BY completed_revision DESC LIMIT 1",c,tx);
            reached.Parameters.AddWithValue("id",id);
            if(await reached.ExecuteScalarAsync(ct) is not string json) return false;
            var cp=System.Text.Json.JsonSerializer.Deserialize<RuntimeCheckpoint>(json,new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
            if(cp.NextNodeId is not null || cp.OutputId==ExecutionOutcomes.ErrorOutput || cp.TerminalResult==ExecutionOutcomes.Failed) return false;
            if(request.State==ExecutionStates.Deferred && (cp.TerminalResult!=ExecutionOutcomes.Deferred || request.DeferMinutes is not >=1 or >1440)) return false;
            if(request.BeforeMetadata is null || request.AfterMetadata is null) return false;
        }
        await using(var metadata=Sql.Command("UPDATE execution_work SET before_metadata=COALESCE(@before,before_metadata),after_metadata=COALESCE(@after,after_metadata) WHERE execution_id=@id",c,tx))
        {
            metadata.Parameters.AddWithValue("id",id);
            metadata.Parameters.AddWithValue("before",SqlKind.Json,(object?)request.BeforeMetadata?.GetRawText()??DBNull.Value);
            metadata.Parameters.AddWithValue("after",SqlKind.Json,(object?)request.AfterMetadata?.GetRawText()??DBNull.Value);
            await metadata.ExecuteNonQueryAsync(ct);
        }
        if(request.State==ExecutionStates.Succeeded && request.ResultSha256 is { } sha)
        {
            if(!RuntimeArtifactStore.ValidDigest(sha) || request.Extension is null || !System.Text.RegularExpressions.Regex.IsMatch(request.Extension,"^\\.[a-zA-Z0-9]{1,10}$")) return false;
            if (!await HasIntegrityProofAsync(id, sha, c, tx, ct)) return false;
            // Un résultat déjà publié par un bloc du flow ne peut pas être remplacé par un second.
            await using(var published=Sql.Command("SELECT published_path IS NOT NULL FROM execution_work WHERE execution_id=@id",c,
                tx)) { published.Parameters.AddWithValue("id",id); if(await published.ScalarBoolAsync(ct)) return false; }
            await using var set=Sql.Command("""
              UPDATE execution_work w SET result_sha=@sha,result_extension=@extension,publication_requested=true,continue_after_publication=@continue,
              replacement_grace_hours=CASE WHEN w.media_policy='beside' THEN NULL ELSE (SELECT p.grace_period_hours FROM library_replacement_policies p WHERE p.library_id=w.library_id AND p.enabled) END
              WHERE execution_id=@id AND EXISTS(SELECT 1 FROM execution_artifacts a WHERE a.execution_id=w.execution_id AND a.sha256=@sha)
              AND EXISTS(SELECT 1 FROM library_node_accesses a WHERE a.library_id=w.library_id AND a.node_id=w.source_node_id AND a.is_enabled AND a.can_write)
              """,c,tx);
            set.Parameters.AddWithValue("id",id); set.Parameters.AddWithValue("sha",sha); set.Parameters.AddWithValue("extension",request.Extension);
                set.Parameters.AddWithValue("continue",request.ContinueFlow);
            if(await set.ExecuteNonQueryAsync(ct)!=1) return false;
            if(!await ExecutionRepository.RecordInTransactionAsync(id,new(request.EventId,request.ExpectedRevision,"running",worker,null,
                "Résultat validé ; publication demandée au nœud d’accès."),c,tx,ct)) return false;
        }
        else
        {
            if(!await ExecutionRepository.RecordInTransactionAsync(id,new(request.EventId,request.ExpectedRevision,request.State,worker,
                request.State==ExecutionStates.Succeeded?100:null,request.Detail),c,tx,ct)) return false;
            await RuntimeRepository.ReleaseAsync(id,c,tx,ct);
            if(request.State==ExecutionStates.Deferred)
            {
                var next=Guid.NewGuid();
                await using var schedule=Sql.Command("""
                  INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt,retry_of,detail)
                  SELECT @next,media,library_name,flow_name,flow_version,flow_definition,'queued',attempt+1,id,'Nouvelle tentative planifiée après report.' FROM executions WHERE id=@id;
                  INSERT INTO execution_work(execution_id,library_id,media_path,media_key,available_at)
                  SELECT @next,library_id,media_path,media_key,clock_timestamp()+@delay FROM execution_work WHERE execution_id=@id;
                  INSERT INTO execution_events(id,execution_id,revision,state,detail) VALUES(@event,@next,0,'queued','Nouvelle tentative planifiée après report.');
                  """,c,tx);
                schedule.Parameters.AddWithValue("id",id); schedule.Parameters.AddWithValue("next",next); schedule.Parameters.AddWithValue("event",Guid.NewGuid());
                schedule.Parameters.AddWithValue("delay",TimeSpan.FromMinutes(request.DeferMinutes!.Value)); await schedule.ExecuteNonQueryAsync(ct);
                if(ownership is not null) await ownership.CreateAsync([next],c,tx,ct);
            }
        }
        await tx.CommitAsync(ct); return true;
    }
}
