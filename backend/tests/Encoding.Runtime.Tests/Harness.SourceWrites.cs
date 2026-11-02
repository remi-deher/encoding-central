using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Nodes;
using Encoding.NodeAgent.Runtime;
using Encoding.NodeAgent.Runtime.Media;
using Encoding.NodeAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Encoding.Runtime.Tests;

internal static partial class Harness
{
    internal sealed partial class Fixture
    {
        public async Task DeferredSourceWriteEndToEndAsync(bool continueFlow=true)
        {
            Check(sourceWriteBarrier is not null,"A guarded fixture is required.");
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(90));var ct=deadline.Token;
            var tools=new ProcessToolRunner();var scanner=new MediaScanner(tools);
            var source=Path.Combine(directory,"source.mkv");var candidate=Path.Combine(directory,"candidate.mkv");
            await tools.CheckedAsync("ffmpeg",["-v","error","-y","-f","lavfi","-i","color=c=red:size=32x32:duration=0.4","-c:v","ffv1",source],directory,ct);
            var originalBytes=await File.ReadAllBytesAsync(source,ct);var before=await scanner.ScanAsync(source,ct);
            await tools.CheckedAsync("ffmpeg",["-v","error","-y","-f","lavfi","-i","color=c=blue:size=32x32:duration=0.4","-c:v","ffv1",candidate],directory,ct);
            var resultSha=await SafeMediaPaths.DigestAsync(candidate,ct);var after=await scanner.ValidateAsync(candidate,"video",before,ct);
            var flow=Chain(Node(registry,"entry","base-input-video"),Node(registry,"end","base-complete-flow"));
            var(id,lease,context)=await EnqueueAsync(flow,originalBytes);
            Check(await new SourceReplacementRepository(db).SavePolicyAsync(lease.LibraryId!.Value,new(true,1,0),ct),"Enable replacement policy.");
            context=context with { BeforeMetadata=before,CurrentMetadata=before };
            await CompleteAsync(id,a,lease,"entry",context);
            if(!continueFlow) await CompleteAsync(id,a,lease,"end",context);
            await using(var input=File.OpenRead(candidate)) Check(await artifacts.PutAsync(id,a,lease.Epoch,resultSha,input,false,ct),"Register a validated candidate.");
            var peer=Guid.NewGuid();
            await using(var admit=db.CreateCommand("INSERT INTO cluster_peer_admissions(cluster_id,peer_id,public_key,approved_by_peer_id,key_fingerprint,issued_at,signature) SELECT id,@peer,'test-key',@peer,'test-key',clock_timestamp(),'test-signature' FROM cluster_identity"))
            { admit.Parameters.AddWithValue("peer",peer);await admit.ExecuteNonQueryAsync(ct); }
            using var client=Client(a);
            var execution=(await executions.GetAsync(id,ct))!;
            await client.PostAsync<object>($"executions/{id:D}/finish",new FinishRuntimeExecution(lease.Epoch,Guid.NewGuid(),execution.Revision,"succeeded","Publish validated result.",resultSha,".mkv",before,after,ContinueFlow:continueFlow),ct);
            var transfers=await client.GetAsync<RuntimeTransfer[]>("transfers",ct);
            Check(transfers.Single(item=>item.ExecutionId==id).ReplacementGraceHours is null,"Cluster publication never grants an immediate source replacement.");
            Runner("source-write-agent",out var options);Directory.CreateDirectory(options.DataDirectory);
            await File.WriteAllTextAsync(Path.Combine(options.DataDirectory,"node-identity.json"),JsonSerializer.Serialize(new WorkerIdentityState(a,"fixture","fixture",app.Urls.Single()+"/",Token,true),Json),ct);
            var guards=new PublicationGuardRunner(new NoGuardInvoker(),NullLogger<PublicationGuardRunner>.Instance);
            using var service=new RuntimeTransferService(options,new WorkerIdentityStore(options),scanner,guards,localPublicationArtifacts,NullLogger<RuntimeTransferService>.Instance);
            await service.StartAsync(ct);
            var repository=new RuntimeTransferRepository(db,sourceWrites:sourceWriteBarrier);
            while((await repository.PublicationStateAsync(id,ct)) is not { Published:true }) await Task.Delay(50,ct);
            await service.StopAsync(ct);
            var published=Path.Combine(directory,".encoding-results",id.ToString("D"),"result.mkv");
            Check(await SafeMediaPaths.DigestAsync(published,ct)==resultSha,"Validated result publishes while the peer is isolated.");
            Check(await SafeMediaPaths.DigestAsync(source,ct)==context.CurrentSha256,"The source is preserved during isolation.");
            if(!continueFlow) Check(await runtime.RenewAsync(id,a,lease.Epoch,ct) is null,"The final wait invalidates and frees its former worker lease.");
            Check((await executions.GetAsync(id,ct))?.State=="running","Waiting for the destructive operation is not reported as a completed execution.");
            Check((await new SourceReplacementRepository(db).GetAsync(id,ct))?.State=="waiting","Policy replacement is durably visible as waiting.");
            Check(await client.PostAsync<PendingSourceWrite>("source-writes/claim",new {},ct) is null,"Isolation prevents claiming a destructive task.");
            sourceWriteHealth!.ConfirmCatchUp("http://peer.test/",peer,true);sourceWriteHealth!.ConfirmSourceWriteSupport(peer,true);
            var first=(await client.PostAsync<PendingSourceWrite>("source-writes/claim",new {},ct))!;
            sourceWriteHealth.Fail("http://peer.test/","partition before the last write permit");
            await PendingSourceWriteHandler.HandleAsync(first,client,guards,ct);
            Check(await SafeMediaPaths.DigestAsync(source,ct)==context.CurrentSha256,"Lost connectivity after claim cannot modify the source.");
            sourceWriteHealth.ConfirmCatchUp("http://peer.test/",peer,true);sourceWriteHealth.ConfirmSourceWriteSupport(peer,true);
            var resumed=(await client.PostAsync<PendingSourceWrite>("source-writes/claim",new {},ct))!;
            Check(resumed.ClaimId!=first.ClaimId,"Resumption uses a new source-write claim.");
            await PendingSourceWriteHandler.HandleAsync(resumed,client,guards,ct);
            Check(await SafeMediaPaths.DigestAsync(source,ct)==resultSha,"The deferred replacement applies after catch-up.");
            var replacement=(await new SourceReplacementRepository(db).GetAsync(id,ct))!;
            Check(replacement.State=="replaced" && await SafeMediaPaths.DigestAsync(Path.Combine(directory,replacement.BackupPath!),ct)==context.CurrentSha256,"The original remains available in its verified backup.");
            if(continueFlow) Check(await runtime.RenewAsync(id,a,lease.Epoch,ct) is not null,"Completing deferred replacement does not release the lease of a continuing flow.");
            else Check((await executions.GetAsync(id,ct))?.State=="succeeded","A final publication completes only after the deferred source write resolves.");
        }
    }
}
