using System.Security.Cryptography;
using Encoding.Api.Executions;

namespace Encoding.Runtime.Tests;

internal static partial class Harness
{
    internal sealed partial class Fixture
    {
        /// <summary>Publication demandée par un bloc de flow (ADR 0003) : l'exécution reste active et le flow continue sur le fichier publié.</summary>
        public async Task PublishStepKeepsTheExecutionActiveAsync()
        {
            var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"end","base-complete-flow"));
            var(id,lease,context)=await EnqueueAsync(flow);
            var transfers=new RuntimeTransferRepository(db);
            await CompleteAsync(id,a,lease,"entry",context);
            var result=System.Text.Encoding.UTF8.GetBytes("published-by-step-"+id); var resultSha=Convert.ToHexStringLower(SHA256.HashData(result));
            Check(await artifacts.PutAsync(id,a,lease.Epoch,resultSha,new MemoryStream(result),false,CancellationToken.None),"Register the validated result.");
            var status=await runtime.RenewAsync(id,a,lease.Epoch,CancellationToken.None)??throw new InvalidOperationException("Renew before publishing.");

            Check(!await transfers.FinishAsync(id,a,new(lease.Epoch,Guid.NewGuid(),status.Revision,"failed","x",resultSha,".mkv",J(new{v="b"}),J(new{v="a"}),ContinueFlow:true),CancellationToken.None),"A mid-flow publication must be a success with a result.");
            Check(!await transfers.FinishAsync(id,a,new(lease.Epoch,Guid.NewGuid(),status.Revision,"succeeded","x",null,null,J(new{v="b"}),J(new{v="a"}),ContinueFlow:true),CancellationToken.None),"A mid-flow publication needs a result.");
            Check(await transfers.FinishAsync(id,a,new(lease.Epoch,Guid.NewGuid(),status.Revision,"succeeded","Publication demandée",resultSha,".mkv",J(new{v="b"}),J(new{v="a"}),ContinueFlow:true),CancellationToken.None),"The step requests the publication without ending the flow.");
            Check(await ScalarAsync<string>("SELECT state FROM executions WHERE id=@id",("id",id))=="running","The execution stays active while the publication is pending.");
            Check(await ScalarAsync<bool>("SELECT continue_after_publication FROM execution_work WHERE execution_id=@id",("id",id)),"The request remembers that the flow continues.");
            Check(!(await transfers.PublicationStateAsync(id,CancellationToken.None))!.Published,"Nothing is published before the access node confirms.");
            var pending=(await transfers.PublicationStateAsync(id,CancellationToken.None))!;
            Check(pending.Requested && pending.Sha256==resultSha,"A resumed worker can see that this result's publication was already requested and must not request it again.");
            Check((await transfers.ListAsync(a,CancellationToken.None)).Any(item=>item.ExecutionId==id && item.Publish),"The access node sees the publication request.");

            var execution=await executions.GetAsync(id,CancellationToken.None);
            var path=$".encoding-results/{id:D}/result.mkv";
            Check(await transfers.ConfirmAsync(id,a,new(lease.Epoch,Guid.NewGuid(),execution!.Revision,path,resultSha,null),CancellationToken.None),"The access node confirms the publication.");
            Check(await ScalarAsync<string>("SELECT state FROM executions WHERE id=@id",("id",id))=="running","The execution is still running after the publication.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM media_reservations WHERE execution_id=@id",("id",id))==1,"The media stays reserved for the rest of the flow.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM execution_events WHERE execution_id=@id AND state='media.published'",("id",id))==1,"The durable publication event is written.");
            var published=(await transfers.PublicationStateAsync(id,CancellationToken.None))!;
            Check(published.Published && published.Path==path && published.Sha256==resultSha,"The worker can read the published path and digest.");

            status=await runtime.RenewAsync(id,a,lease.Epoch,CancellationToken.None)??throw new InvalidOperationException("Renew after publishing.");
            Check(!await transfers.FinishAsync(id,a,new(lease.Epoch,Guid.NewGuid(),status.Revision,"succeeded","again",resultSha,".mkv",J(new{v="b"}),J(new{v="a"}),ContinueFlow:true),CancellationToken.None),"A second mid-flow publication is refused.");
            Check(!await transfers.FinishAsync(id,a,new(lease.Epoch,Guid.NewGuid(),status.Revision,"succeeded","again",resultSha,".mkv",J(new{v="b"}),J(new{v="a"})),CancellationToken.None),"A result cannot be published a second time at the end of the flow.");

            await CompleteAsync(id,a,lease,"end",context);
            status=await runtime.RenewAsync(id,a,lease.Epoch,CancellationToken.None)??throw new InvalidOperationException("Renew before finishing.");
            Check(await transfers.FinishAsync(id,a,new(lease.Epoch,Guid.NewGuid(),status.Revision,"succeeded","Flow terminé",null,null,J(new{v="b"}),J(new{v="a"})),CancellationToken.None),"The flow ends normally after the publication.");
            Check(await ScalarAsync<string>("SELECT state FROM executions WHERE id=@id",("id",id))=="succeeded","The execution succeeds at the end of the flow.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM media_reservations WHERE execution_id=@id",("id",id))==0,"The media reservation is released at the end of the flow.");
        }
    }
}
