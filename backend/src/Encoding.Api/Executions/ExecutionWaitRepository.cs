using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using System.Text.Json;
using Encoding.Api.Nodes;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Executions;

/// <summary>Attentes durables des blocs Attendre (architecture 0033) : mise en attente, décision unique, sondes de conditions de plugin.</summary>
public sealed class ExecutionWaitRepository(DbDataSource dataSource,NodePresenceOptions presence)
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    internal static RuntimeWaitProbe ProbeOf(RuntimeWait wait)=>new(wait.BlockNodeId!,wait.MetOutputs!);

    internal static string Describe(RuntimeWait wait)=>wait.Mode switch
    {
        "timer"=>$"En attente : {wait.DelayMinutes} min.",
        "nodeState"=>$"En attente : worker {(wait.NodeId is null?"quelconque":wait.NodeId.Value.ToString("D"))} {wait.NodeStatus switch{"online"=>"en ligne","offline"=>"hors ligne",_=>"libre"}}.",
        "workload"=>$"En attente : {(wait.Metric=="running"?"tâches en cours":"tâches en file")} ({(wait.Scope=="library"?"bibliothèque":"cluster")}) {(wait.Operator=="lte"?"≤":"≥")} {wait.Threshold}.",
        _=>$"En attente : condition du bloc {wait.BlockNodeId} ({wait.MetOutputs})."
    };

    /// <summary>Même transaction que le checkpoint : crée l'attente et libère worker, bail et plafonds en gardant la réservation du média.</summary>
    internal static async Task BeginAsync(Guid id,string nodeId,RuntimeWait wait,long epoch,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        await using var cmd=Sql.Command("""
          INSERT INTO execution_waits(execution_id,node_id,spec,state,started_at,deadline,due_at,next_check_at,resolved_at,probe_count,last_probe_at,last_probe_output)
          VALUES(@id,@node,@spec,'waiting',clock_timestamp(),
            CASE WHEN @max::integer IS NULL THEN NULL ELSE clock_timestamp()+make_interval(mins=>@max) END,
            CASE WHEN @delay::integer IS NULL THEN NULL ELSE clock_timestamp()+make_interval(mins=>@delay) END,
            CASE WHEN @delay::integer IS NULL THEN clock_timestamp() ELSE clock_timestamp()+make_interval(mins=>@delay) END,NULL,0,NULL,NULL)
          ON CONFLICT(execution_id) DO UPDATE SET node_id=EXCLUDED.node_id,spec=EXCLUDED.spec,state='waiting',started_at=EXCLUDED.started_at,
            deadline=EXCLUDED.deadline,due_at=EXCLUDED.due_at,next_check_at=EXCLUDED.next_check_at,resolved_at=NULL,probe_count=0,last_probe_at=NULL,last_probe_output=NULL;
          UPDATE execution_waits SET next_check_at=LEAST(next_check_at,COALESCE(deadline,'infinity'::timestamptz)) WHERE execution_id=@id;
          UPDATE execution_work SET worker_node_id=NULL,lease_until=NULL,lease_epoch=lease_epoch+1,available_at='infinity'::timestamptz,wait_probe_due=false,
            handoff_pending=false,handoff_selector=NULL,previous_worker_node_id=NULL,media_policy=@media WHERE execution_id=@id;
          UPDATE media_reservations SET lease_epoch=@epoch+1,lease_until='infinity'::timestamptz WHERE execution_id=@id;
          DELETE FROM disk_locks WHERE execution_id=@id;
          """,c,tx);
        cmd.Parameters.AddWithValue("media",wait.MediaPolicy);
        cmd.Parameters.AddWithValue("id",id); cmd.Parameters.AddWithValue("node",nodeId); cmd.Parameters.AddWithValue("epoch",epoch);
        cmd.Parameters.AddWithValue("spec",SqlKind.Json,JsonSerializer.Serialize(wait,Json));
        cmd.Parameters.AddWithValue("max",SqlKind.Integer,(object?)wait.MaxWaitMinutes??DBNull.Value);
        cmd.Parameters.AddWithValue("delay",SqlKind.Integer,(object?)wait.DelayMinutes??DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Un worker rend le résultat d'une sonde. Une époque périmée ou un bail non destiné à une sonde est refusé.</summary>
    public async Task<bool> ReportProbeAsync(Guid id,Guid worker,ReportWaitProbe request,CancellationToken ct)
    {
        if(request.OutputId is { Length: > 128 }) return false;
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        if(!await RuntimeRepository.FenceAsync(id,worker,request.Epoch,c,tx,ct)) return false;
        RuntimeWait spec; int revision; Guid? actor;
        await using(var read=Sql.Command("""
          SELECT x.spec::text,e.revision,e.node_id FROM execution_waits x JOIN execution_work k ON k.execution_id=x.execution_id
          JOIN executions e ON e.id=x.execution_id
          WHERE x.execution_id=@id AND x.state='waiting' AND k.wait_probe_due FOR UPDATE OF x
          """,c,tx))
        {
            read.Parameters.AddWithValue("id",id);
            await using var r=await read.ExecuteReaderAsync(ct); if(!await r.ReadAsync(ct)) return false;
            spec=JsonSerializer.Deserialize<RuntimeWait>(r.GetString(0),Json)!; revision=r.GetInt32(1); actor=r.IsDBNull(2)?null:r.GetGuid(2);
        }
        var met=request.OutputId is not null && ProbeOf(spec).Met.Contains(request.OutputId);
        await using(var update=Sql.Command("""
          UPDATE execution_waits SET probe_count=probe_count+1,last_probe_at=clock_timestamp(),last_probe_output=@output,
            state=CASE WHEN @met THEN 'met' ELSE state END,resolved_at=CASE WHEN @met THEN clock_timestamp() ELSE NULL END,
            next_check_at=CASE WHEN @met THEN next_check_at ELSE LEAST(clock_timestamp()+make_interval(secs=>@poll),COALESCE(deadline,'infinity'::timestamptz)) END
            WHERE execution_id=@id;
          UPDATE execution_work SET worker_node_id=NULL,lease_until=NULL,lease_epoch=lease_epoch+1,wait_probe_due=false,
            available_at=CASE WHEN @met THEN clock_timestamp() ELSE 'infinity'::timestamptz END WHERE execution_id=@id;
          UPDATE media_reservations SET lease_epoch=@epoch+1,lease_until='infinity'::timestamptz WHERE execution_id=@id;
          """,c,tx))
        {
            update.Parameters.AddWithValue("id",id); update.Parameters.AddWithValue("met",met); update.Parameters.AddWithValue("epoch",request.Epoch);
            update.Parameters.AddWithValue("poll",(double)spec.PollSeconds); update.Parameters.AddWithValue("output",SqlKind.Text,(object?)request.OutputId??DBNull.Value);
            await update.ExecuteNonQueryAsync(ct);
        }
        if(met && actor is not null)
            await ExecutionRepository.RecordInTransactionAsync(id,new(Guid.NewGuid(),revision,"running",actor,null,
                $"Attente terminée : la condition du bloc est remplie ({request.OutputId})."),c,tx,ct);
        await tx.CommitAsync(ct); return true;
    }

    /// <summary>Évalue les attentes dues. Chaque décision est prise une seule fois (verrou de ligne, SKIP LOCKED) quel que soit le nombre de serveurs.</summary>
    public async Task<int> ProcessDueAsync(CancellationToken ct)
    {
        // Skip idle polls without taking SQLite's writer lock. The transaction below rechecks
        // both cleanup and due-work conditions, so a concurrent resolution is still safe.
        await using (var readConnection = await dataSource.OpenConnectionAsync(ct))
        await using (var pending = Sql.Command("""
            SELECT EXISTS(SELECT 1 FROM execution_waits x JOIN executions e ON e.id=x.execution_id
              WHERE x.state='waiting' AND (e.state IN('cancelled','failed','succeeded','deferred')
                OR (e.state='running' AND x.next_check_at<=clock_timestamp()
                  AND EXISTS(SELECT 1 FROM execution_work w WHERE w.execution_id=e.id))))
            """, readConnection))
            if (!await pending.ScalarBoolAsync(ct)) return 0;
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        await using(var closed=Sql.Command("""
          UPDATE execution_waits SET state='cancelled',resolved_at=clock_timestamp()
          WHERE state='waiting' AND execution_id IN(SELECT id FROM executions WHERE state IN('cancelled','failed','succeeded','deferred'))
          """,c,tx)) await closed.ExecuteNonQueryAsync(ct);
        var due=new List<(Guid Id,string Node,RuntimeWait Spec,bool Expired,bool TimerDue,int Revision,Guid? Actor,FlowDefinition Flow,Guid Library,bool ProbePending)>();
        await using(var read=Sql.Command("""
          SELECT x.execution_id,x.node_id,x.spec::text,COALESCE(x.deadline<=clock_timestamp(),false),COALESCE(x.due_at<=clock_timestamp(),false),
            e.revision,e.node_id,e.flow_definition::text,k.library_id,k.wait_probe_due
          FROM execution_waits x JOIN executions e ON e.id=x.execution_id JOIN execution_work k ON k.execution_id=x.execution_id
          WHERE x.state='waiting' AND x.next_check_at<=clock_timestamp() AND e.state='running'
          ORDER BY x.next_check_at LIMIT 50 FOR UPDATE OF x,e,k SKIP LOCKED
          """,c,tx))
        {
            await using var r=await read.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) due.Add((r.GetGuid(0),r.GetString(1),JsonSerializer.Deserialize<RuntimeWait>(r.GetString(2),Json)!,r.GetBoolean(3),r.GetBoolean(4),
                r.GetInt32(5),r.IsDBNull(6)?null:r.GetGuid(6),JsonSerializer.Deserialize<FlowDefinition>(r.GetString(7),Json)!,r.GetGuid(8),r.GetBoolean(9)));
        }
        foreach(var item in due)
        {
            if(item.Expired) { await ResolveAsync(item,"timeout",c,tx,ct); continue; }
            var met=item.Spec.Mode switch
            {
                "timer"=>item.TimerDue,
                "nodeState"=>await NodeStateAsync(item.Spec,c,tx,ct),
                "workload"=>await WorkloadAsync(item.Spec,item.Library,c,tx,ct),
                _=>false
            };
            if(met) { await ResolveAsync(item,"met",c,tx,ct); continue; }
            // Une condition de plugin est évaluée par un worker. Si ce worker perd son bail, RuntimeRecovery échoue la tentative au lieu de réattribuer la sonde.
            var probe=item.Spec.Mode=="block";
            await using var next=Sql.Command("""
              UPDATE execution_waits SET next_check_at=LEAST(clock_timestamp()+make_interval(secs=>@poll),COALESCE(deadline,'infinity'::timestamptz)) WHERE execution_id=@id;
              UPDATE execution_work SET wait_probe_due=true,available_at=clock_timestamp() WHERE execution_id=@id AND @probe AND lease_until IS NULL;
              """,c,tx);
            next.Parameters.AddWithValue("id",item.Id); next.Parameters.AddWithValue("probe",probe); next.Parameters.AddWithValue("poll",(double)item.Spec.PollSeconds);
            await next.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct); return due.Count;
    }

    private async Task ResolveAsync((Guid Id,string Node,RuntimeWait Spec,bool Expired,bool TimerDue,int Revision,Guid? Actor,FlowDefinition Flow,Guid Library,bool ProbePending) item,
        string outcome,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        var detail="Attente terminée : condition remplie.";
        if(outcome=="timeout")
        {
            // Le contexte ne change pas pendant l'attente : seule la sortie du dernier checkpoint est remplacée, par une connexion validée du flow publié.
            var (output,nextNode)=RuntimeWaitPolicy.TimeoutRoute(item.Flow,item.Node);
            string? json;
            await using(var read=Sql.Command("SELECT checkpoint::text FROM execution_steps WHERE execution_id=@id AND node_id=@node AND completed_at IS NOT NULL",c,tx))
            { read.Parameters.AddWithValue("id",item.Id); read.Parameters.AddWithValue("node",item.Node); json=await read.ExecuteScalarAsync(ct) as string; }
            if(json is null) return;
            var checkpoint=JsonSerializer.Deserialize<RuntimeCheckpoint>(json,Json)! with { OutputId=output,NextNodeId=nextNode,Wait=null };
            await using(var write=Sql.Command("UPDATE execution_steps SET checkpoint=@checkpoint WHERE execution_id=@id AND node_id=@node",c,tx))
            {
                write.Parameters.AddWithValue("id",item.Id); write.Parameters.AddWithValue("node",item.Node);
                write.Parameters.AddWithValue("checkpoint",SqlKind.Json,JsonSerializer.Serialize(checkpoint,Json)); await write.ExecuteNonQueryAsync(ct);
            }
            detail=nextNode is null?"Délai maximal atteint ; la sortie « Délai dépassé » n'est pas reliée : l'exécution échoue.":"Délai maximal atteint ; le flow continue par la sortie « Délai dépassé ».";
        }
        await using(var update=Sql.Command("""
          UPDATE execution_waits SET state=@state,resolved_at=clock_timestamp() WHERE execution_id=@id;
          UPDATE execution_work SET available_at=clock_timestamp(),wait_probe_due=false WHERE execution_id=@id;
          """,c,tx))
        { update.Parameters.AddWithValue("id",item.Id); update.Parameters.AddWithValue("state",outcome); await update.ExecuteNonQueryAsync(ct); }
        if(item.Actor is not null)
            await ExecutionRepository.RecordInTransactionAsync(item.Id,new(Guid.NewGuid(),item.Revision,"running",item.Actor,null,detail),c,tx,ct);
    }

    private async Task<bool> NodeStateAsync(RuntimeWait spec,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        await using var cmd=Sql.Command("""
          SELECT EXISTS(
            SELECT 1 FROM cluster_nodes n
            WHERE n.deleted_at IS NULL AND 'worker'=ANY(n.roles) AND (@node::uuid IS NULL OR n.id=@node)
              AND @status<>'offline' AND n.last_seen_at>clock_timestamp()-@offline
              AND (@status='online' OR (SELECT count(*) FROM execution_work w JOIN executions e ON e.id=w.execution_id
                    WHERE w.worker_node_id=n.id AND w.lease_until>clock_timestamp() AND e.state IN('running','cancelling'))
                  <COALESCE((SELECT p.max_concurrent_tasks FROM node_processing_settings p WHERE p.node_id=n.id),n.max_concurrent_tasks)))
          OR (@status='offline' AND NOT EXISTS(
            SELECT 1 FROM cluster_nodes n WHERE n.id=@node AND n.deleted_at IS NULL AND n.last_seen_at>clock_timestamp()-@offline))
          """,c,tx);
        cmd.Parameters.AddWithValue("node",SqlKind.Uuid,(object?)spec.NodeId??DBNull.Value); cmd.Parameters.AddWithValue("status",spec.NodeStatus!);
        cmd.Parameters.AddWithValue("offline",presence.OfflineAfter);
        return (await cmd.ScalarBoolAsync(ct));
    }

    private static async Task<bool> WorkloadAsync(RuntimeWait spec,Guid library,DbConnection c,DbTransaction tx,CancellationToken ct)
    {
        await using var cmd=Sql.Command("""
          SELECT count(*) FROM execution_work w JOIN executions e ON e.id=w.execution_id
          WHERE (@scope='cluster' OR w.library_id=@library)
            AND CASE WHEN @metric='running' THEN e.state IN('running','cancelling') AND w.lease_until>clock_timestamp() ELSE e.state='queued' END
          """,c,tx);
        cmd.Parameters.AddWithValue("scope",spec.Scope!); cmd.Parameters.AddWithValue("library",library); cmd.Parameters.AddWithValue("metric",spec.Metric!);
        var count=(long)(await cmd.ExecuteScalarAsync(ct))!;
        return spec.Operator=="lte"?count<=spec.Threshold:count>=spec.Threshold;
    }
}

/// <summary>Évalue périodiquement les attentes dues sur chaque serveur ; sans état local, donc sans point de défaillance unique.</summary>
public sealed class ExecutionWaitService(ExecutionWaitRepository waits,ILogger<ExecutionWaitService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try
            {

                await waits.ProcessDueAsync(ct);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { break; }
            catch(Exception exception) { logger.LogWarning("Évaluation des attentes momentanément indisponible : {Type}.",exception.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(5),ct); } catch(OperationCanceledException) when(ct.IsCancellationRequested) { break; }
        }
    }
}
