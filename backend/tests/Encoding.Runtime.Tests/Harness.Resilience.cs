using System.Security.Cryptography;
using Encoding.Api.Executions;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Workers;
using Encoding.Api.Plugins;
using Encoding.Contracts.Executions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Encoding.Runtime.Tests;

internal static partial class Harness
{
    internal sealed partial class Fixture
    {
        /// <summary>Perte du worker entre la demande de publication du bloc et sa confirmation (ADR 0003 + 0004) : la publication est adoptée, jamais refaite.</summary>
        public async Task PublicationRequestedBeforeWorkerLossIsAdoptedNotRepeatedAsync()
        {
            var chain=Chain(Node(registry,"entry","base-input-file"),Node(registry,"publish","base-publish-result"),Node(registry,"end","base-complete-flow"));
            var flow=chain with { Connections=[chain.Connections[0],new("publish-end","publish","published","end")] };
            var(id,first,context)=await EnqueueAsync(flow);
            var transfers=new RuntimeTransferRepository(db);
            await CompleteAsync(id,a,first,"entry",context);
            Check(await runtime.StartStepAsync(id,a,new(first.Epoch,"publish"),CancellationToken.None) is not null,"The publish block starts.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM execution_steps WHERE execution_id=@id AND flow_node_id='publish' AND idempotent",("id",id))==1,"Requesting a publication is classified as replayable.");
            var result=System.Text.Encoding.UTF8.GetBytes("result-before-loss-"+id); var resultSha=Convert.ToHexStringLower(SHA256.HashData(result));
            Check(await artifacts.PutAsync(id,a,first.Epoch,resultSha,new MemoryStream(result),false,CancellationToken.None),"Register the validated result.");
            var status=await runtime.RenewAsync(id,a,first.Epoch,CancellationToken.None)??throw new InvalidOperationException("Renew before publishing.");
            Check(await transfers.FinishAsync(id,a,new(first.Epoch,Guid.NewGuid(),status.Revision,"succeeded","Publication demandée",resultSha,".mkv",J(new{v="b"}),J(new{v="a"}),ContinueFlow:true),CancellationToken.None),"The publication is requested.");

            // Le worker disparaît avant que le worker d'accès ait confirmé : la reprise hérite de la demande.
            await ExpireAsync(id);
            await FullRecovery().ProcessOnceAsync(CancellationToken.None);
            Check((await executions.GetAsync(id,CancellationToken.None))!.State=="running","The execution survives the loss of its worker.");
            var resumed=await runtime.ClaimAsync(b,CancellationToken.None)??throw new InvalidOperationException("Another worker resumes the execution.");
            Check(resumed.ExecutionId==id && resumed.Epoch>first.Epoch && resumed.Checkpoint?.NextNodeId=="publish","The resume re-enters the publish block.");
            var pending=(await transfers.PublicationStateAsync(id,CancellationToken.None))!;
            Check(pending.Requested && !pending.Published && pending.Sha256==resultSha,"The resumed worker sees the request already made for this very result.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM execution_events WHERE execution_id=@id AND state='media.published'",("id",id))==0,"Nothing is published yet.");

            var execution=await executions.GetAsync(id,CancellationToken.None);
            var path=$".encoding-results/{id:D}/result.mkv";
            Check(await transfers.ConfirmAsync(id,a,new(resumed.Epoch,Guid.NewGuid(),execution!.Revision,path,resultSha,null),CancellationToken.None),"The access node confirms the single publication.");
            var published=(await transfers.PublicationStateAsync(id,CancellationToken.None))!;
            Check(published.Published && published.Path==path,"The resumed worker reads the published path.");

            var replayed=await runtime.StartStepAsync(id,b,new(resumed.Epoch,"publish"),CancellationToken.None)??throw new InvalidOperationException("The publish block restarts on the resumed worker.");
            status=await runtime.RenewAsync(id,b,resumed.Epoch,CancellationToken.None)??throw new InvalidOperationException("Renew after the resume.");
            Check(!await transfers.FinishAsync(id,b,new(resumed.Epoch,Guid.NewGuid(),status.Revision,"succeeded","again",resultSha,".mkv",J(new{v="b"}),J(new{v="a"}),ContinueFlow:true),CancellationToken.None),"The resumed worker cannot request the publication again.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM execution_events WHERE execution_id=@id AND state='media.published'",("id",id))==1,"The result is published exactly once.");

            var publishedContext=context with { CurrentSha256=resultSha };
            Check(await runtime.CompleteStepAsync(id,b,new(resumed.Epoch,Guid.NewGuid(),status.Revision,new RuntimeCheckpoint("publish",replayed.InvocationId,"published","end",publishedContext,null,null)),CancellationToken.None),"The publish block completes on its published output.");
            await CompleteAsync(id,b,resumed,"end",context);
            status=await runtime.RenewAsync(id,b,resumed.Epoch,CancellationToken.None)??throw new InvalidOperationException("Renew before finishing.");
            Check(await transfers.FinishAsync(id,b,new(resumed.Epoch,Guid.NewGuid(),status.Revision,"succeeded","Flow terminé",null,null,J(new{v="b"}),J(new{v="a"})),CancellationToken.None),"The resumed flow ends normally.");
            Check(await ScalarAsync<string>("SELECT state FROM executions WHERE id=@id",("id",id))=="succeeded","The execution succeeds.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM media_reservations WHERE execution_id=@id",("id",id))==0,"The media reservation is released.");
            Console.WriteLine("PASS: a publication requested before a worker loss is adopted by the resumed worker, exactly once.");
        }

        /// <summary>Stockage partagé : chaque worker a un accès direct. Si le worker qui détenait le média est perdu, un autre worker en accès direct reprend (plus de « worker source » épinglé sur le worker mort).</summary>
        public async Task ResumeOnAnotherDirectWorkerWhenTheDeadWorkerWasTheAccessNodeAsync()
        {
            var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"variable","base-variable",new(){["name"]=J("kept"),["value"]=J("7")}),Node(registry,"end","base-complete-flow"));
            var(id,first,context)=await EnqueueAsync(flow);
            await CompleteAsync(id,a,first,"entry",context);
            await using(var shared=db.CreateCommand("UPDATE library_worker_accesses SET mode='direct',local_path=@root,transfer_source_worker_id=NULL WHERE worker_id IN (@b,@c) AND library_id=(SELECT library_id FROM execution_work WHERE execution_id=@id)"))
            { shared.Parameters.AddWithValue("root",directory); shared.Parameters.AddWithValue("b",b); shared.Parameters.AddWithValue("c",c); shared.Parameters.AddWithValue("id",id); await shared.ExecuteNonQueryAsync(); }
            await ExpireAsync(id);
            await using(var gone=db.CreateCommand("UPDATE workers SET last_seen_at=(strftime('%Y-%m-%d %H:%M:%f', 'now', '-2 hours') || '000Z') WHERE id=@a")){ gone.Parameters.AddWithValue("a",a); await gone.ExecuteNonQueryAsync(); }
            var recovery=new RuntimeRecoveryService(db,NullLogger<RuntimeRecoveryService>.Instance,new WorkerGroupRepository(db),eligibility,new WorkerPresenceOptions(TimeSpan.FromMinutes(5)));
            await recovery.ProcessOnceAsync(CancellationToken.None);
            var state=await executions.GetAsync(id,CancellationToken.None);
            Check(state!.State=="running","Another worker with direct access to the library keeps the execution alive: "+state.Detail);
            var resumed=await runtime.ClaimAsync(b,CancellationToken.None)??throw new InvalidOperationException("The surviving direct worker must resume the execution.");
            Check(resumed.ExecutionId==id && resumed.Checkpoint?.NextNodeId=="variable","The resume continues at the last checkpoint.");
            Check(resumed.SourceWorkerId==b && resumed.SourceRoot==directory,"The resuming worker uses its own direct access, not the lost worker's.");
            Check(await executions.CancelAsync(id,CancellationToken.None),"Clean up.");await ExpireAsync(id);await RecoverCancellationAsync(id);
            Console.WriteLine("PASS: a resume does not stay pinned to the lost worker's own library access.");
        }
    }
}