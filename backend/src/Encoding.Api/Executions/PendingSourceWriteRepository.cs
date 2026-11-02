using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Executions;
using System.Data.Common;

namespace Encoding.Api.Executions;

/// <summary>Local durable policy writes. A published result is never lost while the source write is waiting.</summary>
public sealed class PendingSourceWriteRepository(DbDataSource db,ClusterSourceWriteBarrier barrier)
{
    internal static async Task EnqueueAsync(Guid id,Guid source,Guid library,string original,string published,string sourceSha,string resultSha,
        string extension,int hours,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        await using var cmd=Sql.Command($"""
          INSERT INTO pending_source_writes(execution_id,source_node_id,library_id,original_path,published_path,source_sha,result_sha,result_extension,grace_hours,detail)
          VALUES(@id,@source,@library,@original,@published,@before,@after,@extension,@hours,'Remplacement en attente de la connectivité et du rattrapage du cluster.')
          ON CONFLICT(execution_id) DO NOTHING
          """,c,tx);
        cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("source",source);cmd.Parameters.AddWithValue("library",library);
        cmd.Parameters.AddWithValue("original",original);cmd.Parameters.AddWithValue("published",published);cmd.Parameters.AddWithValue("before",sourceSha);
        cmd.Parameters.AddWithValue("after",resultSha);cmd.Parameters.AddWithValue("extension",extension);cmd.Parameters.AddWithValue("hours",hours);
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<PendingSourceWrite?> ClaimAsync(Guid node,CancellationToken ct)
    {
        await using var c=await db.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        if(await barrier.CheckAsync(c,tx,ct) is not null || !await barrier.SupportsGuardAsync(node,c,tx,ct)) return null;
        await using var read=Sql.Command($"""
          SELECT p.execution_id,a.local_path,p.original_path,p.published_path,p.source_sha,p.result_sha,p.result_extension,p.grace_hours,p.library_id
          FROM pending_source_writes p JOIN library_node_accesses a ON a.library_id=p.library_id AND a.node_id=p.source_node_id
          WHERE {TaskOwnershipRepository.ActiveSql("p.execution_id")} AND p.source_node_id=@node AND EXISTS(SELECT 1 FROM cluster_nodes n WHERE n.id=@node AND n.deleted_at IS NULL AND n.accepts_work) AND EXISTS(SELECT 1 FROM executions e WHERE e.id=p.execution_id AND e.state IN ('running','succeeded')) AND (p.state='waiting' OR p.state='claimed' AND p.claim_until<=clock_timestamp())
            AND a.mode='direct' AND a.is_enabled AND a.can_read AND a.can_write AND a.local_path IS NOT NULL
            AND EXISTS(SELECT 1 FROM library_replacement_policies policy WHERE policy.library_id=p.library_id AND policy.enabled)
          ORDER BY p.created_at,p.execution_id LIMIT 1
          """,c,tx);
        read.Parameters.AddWithValue("node",node); PendingSourceWrite result;
        await using(var r=await read.ExecuteReaderAsync(ct))
        {
            if(!await r.ReadAsync(ct)) return null;
            result=new(r.GetGuid(0),Guid.NewGuid(),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetInt32(7),r.GetGuid(8));
        }
        await using var claim=Sql.Command("UPDATE pending_source_writes SET state='claimed',detail='Remplacement en cours sur le nœud d’accès.',claim_id=@claim,claim_until=clock_timestamp()+interval '5 minutes' WHERE execution_id=@id",
            c,tx);
        claim.Parameters.AddWithValue("id",result.ExecutionId);claim.Parameters.AddWithValue("claim",result.ClaimId);
        await claim.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);return result;
    }
    public async Task<bool> AuthorizeAsync(Guid id,Guid node,Guid claim,string root,CancellationToken ct)
    {
        await using(var check=db.CreateCommand("SELECT EXISTS(SELECT 1 FROM pending_source_writes WHERE execution_id=@id AND source_node_id=@node AND state='claimed' AND claim_id=@claim AND claim_until>clock_timestamp())"))
        {
            check.Parameters.AddWithValue("id",id);check.Parameters.AddWithValue("node",node);check.Parameters.AddWithValue("claim",claim);
            if(!await check.ScalarBoolAsync(ct)) return false;
        }
        if(!await barrier.VerifyAsync(ct)) return false;
        await using var c=await db.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        if(await barrier.CheckAsync(c,tx,ct) is not null || !await barrier.SupportsGuardAsync(node,c,tx,ct)) return false;
        await using var update=Sql.Command($"""
          UPDATE pending_source_writes SET claim_until=clock_timestamp()+interval '5 minutes'
          WHERE {TaskOwnershipRepository.ActiveSql("pending_source_writes.execution_id")} AND execution_id=@id AND source_node_id=@node AND state='claimed' AND claim_id=@claim AND claim_until>clock_timestamp()
            AND EXISTS(SELECT 1 FROM media_reservations m WHERE m.execution_id=@id AND m.lease_until>clock_timestamp())
            AND EXISTS(SELECT 1 FROM executions e WHERE e.id=@id AND e.state IN ('running','succeeded'))
            AND EXISTS(SELECT 1 FROM library_replacement_policies policy WHERE policy.library_id=pending_source_writes.library_id AND policy.enabled)
            AND EXISTS(SELECT 1 FROM library_node_accesses a WHERE a.library_id=pending_source_writes.library_id AND a.node_id=@node AND a.is_enabled AND a.can_write AND a.mode='direct' AND a.local_path=@root)
          """,c,tx);
        update.Parameters.AddWithValue("id",id);update.Parameters.AddWithValue("node",node);update.Parameters.AddWithValue("claim",claim);update.Parameters.AddWithValue("root",root);
        var ok=await update.ExecuteNonQueryAsync(ct)==1;await tx.CommitAsync(ct);return ok;
    }
    public async Task<bool> CompleteAsync(Guid id,Guid node,PendingSourceWriteCompletion request,CancellationToken ct)
    {
        await using var c=await db.OpenConnectionAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        await using(var receipt=Sql.Command("SELECT state FROM pending_source_writes WHERE execution_id=@id AND source_node_id=@node AND claim_id=@claim AND state IN ('completed','failed')",c,tx))
        {
            receipt.Parameters.AddWithValue("id",id);receipt.Parameters.AddWithValue("node",node);receipt.Parameters.AddWithValue("claim",request.ClaimId);
            if(await receipt.ExecuteScalarAsync(ct) is string prior)
                return request.Report is not null && (prior=="completed"?request.Report.Status=="replaced":request.Report.Status=="failed");
        }
        Guid library;string original,sourceSha,resultSha;int hours;
        await using(var read=Sql.Command("SELECT library_id,original_path,source_sha,result_sha,grace_hours FROM pending_source_writes WHERE execution_id=@id AND source_node_id=@node AND state='claimed' AND claim_id=@claim AND claim_until>clock_timestamp()",
            c,tx))
        {
            read.Parameters.AddWithValue("id",id);read.Parameters.AddWithValue("node",node);read.Parameters.AddWithValue("claim",request.ClaimId);
            await using var r=await read.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct)) return false;
            library=r.GetGuid(0);original=r.GetString(1);sourceSha=r.GetString(2);resultSha=r.GetString(3);hours=r.GetInt32(4);
        }
        if(request.Report is {} report)
        {
            if(!SourceReplacementRepository.ValidReport(id,original,report)) return false;
            await SourceReplacementRepository.RecordInTransactionAsync(id,library,node,original,sourceSha,resultSha,hours,report,c,tx,ct);
        }
        await using var update=Sql.Command("UPDATE pending_source_writes SET state=@state,detail=@detail,claim_id=CASE WHEN @done THEN claim_id END,claim_until=NULL,finished_at=CASE WHEN @done THEN clock_timestamp() END WHERE execution_id=@id",
            c,tx);
        update.Parameters.AddWithValue("id",id);update.Parameters.AddWithValue("state",request.Report is null?"waiting":request.Report.Status=="replaced"?"completed":"failed");
        update.Parameters.AddWithValue("detail",request.Report?.Detail??"Remplacement différé ; résultat publié et source conservés.");
            update.Parameters.AddWithValue("done",request.Report is not null);
        await update.ExecuteNonQueryAsync(ct);
        if(request.Report is not null)
        {
            await using(var finish=Sql.Command("SELECT e.revision,e.node_id FROM executions e JOIN execution_work w ON w.execution_id=e.id WHERE e.id=@id AND e.state='running' AND w.publication_requested AND w.published_path IS NOT NULL AND NOT w.continue_after_publication",
                c,tx))
            {
                finish.Parameters.AddWithValue("id",id);int? revision=null;Guid? actor=null;
                await using(var row=await finish.ExecuteReaderAsync(ct))
                    if(await row.ReadAsync(ct)) {revision=row.GetInt32(0);actor=row.IsDBNull(1)?null:row.GetGuid(1);}
                if(revision.HasValue)
                    await ExecutionRepository.RecordInTransactionAsync(id,new(Guid.NewGuid(),revision.Value,"succeeded",actor,100,
                        request.Report.Status=="replaced"?"Résultat publié ; remplacement différé effectué, sauvegarde conservée.":"Résultat publié ; remplacement différé non effectué, voir le diagnostic de remplacement."),
                            c,tx,ct);
            }
            await using var terminal=Sql.Command("SELECT state IN ('succeeded','failed','cancelled') FROM executions WHERE id=@id",c,tx);terminal.Parameters.AddWithValue("id",id);
            if(await terminal.ScalarBoolAsync(ct)) await RuntimeRepository.ReleaseAsync(id,c,tx,ct);
        }
        await tx.CommitAsync(ct);return true;
    }
}
