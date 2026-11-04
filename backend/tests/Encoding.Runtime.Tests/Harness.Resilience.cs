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
        /// <summary>Perte du worker avant confirmation de publication : la tentative échoue et toute publication tardive est refusée.</summary>
        public async Task PublicationRequestedBeforeWorkerLossIsNotCompletedAsync()
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

            // La perte du bail interdit toute publication tardive, même si elle était déjà demandée.
            await ExpireAsync(id);
            await FullRecovery().ProcessOnceAsync(CancellationToken.None);
            Check((await executions.GetAsync(id,CancellationToken.None))!.State=="failed","The attempt fails after lease expiry.");
            Check(await runtime.ClaimAsync(b,CancellationToken.None) is null,"No worker adopts the failed attempt.");
            var execution=(await executions.GetAsync(id,CancellationToken.None))!;
            Check(!await transfers.ConfirmAsync(id,a,new(first.Epoch,Guid.NewGuid(),execution.Revision,$".encoding-results/{id:D}/result.mkv",resultSha,null),CancellationToken.None),"The expired lease cannot publish its result.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM execution_events WHERE execution_id=@id AND state='media.published'",("id",id))==0,"No publication occurs after lease loss.");
            Check(File.Exists(artifacts.PathFor(id,resultSha)),"The validated result artifact remains available for diagnosis.");

        }

        /// <summary>Même avec un accès direct partagé, une perte de bail ne réattribue pas la tentative.</summary>
        public async Task DirectAccessDoesNotAuthorizeRecoveryAsync()
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
            Check(state!.State=="failed","A surviving worker with direct access does not keep the attempt running.");
            Check(await runtime.ClaimAsync(b,CancellationToken.None) is null,"Direct access does not authorize reassignment.");

        }
    }
}
