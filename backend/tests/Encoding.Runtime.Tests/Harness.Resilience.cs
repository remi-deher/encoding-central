using System.Security.Cryptography;
using Encoding.Api.Executions;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Nodes;
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
            Check(await ScalarAsync<long>("SELECT count(*) FROM execution_steps WHERE execution_id=@id AND node_id='publish' AND idempotent",("id",id))==1,"Requesting a publication is classified as replayable.");
            var result=System.Text.Encoding.UTF8.GetBytes("result-before-loss-"+id); var resultSha=Convert.ToHexStringLower(SHA256.HashData(result));
            Check(await artifacts.PutAsync(id,a,first.Epoch,resultSha,new MemoryStream(result),false,CancellationToken.None),"Register the validated result.");
            var status=await runtime.RenewAsync(id,a,first.Epoch,CancellationToken.None)??throw new InvalidOperationException("Renew before publishing.");
            Check(await transfers.FinishAsync(id,a,new(first.Epoch,Guid.NewGuid(),status.Revision,"succeeded","Publication demandée",resultSha,".mkv",J(new{v="b"}),J(new{v="a"}),ContinueFlow:true),CancellationToken.None),"The publication is requested.");

            // Le worker disparaît avant que le nœud d'accès ait confirmé : la reprise hérite de la demande.
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

        /// <summary>Une exécution répliquée vers un autre serveur y reste visible dans l'historique mais n'y est jamais attribuable : baux et réservations restent locaux.</summary>
        public async Task ReplicatedExecutionCannotBeClaimedOnAnotherServerAsync()
        {
            var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"end","base-complete-flow"));
            var(id,first,context)=await EnqueueAsync(flow);
            await CompleteAsync(id,a,first,"entry",context);

            var clusterId=Guid.NewGuid(); var key=RandomNumberGenerator.GetBytes(32);
            await new ClusterIdentityRepository(db).AdoptAsync(clusterId,"Media",CancellationToken.None);
            await using var other=await TestDatabase.CreateSqliteAsync("second-server");
            await DatabaseMigrator.ApplyAsync(other.Db,RepositoryRoot(),CancellationToken.None);
            await new ClusterIdentityRepository(other.Db).AdoptAsync(clusterId,"Media",CancellationToken.None);
            var protection=new EphemeralDataProtectionProvider();
            var firstEntities=new ClusterEntityReconciliation(db,new ClusterDataKeyStore(clusterId,key),DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory,"keys"))),NullLogger<ClusterEntityReconciliation>.Instance);
            var otherEntities=new ClusterEntityReconciliation(other.Db,new ClusterDataKeyStore(clusterId,key),protection,NullLogger<ClusterEntityReconciliation>.Instance);
            var sender=new ClusterReconciliationRepository(db); var receiver=new ClusterReconciliationRepository(other.Db);
            await firstEntities.CaptureLocalChangesAsync(CancellationToken.None);
            while(true)
            {
                var events=await sender.GetEventsAfterAsync(clusterId,await receiver.GetVectorAsync(CancellationToken.None),ClusterReconciliationRepository.MaximumBatchSize,CancellationToken.None);
                if(events.Count==0) break;
                await receiver.ApplyIncomingAsync(events,CancellationToken.None);
            }
            await otherEntities.ApplyPendingAsync(CancellationToken.None);

            async Task<long> OnOtherAsync(string sql){ await using var command=other.Db.CreateCommand(sql); return Convert.ToInt64(await command.ExecuteScalarAsync()); }
            Check(await OnOtherAsync($"SELECT count(*) FROM executions WHERE id='{id}'")==1,"The second server sees the execution in the replicated history.");
            Check(await OnOtherAsync("SELECT count(*) FROM execution_work")==0,"Assignment data (leases, work) is not replicated.");
            Check(await OnOtherAsync("SELECT count(*) FROM media_reservations")==0,"Media reservations are not replicated.");

            // Un worker de l'autre serveur ne peut ni réclamer ni reprendre cette exécution, même après expiration du bail d'origine.
            await ExpireAsync(id);
            var presence=new NodePresenceOptions(TimeSpan.FromHours(1));
            var otherRuntime=new RuntimeRepository(other.Db,eligibility,registry,presence,new PluginConfigurationRepository(other.Db,protection),new NodeGroupRepository(other.Db));
            foreach(var worker in new[]{a,b,c}) Check(await otherRuntime.ClaimAsync(worker,CancellationToken.None) is null,"The second server never assigns a replicated execution.");
            Check(await otherRuntime.RenewAsync(id,a,first.Epoch,CancellationToken.None) is null,"A replicated execution has no lease on the second server.");

            // Le serveur d'origine garde la main : sa récupération reste la seule source de reprise.
            await FullRecovery().ProcessOnceAsync(CancellationToken.None);
            var resumed=await runtime.ClaimAsync(b,CancellationToken.None)??throw new InvalidOperationException("The owning server resumes it.");
            Check(resumed.ExecutionId==id,"Only the assigning server resumes the execution.");
            Check(await executions.CancelAsync(id,CancellationToken.None),"Clean up.");await ExpireAsync(id);await RecoverCancellationAsync(id);
            Console.WriteLine("PASS: a replicated execution is visible but unassignable on another server; leases stay local.");
        }

        /// <summary>Stockage partagé : chaque worker a un accès direct. Si le worker qui détenait le média est perdu, un autre worker en accès direct reprend (plus de « nœud source » épinglé sur le worker mort).</summary>
        public async Task ResumeOnAnotherDirectWorkerWhenTheDeadWorkerWasTheAccessNodeAsync()
        {
            var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"variable","base-variable",new(){["name"]=J("kept"),["value"]=J("7")}),Node(registry,"end","base-complete-flow"));
            var(id,first,context)=await EnqueueAsync(flow);
            await CompleteAsync(id,a,first,"entry",context);
            await using(var shared=db.CreateCommand("UPDATE library_node_accesses SET mode='direct',local_path=@root,transfer_source_node_id=NULL WHERE node_id IN (@b,@c) AND library_id=(SELECT library_id FROM execution_work WHERE execution_id=@id)"))
            { shared.Parameters.AddWithValue("root",directory); shared.Parameters.AddWithValue("b",b); shared.Parameters.AddWithValue("c",c); shared.Parameters.AddWithValue("id",id); await shared.ExecuteNonQueryAsync(); }
            await ExpireAsync(id);
            await using(var gone=db.CreateCommand("UPDATE cluster_nodes SET last_seen_at=clock_timestamp()-interval '2 hours' WHERE id=@a")){ gone.Parameters.AddWithValue("a",a); await gone.ExecuteNonQueryAsync(); }
            var recovery=new RuntimeRecoveryService(db,NullLogger<RuntimeRecoveryService>.Instance,new NodeGroupRepository(db),eligibility,new NodePresenceOptions(TimeSpan.FromMinutes(5)));
            await recovery.ProcessOnceAsync(CancellationToken.None);
            var state=await executions.GetAsync(id,CancellationToken.None);
            Check(state!.State=="running","Another worker with direct access to the library keeps the execution alive: "+state.Detail);
            var resumed=await runtime.ClaimAsync(b,CancellationToken.None)??throw new InvalidOperationException("The surviving direct worker must resume the execution.");
            Check(resumed.ExecutionId==id && resumed.Checkpoint?.NextNodeId=="variable","The resume continues at the last checkpoint.");
            Check(resumed.SourceNodeId==b && resumed.SourceRoot==directory,"The resuming worker uses its own direct access, not the lost worker's.");
            Check(await executions.CancelAsync(id,CancellationToken.None),"Clean up.");await ExpireAsync(id);await RecoverCancellationAsync(id);
            Console.WriteLine("PASS: a resume does not stay pinned to the lost worker's own library access.");
        }
    }
}