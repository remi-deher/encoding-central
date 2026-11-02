using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Microsoft.Extensions.Logging.Abstractions;

namespace Encoding.Runtime.Tests;

internal static partial class Harness
{
    internal sealed partial class Fixture
    {
        private ExecutionWaitRepository Waits => new(db,new Encoding.Api.Nodes.NodePresenceOptions(TimeSpan.FromHours(1)));

        /// <summary>entrée → [lecture Plex] → attente → fin ; l'attente a une sortie « timeout » si <paramref name="timeoutRoute"/>.</summary>
        private FlowDefinition WaitFlow(Dictionary<string,JsonElement> wait,bool timeoutRoute=false,bool watched=false)
        {
            var nodes=new List<FlowNodeDefinition>{Node(registry,"entry","base-input-file")};
            var edges=new List<FlowConnectionDefinition>();
            if(watched)
            {
                nodes.Add(Node(registry,"playback","plex-check-playback",new(){["scope"]=J("global")}));
                edges.Add(new("e1","entry","success","playback")); edges.Add(new("e2","playback","idle","wait")); edges.Add(new("e3","playback","playing","wait"));
            }
            else edges.Add(new("e1","entry","success","wait"));
            nodes.Add(Node(registry,"wait","base-wait",wait));
            nodes.Add(Node(registry,"end","base-complete-flow"));
            edges.Add(new("e4","wait","resumed","end"));
            if(timeoutRoute) { nodes.Add(Node(registry,"late","base-complete-flow",new(){["result"]=J("failed")})); edges.Add(new("e5","wait","timeout","late")); }
            return new(1,Guid.NewGuid(),1,"Wait fixture","video",nodes,edges);
        }

        /// <summary>Complète les étapes jusqu'au bloc Attendre puis enregistre son checkpoint avec la demande d'attente.</summary>
        private async Task<(Guid Id,RuntimeLease Lease,RuntimeContext Context,RuntimeCheckpoint Checkpoint)> WaitingAsync(FlowDefinition flow,string[]? before=null,Func<Guid,RuntimeLease,Task>? beforeWait=null)
        {
            var (id,lease,context)=await EnqueueAsync(flow);
            await CompleteAsync(id,a,lease,"entry",context);
            if(beforeWait is not null) await beforeWait(id,lease);
            foreach(var step in before??[])
            {
                var started=await runtime.StartStepAsync(id,a,new(lease.Epoch,step),CancellationToken.None)!;
                var status0=await runtime.RenewAsync(id,a,lease.Epoch,CancellationToken.None)!;
                Check(await runtime.CompleteStepAsync(id,a,new(lease.Epoch,Guid.NewGuid(),status0!.Revision,new(step,started!.InvocationId,"playing","wait",context,null)),CancellationToken.None),"Condition checkpoint.");
            }
            var node=flow.Nodes.Single(item=>item.Id=="wait");
            var spec=RuntimeWaitPolicy.Resolve(node);
            var begun=await runtime.StartStepAsync(id,a,new(lease.Epoch,"wait"),CancellationToken.None)??throw new InvalidOperationException("Start wait.");
            var status=await runtime.RenewAsync(id,a,lease.Epoch,CancellationToken.None)!;
            var checkpoint=new RuntimeCheckpoint("wait",begun.InvocationId,"resumed","end",context,null,null,spec);
            Check(!await runtime.CompleteStepAsync(id,a,new(lease.Epoch,Guid.NewGuid(),status!.Revision,checkpoint with { Wait=null }),CancellationToken.None),"A wait block checkpoint without its wait request is refused.");
            Check(!await runtime.CompleteStepAsync(id,a,new(lease.Epoch,Guid.NewGuid(),status.Revision,checkpoint with { Wait=spec with { PollSeconds=spec.PollSeconds+1 } }),CancellationToken.None),"A forged wait request is refused.");
            Check(await runtime.CompleteStepAsync(id,a,new(lease.Epoch,Guid.NewGuid(),status.Revision,checkpoint),CancellationToken.None),"The wait checkpoint commits.");
            return (id,lease,context,checkpoint);
        }

        private Task<string?> ReservationUntilAsync(Guid id)=>ScalarAsync<string>("SELECT CASE WHEN lease_until='infinity' THEN 'unbounded' ELSE 'finite' END FROM media_reservations WHERE execution_id=@id",("id",id));
        private Task AgeWaitAsync(Guid id,string column)=>ScalarAsync<object>($"UPDATE execution_waits SET {column}=clock_timestamp()-interval '1 second',next_check_at=clock_timestamp()-interval '1 second' WHERE execution_id=@id RETURNING 1",("id",id));

        public async Task WaitTimerAsync()
        {
            var flow=WaitFlow(new(){["mode"]=J("timer"),["delayMinutes"]=J(30)});
            var (id,lease,_,_)=await WaitingAsync(flow);
            Check(await runtime.RenewAsync(id,a,lease.Epoch,CancellationToken.None) is null,"The waiting execution gives up its lease: the worker is free.");
            Check(await ReservationUntilAsync(id)=="unbounded","The media stays reserved while waiting.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM execution_work w JOIN executions e ON e.id=w.execution_id WHERE w.execution_id=@id AND w.lease_until>clock_timestamp()",("id",id))==0,"A waiting execution holds no active lease, so no processing slot.");
            Check((await executions.GetAsync(id,CancellationToken.None))!.Detail.StartsWith("En attente",StringComparison.Ordinal),"The wait is visible in the execution detail.");
            foreach(var worker in new[]{a,b,c}) Check(await runtime.ClaimAsync(worker,CancellationToken.None) is null,"No worker can claim a waiting execution.");
            await Waits.ProcessDueAsync(CancellationToken.None);
            Check(await runtime.ClaimAsync(b,CancellationToken.None) is null,"A timer that is not due does not wake the execution.");
            await AgeWaitAsync(id,"due_at");
            Check(await Waits.ProcessDueAsync(CancellationToken.None)>=1,"The due timer is evaluated.");
            var resumed=await runtime.ClaimAsync(c,CancellationToken.None)??throw new InvalidOperationException("The woken execution must be claimable.");
            Check(resumed.ExecutionId==id && resumed.Checkpoint?.NextNodeId=="end" && resumed.Probe is null && resumed.Epoch>lease.Epoch,"The flow resumes at the step after the block under a new epoch.");
            Check(await ScalarAsync<string>("SELECT state FROM execution_waits WHERE execution_id=@id",("id",id))=="met","The wait is recorded as met.");
            await Waits.ProcessDueAsync(CancellationToken.None);
            Check(await ScalarAsync<long>("SELECT count(*) FROM execution_events WHERE execution_id=@id AND detail LIKE 'Attente terminée%'",("id",id))==1,"The decision is recorded once.");
            Check(await executions.CancelAsync(id,CancellationToken.None),"Cancel timer fixture.");await ExpireAsync(id);await RecoverCancellationAsync(id);
        }

        public async Task WaitDeadlineAsync(bool routed)
        {
            var flow=WaitFlow(new(){["mode"]=J("timer"),["delayMinutes"]=J(600),["maxWaitMinutes"]=J(5)},routed);
            var (id,_,_,_)=await WaitingAsync(flow);
            await ScalarAsync<object>("UPDATE execution_waits SET deadline=clock_timestamp()-interval '1 second',next_check_at=clock_timestamp()-interval '1 second' WHERE execution_id=@id RETURNING 1",("id",id));
            // Deux évaluations concurrentes (deux serveurs) ne produisent qu'une décision.
            await Task.WhenAll(Waits.ProcessDueAsync(CancellationToken.None),Waits.ProcessDueAsync(CancellationToken.None));
            Check(await ScalarAsync<string>("SELECT state FROM execution_waits WHERE execution_id=@id",("id",id))=="timeout","The maximum wait ends the wait.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM execution_events WHERE execution_id=@id AND detail LIKE 'Délai maximal%'",("id",id))==1,"Concurrent servers record one decision.");
            var resumed=await runtime.ClaimAsync(b,CancellationToken.None)??throw new InvalidOperationException("The timed out execution must continue.");
            if(routed) Check(resumed.Checkpoint is { OutputId:"timeout",NextNodeId:"late" },"A connected timeout output continues the flow.");
            else Check(resumed.Checkpoint is { OutputId:"error",NextNodeId:null },"Without a timeout output the execution ends in an explicit failure.");
            Check(await executions.CancelAsync(id,CancellationToken.None),"Cancel deadline fixture.");await ExpireAsync(id);await RecoverCancellationAsync(id);
        }

        public async Task WaitServerEventsAsync()
        {
            // Charge de travail : aucune tâche en file n'est un seuil déjà atteint.
            var (id,_,_,_)=await WaitingAsync(WaitFlow(new(){["mode"]=J("workload"),["workloadMetric"]=J("queued"),["workloadScope"]=J("cluster"),["workloadOperator"]=J("lte"),["workloadThreshold"]=J(0)}));
            await Waits.ProcessDueAsync(CancellationToken.None);
            Check(await ScalarAsync<string>("SELECT state FROM execution_waits WHERE execution_id=@id",("id",id))=="met","The workload threshold wakes the execution.");
            Check(await executions.CancelAsync(id,CancellationToken.None),"Cancel workload fixture.");await ExpireAsync(id);await RecoverCancellationAsync(id);
            // État d'un worker : attendre qu'un worker précis soit hors ligne.
            var (second,_,_,_)=await WaitingAsync(WaitFlow(new(){["mode"]=J("nodeState"),["nodeStatus"]=J("offline"),["nodeId"]=J(c.ToString("D")),["pollSeconds"]=J(15)}));
            await ScalarAsync<object>("UPDATE execution_waits SET next_check_at=clock_timestamp()-interval '1 second' WHERE execution_id=@id RETURNING 1",("id",second));
            await Waits.ProcessDueAsync(CancellationToken.None);
            Check(await ScalarAsync<string>("SELECT state FROM execution_waits WHERE execution_id=@id",("id",second))=="waiting","An online worker does not satisfy an offline wait.");
            await ScalarAsync<object>("UPDATE cluster_nodes SET last_seen_at=clock_timestamp()-interval '3 hours' WHERE id=@c RETURNING 1",("c",c));
            await ScalarAsync<object>("UPDATE execution_waits SET next_check_at=clock_timestamp()-interval '1 second' WHERE execution_id=@id RETURNING 1",("id",second));
            await Waits.ProcessDueAsync(CancellationToken.None);
            Check(await ScalarAsync<string>("SELECT state FROM execution_waits WHERE execution_id=@id",("id",second))=="met","A worker that stops answering wakes the wait.");
            await ResetNodesAsync();
            Check(await executions.CancelAsync(second,CancellationToken.None),"Cancel node fixture.");await ExpireAsync(second);await RecoverCancellationAsync(second);
        }

        public async Task WaitBlockProbeAsync()
        {
            var flow=WaitFlow(new(){["mode"]=J("block"),["blockNodeId"]=J("playback"),["blockOutputs"]=J("idle"),["pollSeconds"]=J(15)},false,true);
            Check(validator.Validate(flow).IsValid,"A wait watching a plugin condition validates.");
            var (id,lease,_,_)=await WaitingAsync(flow,["playback"]);
            // Seuls les workers disposant du plugin surveillé peuvent évaluer la condition.
            await ScalarAsync<object>(TestDatabase.IsSqlite
                ? "UPDATE cluster_nodes SET capabilities=(SELECT COALESCE(json_group_array(json(c.value)),'[]') FROM json_each(capabilities) c WHERE c.value->>'toolId'<>'plugin/plex') WHERE id=@c RETURNING 1"
                : "UPDATE cluster_nodes SET capabilities=(SELECT jsonb_agg(c) FROM jsonb_array_elements(capabilities) c WHERE c->>'toolId'<>'plugin/plex') WHERE id=@c RETURNING 1",("c",c));
            await Waits.ProcessDueAsync(CancellationToken.None);
            Check(await runtime.ClaimAsync(c,CancellationToken.None) is null,"A worker without the plugin cannot evaluate the condition.");
            var probe=await runtime.ClaimAsync(b,CancellationToken.None)??throw new InvalidOperationException("A capable worker must receive the probe.");
            Check(probe.Probe is { BlockNodeId:"playback" } && probe.Checkpoint?.NextNodeId=="end" && probe.Epoch>lease.Epoch,"The probe carries the watched block and the frozen context.");
            Check(!await Waits.ReportProbeAsync(id,a,new(probe.Epoch,"idle"),CancellationToken.None),"Another worker cannot report the probe.");
            Check(!await Waits.ReportProbeAsync(id,b,new(probe.Epoch-1,"idle"),CancellationToken.None),"A stale epoch cannot report the probe.");
            Check(await Waits.ReportProbeAsync(id,b,new(probe.Epoch,"playing"),CancellationToken.None),"The probe result is accepted.");
            Check(!await Waits.ReportProbeAsync(id,b,new(probe.Epoch,"idle"),CancellationToken.None),"A probe cannot be reported twice.");
            Check(await ScalarAsync<string>("SELECT state FROM execution_waits WHERE execution_id=@id",("id",id))=="waiting","An unmet condition keeps waiting.");
            Check(await ReservationUntilAsync(id)=="unbounded" && await runtime.ClaimAsync(b,CancellationToken.None) is null,"After an unmet probe the media stays reserved and the worker is free.");
            // La perte du worker chargé d'une sonde échoue la tentative, comme toute autre tâche attribuée.
            await AgeWaitAsync(id,"due_at");
            await Waits.ProcessDueAsync(CancellationToken.None);
            var lost=await runtime.ClaimAsync(b,CancellationToken.None)??throw new InvalidOperationException("The next probe is due.");
            await ExpireAsync(id);
            Check(await runtime.ClaimAsync(a,CancellationToken.None) is null,"An expired condition probe is not silently reassigned.");
            var recovery=new RuntimeRecoveryService(db,NullLogger<RuntimeRecoveryService>.Instance);
            await recovery.ProcessOnceAsync(CancellationToken.None);
            Check((await executions.GetAsync(id,CancellationToken.None))!.State=="failed","A lost condition-probe worker fails the execution attempt.");
            await Waits.ProcessDueAsync(CancellationToken.None);
            Check(await ScalarAsync<string>("SELECT state FROM execution_waits WHERE execution_id=@id",("id",id))=="cancelled","Failure closes the durable wait.");
            Check(await ReservationUntilAsync(id) is null,"Failure releases the media reservation.");
            Check(lost.Probe is not null,"The expired assignment was the condition probe.");
        }

        public async Task WaitRunnerAndCancellationAsync()
        {
            var flow=WaitFlow(new(){["mode"]=J("timer"),["delayMinutes"]=J(30)});
            var (id,lease,_)=await EnqueueAsync(flow);
            var runner=Runner("wait-a",out var options);
            using var client=Client(a);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await runner.RunAsync(lease,client,deadline.Token);
            Check(!Directory.EnumerateFiles(options.DataDirectory,"*",SearchOption.AllDirectories).Any(),"Waiting does not materialize media on the worker.");
            Check(await ScalarAsync<string>("SELECT state FROM execution_waits WHERE execution_id=@id",("id",id))=="waiting","The real runner puts the execution to wait and returns.");
            Check(await runtime.ClaimAsync(a,CancellationToken.None) is null,"The released worker has nothing to claim.");
            Check(await executions.CancelAsync(id,CancellationToken.None),"A waiting execution can be cancelled.");await RecoverCancellationAsync(id);
            Check(await ScalarAsync<long>("SELECT count(*) FROM media_reservations WHERE execution_id=@id",("id",id))==0,"Cancellation releases the media reservation of a waiting execution.");
            await Waits.ProcessDueAsync(CancellationToken.None);
            Check(await ScalarAsync<string>("SELECT state FROM execution_waits WHERE execution_id=@id",("id",id))=="cancelled","The wait of a cancelled execution is closed.");
        }

        public async Task WaitMediaPolicyAsync()
        {
            var transfers=new RuntimeTransferRepository(db);
            // Écraser sans politique de bibliothèque : rien n'est remplacé et le détail le dit.
            var none=await PublicationRequestedAsync(false,1,"overwrite");
            Check(await ScalarAsync<int?>("SELECT replacement_grace_hours FROM execution_work WHERE execution_id=@id",("id",none.Id)) is null,"Overwrite never replaces without an enabled library policy.");
            var status=await executions.GetAsync(none.Id,CancellationToken.None);
            Check(await transfers.ConfirmAsync(none.Id,a,new(none.Lease.Epoch,Guid.NewGuid(),status!.Revision,$".encoding-results/{none.Id:D}/result.mkv",none.ResultSha,null),CancellationToken.None),"Publication confirmed.");
            Check((await executions.GetAsync(none.Id,CancellationToken.None))!.Detail.Contains("pas de politique de remplacement"),"The history explains why the source was kept.");
            // Écraser avec politique active : le remplacement est demandé.
            var over=await PublicationRequestedAsync(true,3,"overwrite");
            Check(await ScalarAsync<int?>("SELECT replacement_grace_hours FROM execution_work WHERE execution_id=@id",("id",over.Id))==3,"Overwrite requests the replacement allowed by the library.");
            // Copie à côté : jamais de remplacement, même si la bibliothèque l'autorise.
            var beside=await PublicationRequestedAsync(true,3,"beside");
            Check(await ScalarAsync<int?>("SELECT replacement_grace_hours FROM execution_work WHERE execution_id=@id",("id",beside.Id)) is null,"The beside policy never replaces the source.");
            status=await executions.GetAsync(beside.Id,CancellationToken.None);
            Check(await transfers.ConfirmAsync(beside.Id,a,new(beside.Lease.Epoch,Guid.NewGuid(),status!.Revision,$".encoding-results/{beside.Id:D}/result.mkv",beside.ResultSha,Replaced(beside.Id)),CancellationToken.None),"Publication confirmed.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM source_replacements WHERE execution_id=@id",("id",beside.Id))==0,"A forged replacement report is ignored for the beside policy.");
        }

        public void WaitContracts()
        {
            Check(!validator.Validate(WaitFlow(new(){["mode"]=J("block"),["blockNodeId"]=J("entry"),["blockOutputs"]=J("success")})).IsValid,"Only plugin conditions can be watched.");
            Check(!validator.Validate(WaitFlow(new(){["mode"]=J("block"),["blockNodeId"]=J("playback"),["blockOutputs"]=J("nope")},false,true)).IsValid,"Unknown watched outputs are refused.");
            Check(!validator.Validate(WaitFlow(new(){["mode"]=J("nodeState"),["nodeStatus"]=J("offline")})).IsValid,"Waiting for a worker to go offline needs a worker.");
            Check(!validator.Validate(WaitFlow(new(){["mode"]=J("timer"),["delayMinutes"]=J(0)})).IsValid,"Out of range delays are refused.");
            var noNext=WaitFlow(new(){["mode"]=J("timer")}); noNext=noNext with { Connections=noNext.Connections.Where(edge=>edge.SourceNodeId!="wait").ToArray() };
            Check(!validator.Validate(noNext).IsValid,"A wait needs a continuation.");
            Check(validator.Validate(WaitFlow(new(){["mode"]=J("timer")})).IsValid,"A timer wait validates with defaults.");
            Check(!eligibility.CanRun(WaitFlow(new(){["mode"]=J("timer")}),Capabilities(false).Select(cap=>cap.ToolId=="plugin/base"?cap with { Version="1.0.0" }:cap).ToArray(),Guid.NewGuid()),"An old agent cannot run the wait block.");
            Check(!validator.Validate(WaitFlow(new(){["mode"]=J("timer"),["mediaPolicy"]=J("delete")})).IsValid,"Unknown media policies are refused.");
            Check(validator.Validate(WaitFlow(new(){["mode"]=J("timer"),["mediaPolicy"]=J("overwrite")})).IsValid,"Overwrite is a valid media policy.");
            var legacy=Node(registry,"v","base-variable",new(){["name"]=J("x"),["value"]=J("1")});
            Check(registry.Resolve(legacy.Component with { PluginVersion="1.0.0" }) is not null,"Existing base references stay resolvable.");
        }
    }
}
