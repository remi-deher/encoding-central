using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Libraries;

namespace Encoding.Api.Executions;

/// <summary>Politique de remplacement des sources et registre durable des remplacements (voir l'architecture 0029).</summary>
public sealed class SourceReplacementRepository(DbDataSource dataSource,ClusterSourceWriteBarrier? sourceWrites=null)
{
    public static readonly TimeSpan RollbackReservation=TimeSpan.FromMinutes(15);
    public async Task<LibraryReplacementPolicy?> GetPolicyAsync(Guid library,CancellationToken ct)
    {
        await using var cmd=dataSource.CreateCommand("SELECT l.id,p.enabled,p.grace_period_hours,p.revision FROM libraries l LEFT JOIN library_replacement_policies p ON p.library_id=l.id WHERE l.id=@id");
        cmd.Parameters.AddWithValue("id",library); await using var r=await cmd.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct)) return null;
        return r.IsDBNull(1)?new(false,168,0):new(r.GetBoolean(1),r.GetInt32(2),r.GetInt32(3));
    }
    /// <returns>false si la bibliothèque est introuvable ou si la révision attendue est périmée.</returns>
    public async Task<bool> SavePolicyAsync(Guid library,SaveLibraryReplacementPolicy request,CancellationToken ct)
    {
        if(request.GracePeriodHours is <1 or >8760
            || request.ExpectedRevision is <0 or int.MaxValue) throw new ArgumentException("La période de grâce doit être comprise entre 1 heure et 8 760 heures.");
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        await using var locked=Sql.Command("SELECT id FROM libraries WHERE id=@id FOR UPDATE",c,tx); locked.Parameters.AddWithValue("id",library);
        if(await locked.ExecuteScalarAsync(ct) is null) return false;
        await using var current=Sql.Command("SELECT revision FROM library_replacement_policies WHERE library_id=@id",c,tx); current.Parameters.AddWithValue("id",library);
        if((await current.ScalarInt32Async(ct) ?? 0)!=request.ExpectedRevision) return false;
        await using var save=Sql.Command("""
          INSERT INTO library_replacement_policies(library_id,enabled,grace_period_hours,revision) VALUES(@id,@enabled,@hours,@revision)
          ON CONFLICT(library_id) DO UPDATE SET enabled=@enabled,grace_period_hours=@hours,revision=@revision
          """,c,tx);
        save.Parameters.AddWithValue("id",library); save.Parameters.AddWithValue("enabled",request.Enabled); save.Parameters.AddWithValue("hours",
            request.GracePeriodHours); save.Parameters.AddWithValue("revision",request.ExpectedRevision+1);
        await save.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct); return true;
    }

    internal static bool ValidReport(Guid execution,string original,SourceReplacementReport report)
    {
        if(report.Detail?.Length>2000) return false;
        if(report.Status=="failed") return report.FinalPath is null && report.BackupPath is null;
        if(report.Status!="replaced" || report.FinalPath is null || report.BackupPath is null || report.ByteLength<=0) return false;
        try
        {
            var normalizedOriginal=MediaPathRules.Normalize(original); var final=MediaPathRules.Normalize(report.FinalPath);
            var directory=normalizedOriginal.Contains('/')?normalizedOriginal[..normalizedOriginal.LastIndexOf('/')]:string.Empty;
            var finalDirectory=final.Contains('/')?final[..final.LastIndexOf('/')]:string.Empty;
            return directory==finalDirectory && report.BackupPath==$".encoding-replaced/{execution:D}/original{Path.GetExtension(normalizedOriginal)}";
        }
        catch(ArgumentException) { return false; }
    }
    internal static async Task RecordInTransactionAsync(Guid execution,Guid library,Guid sourceNode,string original,string sourceSha,string resultSha,int graceHours,
        SourceReplacementReport report,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        var replaced=report.Status=="replaced";
        await using var insert=Sql.Command("""
          INSERT INTO source_replacements(execution_id,library_id,source_node_id,original_path,final_path,backup_path,source_sha,result_sha,state,detail,grace_until)
          VALUES(@execution,@library,@node,@original,@final,@backup,@source,@result,@state,@detail,CASE WHEN @replaced THEN clock_timestamp()+@grace END)
          ON CONFLICT(execution_id) DO NOTHING
          """,c,tx);
        insert.Parameters.AddWithValue("execution",execution); insert.Parameters.AddWithValue("library",library); insert.Parameters.AddWithValue("node",sourceNode);
        insert.Parameters.AddWithValue("original",original); insert.Parameters.AddWithValue("final",SqlKind.Text,(object?)report.FinalPath??DBNull.Value);
        insert.Parameters.AddWithValue("backup",SqlKind.Text,(object?)report.BackupPath??DBNull.Value);
        insert.Parameters.AddWithValue("source",sourceSha); insert.Parameters.AddWithValue("result",resultSha); insert.Parameters.AddWithValue("state",replaced?"replaced":"failed");
        insert.Parameters.AddWithValue("detail",SqlKind.Text,(object?)report.Detail??DBNull.Value); insert.Parameters.AddWithValue("replaced",replaced);
        insert.Parameters.AddWithValue("grace",TimeSpan.FromHours(graceHours));
        if(await insert.ExecuteNonQueryAsync(ct)==1 && replaced) await MarkAsync(library,report.FinalPath!,report.ByteLength,report.ModifiedAt,execution,c,tx,ct);
    }
    /// <summary>Le fichier produit par le système ne doit pas être remis automatiquement en file par la surveillance.</summary>
    private static async Task MarkAsync(Guid library,string path,long bytes,DateTimeOffset modified,Guid execution,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        await using var mark=Sql.Command("INSERT INTO media_replacement_marks(library_id,relative_path,byte_length,modified_at,execution_id) VALUES(@library,@path,@bytes,@modified,@execution) ON CONFLICT DO NOTHING",
            c,tx);
        mark.Parameters.AddWithValue("library",library); mark.Parameters.AddWithValue("path",path); mark.Parameters.AddWithValue("bytes",bytes);
        mark.Parameters.AddWithValue("modified",modified.ToUniversalTime()); mark.Parameters.AddWithValue("execution",execution); await mark.ExecuteNonQueryAsync(ct);
    }

    public async Task<SourceReplacementResponse?> GetAsync(Guid execution,CancellationToken ct)
    {
        await using var cmd=dataSource.CreateCommand("SELECT execution_id,state,original_path,final_path,backup_path,source_sha,result_sha,replaced_at,grace_until,finished_at,detail FROM source_replacements WHERE execution_id=@id UNION ALL SELECT execution_id,CASE WHEN state='claimed' AND claim_until>clock_timestamp() THEN 'replacing' ELSE 'waiting' END,original_path,NULL,NULL,source_sha,result_sha,created_at,NULL,finished_at,detail FROM pending_source_writes p WHERE execution_id=@id AND NOT EXISTS(SELECT 1 FROM source_replacements r WHERE r.execution_id=p.execution_id)");
        cmd.Parameters.AddWithValue("id",execution); await using var r=await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)?new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.IsDBNull(3)?null:r.GetString(3),r.IsDBNull(4)?null:r.GetString(4),r.GetString(5),r.GetString(6),
            r.GetFieldValue<DateTimeOffset>(7),r.IsDBNull(8)?null:r.GetFieldValue<DateTimeOffset>(8),r.IsDBNull(9)?null:r.GetFieldValue<DateTimeOffset>(9),r.IsDBNull(10)?null:r.GetString(10)):null;
    }

    /// <summary>Demande la restauration pendant la période de grâce. Le média est réservé pour qu'aucun traitement ne le modifie en même temps.</summary>
    /// <returns><c>ok</c>, <c>missing</c>, <c>not-restorable</c> (hors période de grâce ou état final) ou <c>busy</c> (média en cours de traitement).</returns>
    public async Task<string> RequestRollbackAsync(Guid execution,CancellationToken ct)
    {
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        await using var read=Sql.Command("""
          SELECT r.state,r.grace_until>clock_timestamp(),r.library_id,w.media_key FROM source_replacements r JOIN execution_work w ON w.execution_id=r.execution_id
          WHERE r.execution_id=@id FOR UPDATE OF r
          """,c,tx);
        read.Parameters.AddWithValue("id",execution); Guid library; string key;
        await using(var r=await read.ExecuteReaderAsync(ct))
        {
            if(!await r.ReadAsync(ct)) return "missing";
            if(r.GetString(0)!="replaced" || r.IsDBNull(1) || !r.GetBoolean(1)) return "not-restorable";
            library=r.GetGuid(2); key=r.GetString(3);
        }
        await using(var reserve=Sql.Command("""
          DELETE FROM media_reservations WHERE library_id=@library AND media_key=@key AND lease_until<=clock_timestamp();
          INSERT INTO media_reservations(library_id,media_key,execution_id,lease_epoch,lease_until) VALUES(@library,@key,@id,-1,clock_timestamp()+@lease) ON CONFLICT DO NOTHING
          """,c,tx))
        {
            reserve.Parameters.AddWithValue("library",library); reserve.Parameters.AddWithValue("key",key); reserve.Parameters.AddWithValue("id",execution);
                reserve.Parameters.AddWithValue("lease",RollbackReservation);
            if(await reserve.ExecuteNonQueryAsync(ct)<1) return "busy";
        }
        await using var update=Sql.Command("UPDATE source_replacements SET state='rollback_requested',detail=NULL WHERE execution_id=@id",c,tx);
        update.Parameters.AddWithValue("id",execution); await update.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct); return "ok";
    }

    public async Task<IReadOnlyList<RuntimeReplacementTask>> TasksAsync(Guid node,CancellationToken ct)
    {
        if(sourceWrites is not null)
        {
            await using var connection=await dataSource.OpenConnectionAsync(ct);
            if(await sourceWrites.CheckAsync(connection,null,ct) is not null || !await sourceWrites.SupportsGuardAsync(node,connection,null,ct)) return [];
        }

        await using var cmd=dataSource.CreateCommand($"""
          SELECT r.execution_id,CASE WHEN r.state='rollback_requested' THEN 'rollback' ELSE 'finalize' END,a.local_path,r.original_path,r.final_path,r.backup_path,r.source_sha,r.result_sha
          FROM source_replacements r JOIN library_node_accesses a ON a.library_id=r.library_id AND a.node_id=r.source_node_id
          JOIN cluster_nodes n ON n.id=r.source_node_id AND n.accepts_work AND n.deleted_at IS NULL
          WHERE {TaskOwnershipRepository.ActiveSql("r.execution_id")} AND r.source_node_id=@node AND a.mode='direct' AND a.is_enabled AND a.can_read AND a.can_write AND a.local_path IS NOT NULL
          AND (r.state='rollback_requested' OR (r.state='replaced' AND r.grace_until<=clock_timestamp()))
          ORDER BY r.replaced_at LIMIT 20
          """);
        cmd.Parameters.AddWithValue("node",node); await using var r=await cmd.ExecuteReaderAsync(ct); var tasks=new List<RuntimeReplacementTask>();
        while(await r.ReadAsync(ct)) tasks.Add(new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),
            r.GetString(7),SourceWriteGuardsSupported:sourceWrites is not null));
        return tasks;
    }

    /// <summary>Enregistre le résultat d'une restauration ou d'une purge ; seul le nœud d'accès à l'origine du remplacement peut le faire.</summary>
    public async Task<bool> AuthorizeAsync(Guid execution,Guid node,string root,CancellationToken ct)
    {
        await using var cmd=dataSource.CreateCommand($"SELECT EXISTS(SELECT 1 FROM source_replacements r JOIN library_node_accesses a ON a.library_id=r.library_id AND a.node_id=r.source_node_id WHERE {TaskOwnershipRepository.ActiveSql("r.execution_id")} AND r.execution_id=@id AND r.source_node_id=@node AND a.is_enabled AND a.can_write AND a.mode='direct' AND a.local_path=@root AND (r.state='rollback_requested' OR r.state='replaced' AND r.grace_until<=clock_timestamp()))");
        cmd.Parameters.AddWithValue("id",execution);cmd.Parameters.AddWithValue("node",node);cmd.Parameters.AddWithValue("root",root);if(!await cmd.ScalarBoolAsync(ct)) return false;
        if(sourceWrites is not null)
        {
            if(!await sourceWrites.VerifyAsync(ct)) return false;
            await using var connection=await dataSource.OpenConnectionAsync(ct);
            if(await sourceWrites.CheckAsync(connection,null,ct) is not null || !await sourceWrites.SupportsGuardAsync(node,connection,null,ct)) return false;
        }
        return await cmd.ScalarBoolAsync(ct);
    }

    public async Task<bool> ReportAsync(Guid execution,Guid node,ReportReplacementTask request,CancellationToken ct)
    {
        if(request.Outcome is not("rolled_back" or "finalized" or "failed") || request.Detail?.Length>2000) return false;
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        await using var read=Sql.Command("""
          SELECT r.state,r.grace_until<=clock_timestamp(),r.library_id,r.original_path,w.media_key FROM source_replacements r JOIN execution_work w ON w.execution_id=r.execution_id
          WHERE r.execution_id=@id AND r.source_node_id=@node FOR UPDATE OF r
          """,c,tx);
        read.Parameters.AddWithValue("id",execution); read.Parameters.AddWithValue("node",node); string state; bool expired; Guid library; string original; string key;
        await using(var r=await read.ExecuteReaderAsync(ct))
        {
            if(!await r.ReadAsync(ct)) return false;
            state=r.GetString(0); expired=!r.IsDBNull(1) && r.GetBoolean(1); library=r.GetGuid(2); original=r.GetString(3); key=r.GetString(4);
        }
        string next;
        if(state=="rollback_requested") next=request.Outcome switch { "rolled_back"=>"rolled_back","failed"=>"replaced",_=>"" };
        else if(state=="replaced" && expired) next=request.Outcome switch { "finalized"=>"finalized","failed"=>"retained",_=>"" };
        else return false;
        if(next.Length==0) return false;
        if(next=="rolled_back")
        {
            if(request.ByteLength is not>0 || request.ModifiedAt is null) return false;
            await MarkAsync(library,original,request.ByteLength.Value,request.ModifiedAt.Value,execution,c,tx,ct);
        }
        await using(var update=Sql.Command("UPDATE source_replacements SET state=@state,detail=@detail,finished_at=CASE WHEN @state IN ('rolled_back','finalized','retained') THEN clock_timestamp() END WHERE execution_id=@id",
            c,tx))
        {
            update.Parameters.AddWithValue("id",execution); update.Parameters.AddWithValue("state",next); update.Parameters.AddWithValue("detail",SqlKind.Text,
                (object?)request.Detail??DBNull.Value); await update.ExecuteNonQueryAsync(ct);
        }
        if(state=="rollback_requested")
        {
            await using var release=Sql.Command("DELETE FROM media_reservations WHERE library_id=@library AND media_key=@key AND execution_id=@id AND lease_epoch=-1",c,tx);
            release.Parameters.AddWithValue("library",library); release.Parameters.AddWithValue("key",key); release.Parameters.AddWithValue("id",execution); await release.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct); return true;
    }
}
