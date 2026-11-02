using System.Security.Cryptography;
using Encoding.Api.Executions;
using Encoding.Contracts.Executions;
using Encoding.NodeAgent.Runtime;

namespace Encoding.Runtime.Tests;

internal static partial class Harness
{
    internal sealed partial class Fixture
    {
        public async Task DirectInspectionDefersTransfersAndFencesPreparationAsync()
        {
            var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"variable","base-variable",new(){["name"]=J("x"),["value"]=J("1")}),Node(registry,"end","base-complete-flow"));
            var(id,lease,context)=await EnqueueAsync(flow,directRead:true);
            Check(lease.DirectRead && lease.SourceSha256 is null,"A capable access worker starts without a content hash.");
            Check(!eligibility.CanRun(flow,Capabilities(true),b,"variable",true,context),"An old worker cannot resume an inspection-only checkpoint.");
            var transfers=new RuntimeTransferRepository(db);
            Check(!(await transfers.ListAsync(a,CancellationToken.None)).Any(item=>item.ExecutionId==id),"Inspection schedules no source copy or upload.");
            await CompleteAsync(id,a,lease,"entry",context);
            Check(!await runtime.PrepareSourceAsync(id,b,lease.Epoch,CancellationToken.None),"Another worker cannot request preparation.");
            Check(!await runtime.PrepareSourceAsync(id,a,lease.Epoch+1,CancellationToken.None),"A stale epoch cannot request preparation.");
            Check(await runtime.PrepareSourceAsync(id,a,lease.Epoch,CancellationToken.None),"The lease holder can request preparation.");
            Check(await runtime.PrepareSourceAsync(id,a,lease.Epoch,CancellationToken.None),"The request is idempotent.");
            var transfer=(await transfers.ListAsync(a,CancellationToken.None)).Single(item=>item.ExecutionId==id);
            Check(transfer.Observation==context.DirectSource,"Source preparation carries the pre-inspection observation.");
            var step=(await runtime.StartStepAsync(id,a,new(lease.Epoch,"variable"),CancellationToken.None))!;
            var status=(await runtime.RenewAsync(id,a,lease.Epoch,CancellationToken.None))!;
            var cp=new RuntimeCheckpoint("variable",step.InvocationId,"success","end",context,null);
            Check(!await runtime.CompleteStepAsync(id,a,new(lease.Epoch,Guid.NewGuid(),status.Revision,cp),CancellationToken.None),"Inspection checkpoints cannot bypass requested preparation.");
            var bytes=await File.ReadAllBytesAsync(Path.Combine(directory,"source.mkv"));
            var sha=Convert.ToHexStringLower(SHA256.HashData(bytes));
            Check(await artifacts.PutAsync(id,a,lease.Epoch,sha,new MemoryStream(bytes),true,CancellationToken.None),"Durable source transfer still validates its SHA-256.");
            context=context with { CurrentSha256=sha,DirectSource=null };
            cp=cp with { Context=context };
            Check(await runtime.CompleteStepAsync(id,a,new(lease.Epoch,Guid.NewGuid(),status.Revision,cp),CancellationToken.None),"A prepared source returns to the normal artifact contract.");
            await CompleteAsync(id,a,lease,"end",context);
            status=(await runtime.RenewAsync(id,a,lease.Epoch,CancellationToken.None))!;
            Check(await transfers.FinishAsync(id,a,new(lease.Epoch,Guid.NewGuid(),status.Revision,"succeeded","Done",null,null,J(new{streams=1}),J(new{streams=1})),CancellationToken.None),"The upgraded execution completes.");
        }

        public async Task DirectInspectionCannotCheckpointAMutatingBlockAsync()
        {
            var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"remux","base-remux-media"),Node(registry,"end","base-complete-flow"));
            var(id,lease,context)=await EnqueueAsync(flow,directRead:true);
            await CompleteAsync(id,a,lease,"entry",context);
            var step=(await runtime.StartStepAsync(id,a,new(lease.Epoch,"remux"),CancellationToken.None))!;
            var status=(await runtime.RenewAsync(id,a,lease.Epoch,CancellationToken.None))!;
            var forged=new RuntimeCheckpoint("remux",step.InvocationId,"success","end",context,null);
            Check(!await runtime.CompleteStepAsync(id,a,new(lease.Epoch,Guid.NewGuid(),status.Revision,forged),CancellationToken.None),"The backend rejects mutation with an inspection-only context.");
            Check(await executions.CancelAsync(id,CancellationToken.None),"Cancel the rejected fixture.");
            await ExpireAsync(id); await RecoverCancellationAsync(id);
        }

        public async Task DirectInspectionFinishesWithoutCopyOrHashAsync()
        {
            var source=Path.Combine(directory,"direct-input.mkv");
            var tools=new ProcessToolRunner();
            await tools.CheckedAsync("ffmpeg",["-v","error","-y","-f","lavfi","-i","color=size=32x32:duration=0.2","-f","lavfi","-i","anullsrc=r=48000:cl=stereo","-t","0.2","-c:v","mpeg4","-c:a","aac",source],directory,CancellationToken.None);
            var content=await File.ReadAllBytesAsync(source);
            var flow=Chain(Node(registry,"entry","base-input-video"),Node(registry,"prepare","base-prepare-processing"),
                Node(registry,"audio","audio-rules",new(){["processingMode"]=J("plan")}),Node(registry,"execute","base-execute-processing"),Node(registry,"end","base-complete-flow",new(){["result"]=J("success")}));
            flow=flow with { Connections=flow.Connections.Select(edge=>edge.SourceNodeId=="audio"?edge with { SourceOutputId="unchanged" }:edge).ToArray() };
            var(id,lease,_)=await EnqueueAsync(flow,content,directRead:true);
            using var client=Client(a);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await Runner("direct-inspection",out var options).RunAsync(lease,client,deadline.Token);
            var execution=(await executions.GetAsync(id,CancellationToken.None))!;
            Check(execution.State=="succeeded",$"Direct inspection finishes: {execution.Detail}");
            Check(execution.PublishedPath is null,"No unchanged source is published.");
            Check((await executions.PipelineAsync(id,CancellationToken.None)).SelectMany(step=>step.Commands).Count(command=>command.Command.Contains("ffprobe",StringComparison.Ordinal))<=1,
                "One scan supplies both original and current metadata when they refer to the same direct source.");
            // Le sondage d'origine est maintenant fait avant la première étape (publication immédiate des métadonnées) : sa commande
            // n'est rattachée à aucune étape du pipeline, d'où « au plus un » et non « exactement un ».
            Check((await runtime.SourceAsync(id,CancellationToken.None)).SourceSha256 is null,"No SHA-256 source was prepared.");
            Check(!Directory.EnumerateFiles(Path.Combine(options.DataDirectory,"executions"),"*.mkv",SearchOption.AllDirectories).Any(),"No media copy was made in the workspace.");
            var after=await File.ReadAllBytesAsync(Path.Combine(directory,"source.mkv"));
            Check(content.SequenceEqual(after),"The real library source is untouched.");
        }

        public async Task ChangedDirectSourceFailsBeforeAnyPreparationAsync()
        {
            var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"end","base-complete-flow"));
            var(id,lease,context)=await EnqueueAsync(flow,directRead:true);
            var checkpoint=await CompleteAsync(id,a,lease,"entry",context);
            var path=Path.Combine(directory,"source.mkv");
            File.SetLastWriteTimeUtc(path,new DateTime(context.DirectSource!.LastWriteUtcTicks,DateTimeKind.Utc).AddSeconds(10));
            using var client=Client(a);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Runner("changed-inspection",out _).RunAsync(lease with { Checkpoint=checkpoint },client,deadline.Token);
            Check((await executions.GetAsync(id,CancellationToken.None))!.State=="failed","A changed source fails instead of accepting stale metadata.");
            Check((await runtime.SourceAsync(id,CancellationToken.None)).SourceSha256 is null,"A changed source was not copied or hashed.");
        }

        public async Task DirectInspectionPreparesBeforeHandoffAsync()
        {
            var flow=RelayFlow();
            var(id,lease,_)=await EnqueueAsync(flow,directRead:true);
            var transfers=new RuntimeTransferRepository(db);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var client=Client(a);
            var run=Runner("direct-relay",out _).RunAsync(lease,client,deadline.Token);
            RuntimeTransfer? transfer=null;
            while(transfer is null)
            {
                transfer=(await transfers.ListAsync(a,deadline.Token)).SingleOrDefault(item=>item.ExecutionId==id);
                if(transfer is null) await Task.Delay(20,deadline.Token);
            }
            Check(transfer.Observation is not null,"Preparation retains the source observation before a handoff.");
            var content=await File.ReadAllBytesAsync(Path.Combine(directory,"source.mkv"),deadline.Token);
            var sha=Convert.ToHexStringLower(SHA256.HashData(content));
            Check(await artifacts.PutAsync(id,a,lease.Epoch,sha,new MemoryStream(content),true,deadline.Token),"The source is prepared before releasing the lease.");
            await run;
            var received=await runtime.ClaimAsync(b,deadline.Token)??throw new InvalidOperationException("The old receiver must be able to resume after preparation.");
            Check(received.ExecutionId==id && !received.DirectRead && received.Checkpoint!.Context.DirectSource is null
                && received.Checkpoint.Context.CurrentSha256==sha,"Handoff always uses durable SHA-256 artifacts, including an old receiver.");
            Check(await executions.CancelAsync(id,CancellationToken.None),"Cancel the handoff fixture.");
            await ExpireAsync(id); await RecoverCancellationAsync(id);
        }
    }
}
