using Encoding.Api.Infrastructure.Data;
using System.Data.Common;

using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Api.Nodes;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Libraries;

namespace Encoding.Api.Libraries;

public sealed class LibraryWorkflowRepository(DbDataSource dataSource,FlowDefinitionValidator validator,NodePresenceOptions presence,
    ExecutionStreamBroadcaster? broadcaster = null,TaskOwnershipRepository? ownership=null,LocalNodeRegistry? localNode=null)
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public Task<LibraryWorkflowResponse> GetAsync(Guid library,CancellationToken ct)=>GetAsync(library,null,null,null,ct);
    public async Task<LibraryWorkflowResponse> GetAsync(Guid library,string? search,int? limit,int? offset,CancellationToken ct)
    {
        var effectiveLimit=Math.Clamp(limit is null or <=0?10000:limit.Value,1,10000);
        var effectiveOffset=Math.Max(0,offset??0);
        var normalizedSearch=string.IsNullOrWhiteSpace(search)?null:search.Trim().ToLowerInvariant();
        LibraryFlowAssignment? assignment=null; LibraryScanStatus? scan=null; int revision=0;
        await using(var cmd=dataSource.CreateCommand("SELECT l.flow_assignment_revision,a.flow_id,a.flow_version,a.revision,v.definition->>'name' FROM libraries l LEFT JOIN library_flow_assignments a ON a.library_id=l.id LEFT JOIN flow_versions v ON v.flow_id=a.flow_id AND v.version=a.flow_version WHERE l.id=@id AND NOT l.is_deleted"))
        {
            cmd.Parameters.AddWithValue("id",library); await using var r=await cmd.ExecuteReaderAsync(ct);
            if(await r.ReadAsync(ct)) {revision=r.GetInt32(0); if(!r.IsDBNull(1)) assignment=new(r.GetGuid(1),r.GetInt32(2),r.GetInt32(3),r.GetString(4));}
        }
        await using(var cmd=dataSource.CreateCommand("SELECT id,state,detail,completed_at FROM library_scans WHERE library_id=@id ORDER BY created_at DESC,id DESC LIMIT 1"))
        {
            cmd.Parameters.AddWithValue("id",library); await using var r=await cmd.ExecuteReaderAsync(ct);
            if(await r.ReadAsync(ct)) scan=new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.IsDBNull(3)?null:r.GetFieldValue<DateTimeOffset>(3));
        }
        var searchFilter=normalizedSearch is not null?" AND lower(relative_path) LIKE @search":string.Empty;
        int totalFiles;
        await using(var countCmd=dataSource.CreateCommand($"SELECT count(*)::integer FROM library_media WHERE library_id=@id{searchFilter}"))
        {
            countCmd.Parameters.AddWithValue("id",library);
            if(normalizedSearch is not null) countCmd.Parameters.AddWithValue("search",$"%{normalizedSearch}%");
            totalFiles=await countCmd.ScalarInt32Async(ct)??0;
        }
        var files=new List<LibraryMediaFile>();
        await using(var cmd=dataSource.CreateCommand($"SELECT id,relative_path,byte_length,modified_at FROM library_media WHERE library_id=@id{searchFilter} ORDER BY relative_path,id LIMIT @limit OFFSET @offset"))
        {
            cmd.Parameters.AddWithValue("id",library);
            if(normalizedSearch is not null) cmd.Parameters.AddWithValue("search",$"%{normalizedSearch}%");
            cmd.Parameters.AddWithValue("limit",effectiveLimit);
            cmd.Parameters.AddWithValue("offset",effectiveOffset);
            await using var r=await cmd.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) files.Add(new(r.GetGuid(0),r.GetString(1),r.GetInt64(2),r.GetFieldValue<DateTimeOffset>(3)));
        }
        return new(assignment,scan,files,revision,totalFiles,effectiveLimit,effectiveOffset);
    }
    public async Task<bool> AssignAsync(Guid library,AssignLibraryFlowRequest request,CancellationToken ct)
    {
        if(request.ExpectedRevision<0 || request.ExpectedRevision==int.MaxValue || (request.FlowId is null)!=(request.Version is null)) return false;
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        var retargeted=false;
        if(!await LockLibraryAsync(library,c,tx,ct)) return false;
        await using var current=Sql.Command("SELECT flow_assignment_revision FROM libraries WHERE id=@id",c,tx);
        current.Parameters.AddWithValue("id",library);
        if((await current.ScalarInt32Async(ct) ?? 0)!=request.ExpectedRevision) return false;
        if(request.FlowId is {} flow)
        {
            await using var version=Sql.Command("SELECT definition::text FROM flow_versions WHERE flow_id=@flow AND version=@version",c,tx);
            version.Parameters.AddWithValue("flow",flow); version.Parameters.AddWithValue("version",request.Version!.Value);
            if(await version.ExecuteScalarAsync(ct) is not string document || !validator.Validate(JsonSerializer.Deserialize<FlowDefinition>(document,Json)!).IsValid) return false;
            await using var save=Sql.Command("INSERT INTO library_flow_assignments(library_id,flow_id,flow_version,revision) VALUES(@id,@flow,@version,@revision) ON CONFLICT(library_id) DO UPDATE SET flow_id=@flow,flow_version=@version,revision=@revision",
                c,tx);
            save.Parameters.AddWithValue("id",library); save.Parameters.AddWithValue("flow",flow); save.Parameters.AddWithValue("version",
                request.Version.Value); save.Parameters.AddWithValue("revision",request.ExpectedRevision+1);
            await save.ExecuteNonQueryAsync(ct);
            retargeted=await RetargetPendingAsync(library,JsonSerializer.Deserialize<FlowDefinition>(document,Json)!,document,c,tx,ct);
        }
        else
        {
            await using var delete=Sql.Command("DELETE FROM library_flow_assignments WHERE library_id=@id",c,tx);
            delete.Parameters.AddWithValue("id",library); await delete.ExecuteNonQueryAsync(ct);
        }
        await using var advance=Sql.Command("UPDATE libraries SET flow_assignment_revision=flow_assignment_revision+1 WHERE id=@id",c,tx);
        advance.Parameters.AddWithValue("id",library); await advance.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        if(retargeted) broadcaster?.Publish();
        return true;
    }
    /// <summary>
    /// Changer le flow d'une bibliothèque ne relance rien : les exécutions encore en file (jamais démarrées) passent au nouveau flow,
    /// et les fichiers déjà traités gardent leur état (reçus recopiés vers le nouveau flow pour que la surveillance ne les remette pas en file).
    /// </summary>
    private async Task<bool> RetargetPendingAsync(Guid library,FlowDefinition flow,string document,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        var moved=new List<(Guid Id,int Revision)>();
        await using(var update=Sql.Command($"UPDATE executions SET flow_name=@name,flow_version=@version,flow_definition=@definition,detail=@detail,updated_at=clock_timestamp(),revision=revision+1 WHERE state='queued' AND (flow_name<>@name OR flow_version<>@version) AND flow_definition->>'mediaType'=@mediaType AND id IN(SELECT execution_id FROM execution_work WHERE library_id=@library) AND NOT EXISTS(SELECT 1 FROM execution_steps s WHERE s.execution_id=executions.id) AND {TaskOwnershipRepository.ActiveSql("executions.id")} RETURNING id,revision",
            c,tx))
        {
            update.Parameters.AddWithValue("name",flow.Name); update.Parameters.AddWithValue("version",flow.Version); update.Parameters.AddWithValue("definition",SqlKind.Json,document);
            update.Parameters.AddWithValue("detail","Flow de la bibliothèque modifié : "+flow.Name+" v"+flow.Version+".");
                update.Parameters.AddWithValue("mediaType",flow.MediaType); update.Parameters.AddWithValue("library",library);
            await using var r=await update.ExecuteReaderAsync(ct); while(await r.ReadAsync(ct)) moved.Add((r.GetGuid(0),r.GetInt32(1)));
        }
        foreach(var (id,revision) in moved) await ExecutionRepository.AddEvent(c,tx,id,Guid.NewGuid(),revision,"queued","Flow de la bibliothèque modifié : "+flow.Name+" v"+flow.Version+".",ct);
        await using var inherit=Sql.Command("INSERT INTO library_processing_receipts(library_id,file_id,flow_id,flow_version,byte_length,modified_at,execution_id) SELECT DISTINCT r.library_id,r.file_id,@flow,@version,r.byte_length,r.modified_at,r.execution_id FROM library_processing_receipts r JOIN library_media m ON m.id=r.file_id AND m.byte_length=r.byte_length AND m.modified_at=r.modified_at WHERE r.library_id=@library AND NOT EXISTS(SELECT 1 FROM library_processing_receipts n WHERE n.library_id=r.library_id AND n.file_id=r.file_id AND n.flow_id=@flow AND n.flow_version=@version AND n.byte_length=r.byte_length AND n.modified_at=r.modified_at) ON CONFLICT DO NOTHING",
            c,tx);
        inherit.Parameters.AddWithValue("flow",flow.Id); inherit.Parameters.AddWithValue("version",flow.Version); inherit.Parameters.AddWithValue("library",library);
        await inherit.ExecuteNonQueryAsync(ct);
        return moved.Count>0;
    }
    private static async Task<bool> LockLibraryAsync(Guid id,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        await using var cmd=Sql.Command("SELECT id FROM libraries WHERE id=@id AND NOT is_deleted FOR UPDATE",c,tx);
        cmd.Parameters.AddWithValue("id",id); return await cmd.ScalarGuidAsync(ct) is not null;
    }
    public async Task<Guid?> RequestScanAsync(Guid library,CancellationToken ct)
    {
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        if(!await LockLibraryAsync(library,c,tx,ct)) return null;
        await using var type=Sql.Command("SELECT v.definition->>'mediaType' FROM library_flow_assignments a JOIN flow_versions v ON v.flow_id=a.flow_id AND v.version=a.flow_version JOIN libraries l ON l.id=a.library_id WHERE a.library_id=@id AND l.is_active",
            c,tx);
        type.Parameters.AddWithValue("id",library); if(await type.ExecuteScalarAsync(ct) is not string mediaType) return null;
        await using var active=Sql.Command("SELECT id FROM library_scans WHERE library_id=@id AND state IN ('queued','running')",c,tx);
        active.Parameters.AddWithValue("id",library);
        if(await active.ScalarGuidAsync(ct) is {} existing) return existing;
        var id=Guid.NewGuid();
        await using var cmd=Sql.Command("INSERT INTO library_scans(id,library_id,media_type) VALUES(@id,@library,@type)",c,tx);
        cmd.Parameters.AddWithValue("id",id); cmd.Parameters.AddWithValue("library",library); cmd.Parameters.AddWithValue("type",mediaType);
        await cmd.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct); return id;
    }
    public async Task<LibraryScanLease?> ClaimScanAsync(Guid node,CancellationToken ct)
    {
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        await using var cmd=Sql.Command("""
          SELECT s.id,s.library_id,s.epoch,a.local_path,s.media_type FROM library_scans s
          JOIN libraries l ON l.id=s.library_id AND l.is_active
          JOIN library_node_accesses a ON a.library_id=s.library_id AND a.node_id=@node AND a.mode='direct' AND a.is_enabled AND a.can_read AND a.local_path IS NOT NULL
          JOIN cluster_nodes n ON n.id=a.node_id AND n.deleted_at IS NULL AND n.accepts_work AND n.last_seen_at>clock_timestamp()-@offline
          WHERE s.state IN ('queued','running') AND (s.lease_until IS NULL OR s.lease_until<=clock_timestamp())
          ORDER BY s.created_at LIMIT 1 FOR UPDATE OF s SKIP LOCKED
          """,c,tx);
        cmd.Parameters.AddWithValue("node",node); cmd.Parameters.AddWithValue("offline",presence.OfflineAfter);
        await using var r=await cmd.ExecuteReaderAsync(ct); if(!await r.ReadAsync(ct)) return null;
        var lease=new LibraryScanLease(r.GetGuid(0),r.GetGuid(1),checked(r.GetInt64(2)+1),r.GetString(3),r.GetString(4)); await r.DisposeAsync();
        await using var update=Sql.Command("UPDATE library_scans SET node_id=@node,source_root=@root,epoch=@epoch,state='running',lease_until=clock_timestamp()+interval '180 seconds',detail='Découverte des fichiers en cours.' WHERE id=@id",
            c,tx);
        update.Parameters.AddWithValue("root",lease.Root); update.Parameters.AddWithValue("id",lease.Id); update.Parameters.AddWithValue("node",node);
            update.Parameters.AddWithValue("epoch",lease.Epoch);
        await update.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct); return lease;
    }
    public async Task<bool> RenewScanAsync(Guid id,Guid node,long epoch,CancellationToken ct)
    {
        await using var cmd=dataSource.CreateCommand("""
          UPDATE library_scans s SET lease_until=clock_timestamp()+interval '180 seconds'
          WHERE s.id=@id AND s.node_id=@node AND s.epoch=@epoch AND s.state='running' AND s.lease_until>clock_timestamp()
          AND EXISTS(SELECT 1 FROM library_node_accesses a JOIN libraries l ON l.id=a.library_id WHERE a.library_id=s.library_id AND a.node_id=@node AND a.mode='direct' AND a.is_enabled AND a.can_read AND a.local_path=s.source_root AND l.is_active)
          """);
        cmd.Parameters.AddWithValue("id",id); cmd.Parameters.AddWithValue("node",node); cmd.Parameters.AddWithValue("epoch",epoch);
        return await cmd.ExecuteNonQueryAsync(ct)==1;
    }
    public async Task<bool> CompleteScanAsync(Guid id,Guid node,CompleteLibraryScan request,CancellationToken ct)
    {
        if(request.Files is null || request.Files.Count>10000 || request.Error?.Length>512) return false;
        var files=request.Files.Select(file=>file with {RelativePath=MediaPathRules.Normalize(file.RelativePath)}).ToArray();
        if(files.Any(file=>file.Bytes<0) || files.Select(file=>file.RelativePath).Distinct().Count()!=files.Length) return false;
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        await using var cmd=Sql.Command("""
          SELECT s.library_id,s.media_type FROM library_scans s
          JOIN library_node_accesses a ON a.library_id=s.library_id AND a.node_id=@node AND a.mode='direct' AND a.is_enabled AND a.can_read AND a.local_path=s.source_root
          JOIN libraries l ON l.id=s.library_id AND l.is_active
          WHERE s.id=@id AND s.node_id=@node AND s.epoch=@epoch AND s.state='running' AND s.lease_until>clock_timestamp()
          FOR UPDATE OF s
          """,c,tx);
        cmd.Parameters.AddWithValue("id",id); cmd.Parameters.AddWithValue("node",node); cmd.Parameters.AddWithValue("epoch",request.Epoch);
        await using var r=await cmd.ExecuteReaderAsync(ct); if(!await r.ReadAsync(ct)) return false;
        var library=r.GetGuid(0); var type=r.GetString(1); await r.DisposeAsync();
        if(files.Any(file=>!LibraryMediaTypes.Matches(file.RelativePath,type))) return false;
        if(request.Error is null)
        {
            // Use portable VALUES batches instead of provider-specific JSON table functions.
            // Keep batches small enough for SQLite's bind limit.
            const int batchSize = 200;
            for (var offset = 0; offset < files.Length; offset += batchSize)
            {
                var batch = files.Skip(offset).Take(batchSize).ToArray();
                var values = new string[batch.Length];
                await using var save = Sql.Command("", c, tx);
                save.Parameters.AddWithValue("library", library);
                save.Parameters.AddWithValue("scan", id);
                for (var index = 0; index < batch.Length; index++)
                {
                    var file = batch[index];
                    values[index] = $"(@id{index},@library,@path{index},@bytes{index},@modified{index},@scan)";
                    save.Parameters.AddWithValue($"id{index}", Guid.NewGuid());
                    save.Parameters.AddWithValue($"path{index}", file.RelativePath);
                    save.Parameters.AddWithValue($"bytes{index}", file.Bytes);
                    save.Parameters.AddWithValue($"modified{index}", file.ModifiedAt);
                }
                save.CommandText = $"""
                  INSERT INTO library_media(id,library_id,relative_path,byte_length,modified_at,scan_id)
                  VALUES {string.Join(",", values)}
                  ON CONFLICT(library_id,relative_path) DO UPDATE
                  SET stable_since=CASE WHEN library_media.byte_length=EXCLUDED.byte_length AND library_media.modified_at=EXCLUDED.modified_at THEN library_media.stable_since ELSE clock_timestamp() END,byte_length=EXCLUDED.byte_length,modified_at=EXCLUDED.modified_at,scan_id=EXCLUDED.scan_id
                  """;
                await save.ExecuteNonQueryAsync(ct);
            }
            await using var remove=Sql.Command("DELETE FROM library_media WHERE library_id=@library AND scan_id<>@scan",c,tx);
            remove.Parameters.AddWithValue("library",library); remove.Parameters.AddWithValue("scan",id); await remove.ExecuteNonQueryAsync(ct);
        }
        await using var done=Sql.Command("UPDATE library_scans SET state=@state,detail=@detail,completed_at=clock_timestamp(),lease_until=NULL WHERE id=@id AND lease_until>clock_timestamp()",c,tx);
        done.Parameters.AddWithValue("id",id); done.Parameters.AddWithValue("state",request.Error is null?"completed":"failed");
        done.Parameters.AddWithValue("detail",request.Error??$"{files.Length} fichier(s) détecté(s).");
        if(await done.ExecuteNonQueryAsync(ct)!=1) return false; await tx.CommitAsync(ct); return true;
    }
    public Task<(int Queued,int Skipped)?> EnqueueAsync(Guid library,IReadOnlyList<Guid> ids,int assignmentRevision,CancellationToken ct)=>QueueAsync(library,ids,assignmentRevision,false,ct);
    public Task<(int Queued,int Skipped)?> EnqueueConfirmedAsync(Guid library,IReadOnlyList<Guid> ids,int assignmentRevision,Guid confirmationId,CancellationToken ct)
    {
        if (confirmationId == Guid.Empty) throw new ArgumentException("Identifiant de confirmation invalide.", nameof(confirmationId));
        return QueueAsync(library,ids,assignmentRevision,false,ct,actionId:confirmationId);
    }
    public async Task EnqueueAutomaticallyAsync(Guid library,CancellationToken ct)=>await QueueAsync(library,[],null,true,ct);
    /// <summary>Mise en file déclenchée par un service externe : fichiers choisis, flow actuellement attribué, sans retraiter une empreinte déjà reçue.</summary>
    public Task<(int Queued,int Skipped)?> EnqueueTriggeredAsync(Guid library,IReadOnlyList<Guid> ids,CancellationToken ct)=>QueueAsync(library,ids,null,false,ct,true);
    private async Task<(int Queued,int Skipped)?> QueueAsync(Guid library,IReadOnlyList<Guid> ids,int? assignmentRevision,bool automatic,CancellationToken ct,
        bool skipReceipted=false,Guid? actionId=null)
    {
        if(!automatic && (ids.Count is <1 or >10000 || ids.Distinct().Count()!=ids.Count)) return null;
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        if(!await LockLibraryAsync(library,c,tx,ct)) return null;
        await using var assignment=Sql.Command("SELECT v.definition::text FROM library_flow_assignments a JOIN flow_versions v ON v.flow_id=a.flow_id AND v.version=a.flow_version WHERE a.library_id=@id AND (@revision IS NULL OR a.revision=@revision) AND EXISTS(SELECT 1 FROM libraries l WHERE l.id=a.library_id AND l.is_active)",
            c,tx);
        assignment.Parameters.AddWithValue("id",library); assignment.Parameters.AddWithValue("revision",SqlKind.Integer,(object?)assignmentRevision??DBNull.Value);
        if(await assignment.ExecuteScalarAsync(ct) is not string json) return null;
        var flow=JsonSerializer.Deserialize<FlowDefinition>(json,Json)!; if(!validator.Validate(flow).IsValid) return null;
        if(automatic)
        {
            await using var enabled=Sql.Command("SELECT COALESCE((SELECT watch_enabled FROM library_processing_settings WHERE library_id=@id),true)",c,tx);
            enabled.Parameters.AddWithValue("id",library);if(!(await enabled.ScalarBoolAsync(ct)))return null;
            if(!await ServesLibraryAutomaticallyAsync(library,c,tx,ct))return null;
        }
        var files=new List<(Guid Id,string Path,long Bytes,DateTimeOffset Modified)>();
        await using(var select=Sql.Command("SELECT m.id,m.relative_path,m.byte_length,m.modified_at FROM library_media m WHERE m.library_id=@library           AND (NOT @automatic AND m.id=ANY(@ids) AND (NOT @skipReceipted OR NOT EXISTS(SELECT 1 FROM library_processing_receipts r WHERE r.library_id=m.library_id AND r.file_id=m.id AND r.flow_id=@flow AND r.flow_version=@version AND r.byte_length=m.byte_length AND r.modified_at=m.modified_at)) OR @automatic AND m.stable_since<=clock_timestamp()-interval '30 seconds' AND m.modified_at<=clock_timestamp()-interval '30 seconds'           AND NOT EXISTS(SELECT 1 FROM library_processing_receipts r WHERE r.library_id=m.library_id AND r.file_id=m.id AND r.flow_id=@flow AND r.flow_version=@version AND r.byte_length=m.byte_length AND r.modified_at=m.modified_at) AND NOT EXISTS(SELECT 1 FROM media_replacement_marks k WHERE k.library_id=m.library_id AND k.relative_path=m.relative_path AND k.byte_length=m.byte_length AND k.modified_at=m.modified_at))           ORDER BY m.relative_path LIMIT 10000",
            c,tx))
        {
            select.Parameters.AddWithValue("automatic",automatic);select.Parameters.AddWithValue("skipReceipted",skipReceipted);
                select.Parameters.AddWithValue("flow",flow.Id);select.Parameters.AddWithValue("version",flow.Version);select.Parameters.AddWithValue("library",
                library); select.Parameters.AddWithValue("ids",ids.ToArray());
            await using var r=await select.ExecuteReaderAsync(ct); while(await r.ReadAsync(ct)) files.Add((r.GetGuid(0),r.GetString(1),r.GetInt64(2),r.GetFieldValue<DateTimeOffset>(3)));
        }
        if(!automatic && files.Count!=ids.Count || files.Any(file=>!LibraryMediaTypes.Matches(file.Path,flow.MediaType))) return null;
        int queued=0;var assignments=new List<Guid>();
        foreach(var file in files)
        {
            var execution=actionId is { } confirmedAction ? ConfirmedExecutionId(confirmedAction,file.Id) : Guid.NewGuid();
            if(!await RuntimeRepository.EnqueueInTransactionAsync(new(execution,library,file.Path,flow.Id,flow.Version),flow,c,tx,ct,
                automatic ? "surveillance" : skipReceipted ? "trigger" : "manual"))continue;
            queued++;assignments.Add(execution);
            await using var receipt=Sql.Command("INSERT INTO library_processing_receipts(library_id,file_id,flow_id,flow_version,byte_length,modified_at,execution_id) VALUES(@library,@file,@flow,@version,@bytes,@modified,@execution) ON CONFLICT DO NOTHING",
                c,tx);
            receipt.Parameters.AddWithValue("library",library);receipt.Parameters.AddWithValue("file",file.Id);receipt.Parameters.AddWithValue("flow",flow.Id);
                receipt.Parameters.AddWithValue("version",flow.Version);
            receipt.Parameters.AddWithValue("bytes",file.Bytes);receipt.Parameters.AddWithValue("modified",file.Modified.ToUniversalTime());
                receipt.Parameters.AddWithValue("execution",execution);await receipt.ExecuteNonQueryAsync(ct);
        }
        if(ownership is not null) await ownership.CreateAsync(assignments,c,tx,ct);
        await tx.CommitAsync(ct);
        if(queued>0) broadcaster?.Publish();
        return(queued,files.Count-queued);
    }

    /// <summary>
    /// Un serveur ne met en file automatiquement que ce qu'il peut servir : si au moins un nœud serveur monte la bibliothèque en direct,
    /// le nœud de ce serveur doit en faire partie. Sinon l'exécution naîtrait chez un propriétaire sans accès à la source et resterait
    /// en file, alors qu'un autre serveur, qui détient l'accès direct, la met déjà en file. Sans accès direct nulle part, tout serveur peut le faire.
    /// </summary>
    private async Task<bool> ServesLibraryAutomaticallyAsync(Guid library,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        var node=localNode?.NodeId;
        if(node is null)
        {
            await using var cmd=Sql.Command("SELECT a.node_id FROM cluster_peer_admissions a JOIN task_server_identity s ON s.peer_id=a.peer_id WHERE a.revoked_at IS NULL AND a.node_id IS NOT NULL LIMIT 1",
                c,tx);
            node=await cmd.ScalarGuidAsync(ct);
        }
        if(node is null)
        {
            await using var cmd=Sql.Command("SELECT n.id FROM cluster_nodes n WHERE n.deleted_at IS NULL AND 'server'=ANY(n.roles) AND NOT EXISTS(SELECT 1 FROM cluster_peer_admissions a JOIN task_server_identity s ON s.peer_id<>a.peer_id WHERE a.node_id=n.id AND a.revoked_at IS NULL) ORDER BY n.created_at LIMIT 2",
                c,tx);
            var candidates=new List<Guid>();
            await using var cr=await cmd.ExecuteReaderAsync(ct);
            while(await cr.ReadAsync(ct)) candidates.Add(cr.GetGuid(0));
            if(candidates.Count==1) node=candidates[0];
        }
        if(node is not {} resolvedNode) return true;
        if(localNode?.NodeId is null) await localNode!.RegisterAsync(resolvedNode,ct);
        await using var access=Sql.Command("""
          SELECT EXISTS(SELECT 1 FROM library_node_accesses a JOIN cluster_nodes n ON n.id=a.node_id AND n.deleted_at IS NULL AND 'server'=ANY(n.roles)
                        WHERE a.library_id=@library AND a.mode='direct' AND a.is_enabled AND a.can_read),
                 EXISTS(SELECT 1 FROM library_node_accesses a WHERE a.library_id=@library AND a.node_id=@node AND a.mode='direct' AND a.is_enabled AND a.can_read)
          """,c,tx);
        access.Parameters.AddWithValue("library",library);access.Parameters.AddWithValue("node",resolvedNode);
        await using var r=await access.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct)) return true;
        var anyDirect=Convert.ToInt64(r.GetValue(0))!=0;var localDirect=Convert.ToInt64(r.GetValue(1))!=0;
        return !anyDirect || localDirect;
    }

    private static Guid ConfirmedExecutionId(Guid confirmationId, Guid fileId)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(confirmationId.ToString("N") + "/" + fileId.ToString("N")));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
