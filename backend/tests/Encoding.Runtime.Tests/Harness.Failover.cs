using Encoding.Api.Infrastructure.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Api.Nodes;
using Encoding.Api.Plugins;
using Encoding.Api.Executions;
using Encoding.Api.Libraries;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.NodeAgent.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Encoding.Runtime.Tests;

internal static partial class Harness
{
    internal sealed partial class Fixture
    {
        private async Task<T?> ScalarAsync<T>(string sql,params (string Name,object Value)[] parameters)
        {
            await using var command=db.CreateCommand(sql);
            foreach(var (name,value) in parameters) command.Parameters.AddWithValue(name,value);
            var result=await command.ExecuteScalarAsync();
            if(result is null or DBNull) return default;
            var target=Nullable.GetUnderlyingType(typeof(T))??typeof(T);
            if(result is string text && target==typeof(Guid)) return (T)(object)Guid.Parse(text);
            return target.IsInstanceOfType(result)?(T)result:(T)System.Convert.ChangeType(result,target,System.Globalization.CultureInfo.InvariantCulture);
        }
        private async Task ResetNodesAsync()
        {
            await using var command=db.CreateCommand("UPDATE cluster_nodes SET last_seen_at=clock_timestamp(),max_concurrent_tasks=100,deleted_at=NULL; DELETE FROM node_processing_settings;");
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>Exécution terminée côté worker : résultat enregistré, publication demandée au nœud d'accès (a).</summary>
        private async Task<(Guid Id,RuntimeLease Lease,Guid Library,string SourceSha,string ResultSha)> PublicationRequestedAsync(bool replacement,int graceHours=1,string mediaPolicy="keep")
        {
            var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"end","base-complete-flow"));
            var(id,lease,context)=await EnqueueAsync(flow); var library=lease.LibraryId!.Value;
            if(replacement) Check(await new SourceReplacementRepository(db).SavePolicyAsync(library,new(true,graceHours,0),CancellationToken.None),"Enable the replacement policy.");
            if(mediaPolicy!="keep") await using(var set=db.CreateCommand("UPDATE execution_work SET media_policy=@policy WHERE execution_id=@id")){ set.Parameters.AddWithValue("policy",mediaPolicy); set.Parameters.AddWithValue("id",id); await set.ExecuteNonQueryAsync(); }
            await CompleteAsync(id,a,lease,"entry",context); await CompleteAsync(id,a,lease,"end",context);
            var result=System.Text.Encoding.UTF8.GetBytes("encoded-result-"+id); var resultSha=Convert.ToHexStringLower(SHA256.HashData(result));
            Check(await artifacts.PutAsync(id,a,lease.Epoch,resultSha,new MemoryStream(result),false,CancellationToken.None),"Register the validated result.");
            var status=await runtime.RenewAsync(id,a,lease.Epoch,CancellationToken.None)??throw new InvalidOperationException("Renew before finishing.");
            Check(await new RuntimeTransferRepository(db).FinishAsync(id,a,new(lease.Epoch,Guid.NewGuid(),status.Revision,"succeeded","Result validated",resultSha,".mkv",J(new{v="before"}),J(new{v="after"})),CancellationToken.None),"The worker requests publication.");
            return(id,lease,library,context.CurrentSha256,resultSha);
        }
        private static SourceReplacementReport Replaced(Guid id,long bytes=21)=>new("replaced","source.mkv",SourceReplacer.BackupPath(id,"source.mkv"),bytes,new DateTimeOffset(2024,5,6,7,8,9,TimeSpan.Zero),null);

        public async Task ReplacementLifecycleAsync()
        {
            await ResetNodesAsync();
            var policies=new SourceReplacementRepository(db); var transfers=new RuntimeTransferRepository(db);
            var fresh=Guid.NewGuid();
            await using(var create=db.CreateCommand("INSERT INTO libraries(id,name) VALUES(@id,'policy')")){ create.Parameters.AddWithValue("id",fresh); await create.ExecuteNonQueryAsync(); }
            Check(await policies.GetPolicyAsync(fresh,CancellationToken.None) is { Enabled: false, Revision: 0 },"Source replacement is disabled by default.");
            Check(await policies.GetPolicyAsync(Guid.NewGuid(),CancellationToken.None) is null,"An unknown library has no policy.");
            Check(await policies.SavePolicyAsync(fresh,new(true,24,0),CancellationToken.None) && !await policies.SavePolicyAsync(fresh,new(false,24,0),CancellationToken.None),"A stale policy revision is rejected.");
            var invalid=false; try{ await policies.SavePolicyAsync(fresh,new(true,0,1),CancellationToken.None); }catch(ArgumentException){ invalid=true; }
            Check(invalid && (await policies.GetPolicyAsync(fresh,CancellationToken.None))!.GracePeriodHours==24,"An invalid grace period is rejected without changing the policy.");

            // Policy disabled: the source is never replaced, even if an agent reports a replacement.
            var disabled=await PublicationRequestedAsync(false);
            Check(await ScalarAsync<int?>("SELECT replacement_grace_hours FROM execution_work WHERE execution_id=@id",("id",disabled.Id)) is null,"No replacement is requested while the policy is disabled.");
            var disabledPath=$".encoding-results/{disabled.Id:D}/result.mkv";
            var status=await executions.GetAsync(disabled.Id,CancellationToken.None);
            Check(await transfers.ConfirmAsync(disabled.Id,a,new(disabled.Lease.Epoch,Guid.NewGuid(),status!.Revision,disabledPath,disabled.ResultSha,Replaced(disabled.Id)),CancellationToken.None),"Publication is confirmed.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM source_replacements WHERE execution_id=@id",("id",disabled.Id))==0,"A report received while the policy is disabled is ignored.");

            // Enabled: the request carries the policy snapshot; forged reports are refused.
            var item=await PublicationRequestedAsync(true,2);
            var publication=(await transfers.ListAsync(a,CancellationToken.None)).Single(transfer=>transfer.ExecutionId==item.Id && transfer.Publish);
            Check(publication.ReplacementGraceHours==2,"The transfer request carries the library policy snapshot.");
            Check(await policies.SavePolicyAsync(item.Library,new(false,2,1),CancellationToken.None) && (await transfers.ListAsync(a,CancellationToken.None)).Single(transfer=>transfer.ExecutionId==item.Id).ReplacementGraceHours==2,"Changing the policy later does not alter a request already issued.");
            var path=$".encoding-results/{item.Id:D}/result.mkv"; status=await executions.GetAsync(item.Id,CancellationToken.None);
            Check(!await transfers.ConfirmAsync(item.Id,a,new(item.Lease.Epoch,Guid.NewGuid(),status!.Revision,path,item.ResultSha,Replaced(item.Id) with { BackupPath="elsewhere/original.mkv" }),CancellationToken.None),"A backup outside .encoding-replaced is refused.");
            Check(!await transfers.ConfirmAsync(item.Id,a,new(item.Lease.Epoch,Guid.NewGuid(),status.Revision,path,item.ResultSha,Replaced(item.Id) with { FinalPath="other/source.mkv" }),CancellationToken.None),"A replacement outside the media directory is refused.");
            Check(await ScalarAsync<string>("SELECT state FROM executions WHERE id=@id",("id",item.Id))=="running","Refused reports leave the execution unfinished.");
            var confirm=new ConfirmRuntimePublication(item.Lease.Epoch,Guid.NewGuid(),status.Revision,path,item.ResultSha,Replaced(item.Id));
            Check(await transfers.ConfirmAsync(item.Id,a,confirm,CancellationToken.None),"A valid report completes the execution.");
            Check(!await transfers.ConfirmAsync(item.Id,a,confirm with { EventId=Guid.NewGuid() },CancellationToken.None) && await ScalarAsync<long>("SELECT count(*) FROM source_replacements WHERE execution_id=@id",("id",item.Id))==1,"A retried confirmation through another request is rejected and never records a second replacement.");
            var record=await policies.GetAsync(item.Id,CancellationToken.None);
            Check(record is { State: "replaced" } && record.GraceUntil>DateTimeOffset.UtcNow.AddHours(1.9) && record.SourceSha256==item.SourceSha && record.ResultSha256==item.ResultSha,"The replacement is recorded with its grace deadline and digests.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM media_replacement_marks WHERE execution_id=@id AND relative_path='source.mkv' AND byte_length=21",("id",item.Id))==1,"The produced file is marked so monitoring does not re-queue it.");
            Check((await ScalarAsync<string>("SELECT detail FROM executions WHERE id=@id",("id",item.Id)))!.Contains("source remplacée"),"The execution history states that the source was replaced.");

            // Automatic monitoring must not process the freshly replaced file again.
            await AutomaticMonitoringSkipsReplacedFileAsync(item.Library,item.Id);

            // Rollback is only possible inside the grace period, and reserves the media.
            Check(await policies.RequestRollbackAsync(Guid.NewGuid(),CancellationToken.None)=="missing","Unknown replacements cannot be restored.");
            Check(await policies.RequestRollbackAsync(item.Id,CancellationToken.None)=="ok" && await policies.RequestRollbackAsync(item.Id,CancellationToken.None)=="not-restorable","A rollback is requested once.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM media_reservations WHERE execution_id=@id AND lease_epoch=-1",("id",item.Id))==1,"The media is reserved during the rollback.");
            var task=(await policies.TasksAsync(a,CancellationToken.None)).Single(task=>task.ExecutionId==item.Id);
            Check(task.Action=="rollback" && task.Root==directory && task.BackupPath==Replaced(item.Id).BackupPath,"The access node receives the rollback with the library root.");
            Check((await policies.TasksAsync(b,CancellationToken.None)).Count==0 && !await policies.ReportAsync(item.Id,b,new("rolled_back",null,21,DateTimeOffset.UtcNow),CancellationToken.None),"Only the node that holds the access can report the result.");
            Check(await policies.ReportAsync(item.Id,a,new("failed","Fichier modifié"),CancellationToken.None) && (await policies.GetAsync(item.Id,CancellationToken.None))!.State=="replaced"
                && (await policies.GetAsync(item.Id,CancellationToken.None))!.Detail=="Fichier modifié","A refused rollback returns to the grace period and keeps the reason.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM media_reservations WHERE execution_id=@id AND lease_epoch=-1",("id",item.Id))==0,"The rollback reservation is released after a failure.");
            Check(await policies.RequestRollbackAsync(item.Id,CancellationToken.None)=="ok","The rollback can be retried.");
            Check(!await policies.ReportAsync(item.Id,a,new("finalized",null),CancellationToken.None),"A purge cannot be reported for a rollback.");
            Check(await policies.ReportAsync(item.Id,a,new("rolled_back",null,10,new DateTimeOffset(2024,1,2,3,4,5,TimeSpan.Zero)),CancellationToken.None)
                && (await policies.GetAsync(item.Id,CancellationToken.None))!.State=="rolled_back","The rollback is recorded.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM media_replacement_marks WHERE execution_id=@id AND byte_length=10",("id",item.Id))==1,"The restored original is also protected from automatic re-queueing.");
            Check(await policies.RequestRollbackAsync(item.Id,CancellationToken.None)=="not-restorable","A restored replacement cannot be restored twice.");

            // Grace period over: no rollback, only purge, and a media in use blocks the rollback.
            var expired=await PublicationRequestedAsync(true);
            status=await executions.GetAsync(expired.Id,CancellationToken.None);
            Check(await transfers.ConfirmAsync(expired.Id,a,new(expired.Lease.Epoch,Guid.NewGuid(),status!.Revision,$".encoding-results/{expired.Id:D}/result.mkv",expired.ResultSha,Replaced(expired.Id)),CancellationToken.None),"Second replacement confirmed.");
            Check((await policies.TasksAsync(a,CancellationToken.None)).All(task=>task.ExecutionId!=expired.Id),"Nothing is purged during the grace period.");
            var queued=Guid.NewGuid(); var secondFlow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"end","base-complete-flow"));
            Check(await runtime.EnqueueAsync(new(queued,expired.Library,"source.mkv",secondFlow.Id,secondFlow.Version),secondFlow,CancellationToken.None),"Queue a second execution on the same media.");
            RuntimeLease? busyLease=null; for(var attempt=0;attempt<5 && busyLease?.ExecutionId!=queued;attempt++) busyLease=await runtime.ClaimAsync(a,CancellationToken.None);
            Check(busyLease?.ExecutionId==queued,"The second execution holds the media.");
            Check(await policies.RequestRollbackAsync(expired.Id,CancellationToken.None)=="busy","A media that is being processed cannot be restored concurrently.");
            await using(var elapse=db.CreateCommand("UPDATE source_replacements SET grace_until=clock_timestamp()-interval '1 second',state='replaced' WHERE execution_id=@id")){ elapse.Parameters.AddWithValue("id",expired.Id); await elapse.ExecuteNonQueryAsync(); }
            Check(await policies.RequestRollbackAsync(expired.Id,CancellationToken.None)=="not-restorable","A rollback is refused once the grace period is over.");
            Check((await policies.TasksAsync(a,CancellationToken.None)).Single(task=>task.ExecutionId==expired.Id).Action=="finalize","The backup becomes due for purge after the grace period.");
            Check(await policies.ReportAsync(expired.Id,a,new("failed","Fichier remplaçant absent"),CancellationToken.None) && (await policies.GetAsync(expired.Id,CancellationToken.None))!.State=="retained","A refused purge keeps the backup and flags it for manual action.");
            Check((await policies.TasksAsync(a,CancellationToken.None)).All(task=>task.ExecutionId!=expired.Id),"A retained backup is never retried automatically.");
            Console.WriteLine("PASS: replacement policy (disabled by default), forged-report refusal, grace period, rollback reservation, purge and monitoring protection.");
        }

        private async Task AutomaticMonitoringSkipsReplacedFileAsync(Guid library,Guid execution)
        {
            var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"end","base-complete-flow")) with { Id=Guid.NewGuid() };
            var repository=new FlowRepository(db); Check(await repository.SaveAsync(new(0,flow),CancellationToken.None) is not null,"Publish the monitoring flow.");
            var workflow=new LibraryWorkflowRepository(db,validator,new NodePresenceOptions(TimeSpan.FromHours(1)));
            Check(await workflow.AssignAsync(library,new(flow.Id,1,0),CancellationToken.None),"Assign the flow to the library.");
            var modified=new DateTimeOffset(2024,5,6,7,8,9,TimeSpan.Zero);
            var scan=Guid.NewGuid();
            await using(var file=db.CreateCommand("""
                INSERT INTO library_scans(id,library_id,media_type,state,completed_at) VALUES(@scan,@library,'video','completed',clock_timestamp());
                INSERT INTO library_media(id,library_id,relative_path,byte_length,modified_at,scan_id,stable_since) VALUES(gen_random_uuid(),@library,'source.mkv',21,@modified,@scan,clock_timestamp()-interval '1 hour')
                """))
            { file.Parameters.AddWithValue("scan",scan); file.Parameters.AddWithValue("library",library); file.Parameters.AddWithValue("modified",modified); await file.ExecuteNonQueryAsync(); }
            var before=await ScalarAsync<long>("SELECT count(*) FROM executions");
            await workflow.EnqueueAutomaticallyAsync(library,CancellationToken.None);
            Check(await ScalarAsync<long>("SELECT count(*) FROM executions")==before,"The file produced by a replacement is not queued again by automatic monitoring.");
            await using(var changed=db.CreateCommand("UPDATE library_media SET modified_at=@modified WHERE library_id=@library")){ changed.Parameters.AddWithValue("library",library); changed.Parameters.AddWithValue("modified",modified.AddDays(1)); await changed.ExecuteNonQueryAsync(); }
            await workflow.EnqueueAutomaticallyAsync(library,CancellationToken.None);
            Check(await ScalarAsync<long>("SELECT count(*) FROM executions")==before+1,"A later modification of the file makes it eligible again.");
            await using(var cleanup=db.CreateCommand("UPDATE executions SET state='cancelled' WHERE state='queued' AND id<>@id")){ cleanup.Parameters.AddWithValue("id",execution); await cleanup.ExecuteNonQueryAsync(); }
        }

        /// <summary>Service de reprise complet (règles d'éligibilité et de présence), comme en production.</summary>
        private RuntimeRecoveryService FullRecovery()=>new(db,NullLogger<RuntimeRecoveryService>.Instance,new NodeGroupRepository(db),eligibility,new NodePresenceOptions(TimeSpan.FromHours(1)));

        /// <summary>ADR 0024 : une exécution neuve qu'aucun nœud ne pourra jamais faire échoue tôt et explicitement ; une exécution possible reste en file.</summary>
        public async Task FreshExecutionWithoutAnyCapableNodeFailsEarlyAsync()
        {
            await using(var leftovers=db.CreateCommand("UPDATE executions SET state='cancelled' WHERE state='queued'")) await leftovers.ExecuteNonQueryAsync();
            await ResetNodesAsync();
            async Task<Guid> QueueAsync(FlowDefinition flow)
            {
                var library=Guid.NewGuid(); var id=Guid.NewGuid();
                await using(var command=db.CreateCommand("""
                    INSERT INTO libraries(id,name) VALUES(@library,@name);
                    INSERT INTO library_node_accesses(library_id,node_id,mode,local_path,can_read,can_write) VALUES(@library,@a,'direct',@root,true,true);
                    """))
                {
                    command.Parameters.AddWithValue("library",library); command.Parameters.AddWithValue("name",library.ToString());
                    command.Parameters.AddWithValue("root",directory); command.Parameters.AddWithValue("a",a);
                    await command.ExecuteNonQueryAsync();
                }
                Check(await runtime.EnqueueAsync(new(id,library,"source.mkv",flow.Id,flow.Version),flow,CancellationToken.None),"Enqueue fixture.");
                await using var aged=db.CreateCommand("UPDATE executions SET queued_at=clock_timestamp()-interval '10 minutes' WHERE id=@id");
                aged.Parameters.AddWithValue("id",id); await aged.ExecuteNonQueryAsync();
                return id;
            }
            var impossible=await QueueAsync(Chain(Node(registry,"entry","base-input-file"),Node(registry,"need","nodes-require-capability",new(){["toolId"]=J("outil-introuvable")}),Node(registry,"end","base-complete-flow")));
            var possible=await QueueAsync(Chain(Node(registry,"entry","base-input-file"),Node(registry,"end","base-complete-flow")));
            await FullRecovery().ProcessOnceAsync(CancellationToken.None);
            var failed=await executions.GetAsync(impossible,CancellationToken.None);
            Check(failed!.State=="failed","Une exécution qu'aucun nœud ne peut faire échoue tôt.");
            Check((await executions.GetAsync(possible,CancellationToken.None))!.State=="queued","Une exécution possible reste en file.");
            Check(await executions.CancelAsync(possible,CancellationToken.None),"Nettoyage.");
        }

        public async Task WorkerLossFailoverAsync()
        {
            await ResetNodesAsync();
            var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"variable","base-variable",new(){["name"]=J("kept"),["value"]=J("7")}),Node(registry,"end","base-complete-flow"));
            var(id,first,context)=await EnqueueAsync(flow);
            context=context with { Variables=new Dictionary<string,JsonElement>{["kept"]=J(7)} };
            await CompleteAsync(id,a,first,"entry",context); await CompleteAsync(id,a,first,"variable",context);

            await ExpireAsync(id);
            var claims=await Task.WhenAll(runtime.ClaimAsync(a,CancellationToken.None),runtime.ClaimAsync(b,CancellationToken.None),runtime.ClaimAsync(c,CancellationToken.None));
            Check(claims.All(claim=>claim is null),"An expired assigned task is not silently resumed by any worker before recovery.");
            var recovery=FullRecovery();
            await recovery.ProcessOnceAsync(CancellationToken.None);
            Check((await executions.GetAsync(id,CancellationToken.None))!.State=="running","The interrupted execution stays running while it waits for a worker.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM media_reservations WHERE execution_id=@id",("id",id))==1,"The media stays reserved while the execution waits for a worker.");
            Check(await ScalarAsync<int>("SELECT resume_count FROM execution_work WHERE execution_id=@id",("id",id))==1,"The resume is counted.");
            Check(await runtime.RenewAsync(id,a,first.Epoch,CancellationToken.None) is null,"The disconnected worker is fenced once the execution is handed back.");

            var resumed=await runtime.ClaimAsync(b,CancellationToken.None)??throw new InvalidOperationException("A surviving eligible worker must resume the execution.");
            Check(resumed.ExecutionId==id && resumed.Epoch>first.Epoch,"The resume keeps the execution ID under a new fencing epoch.");
            Check(resumed.Checkpoint?.NextNodeId=="end" && resumed.Checkpoint.Context.Variables["kept"].GetInt32()==7,"The resume continues at the last durable checkpoint with its variables.");
            Check(await runtime.ClaimAsync(c,CancellationToken.None) is null,"Only one worker takes the resumed execution.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM execution_events WHERE execution_id=@id AND detail LIKE '%reprise 1/2%'",("id",id))==1,"The history says that the execution was resumed.");

            // Deuxième perte : nouvelle reprise ; troisième : la borne est atteinte, l'exécution échoue et libère le média.
            await ExpireAsync(id); await recovery.ProcessOnceAsync(CancellationToken.None);
            var second=await runtime.ClaimAsync(c,CancellationToken.None)??throw new InvalidOperationException("A second resume is allowed.");
            Check(second.Epoch>resumed.Epoch && second.Checkpoint?.NextNodeId=="end","The second resume starts from the same checkpoint.");
            await ExpireAsync(id); await recovery.ProcessOnceAsync(CancellationToken.None);
            var exhausted=await executions.GetAsync(id,CancellationToken.None);
            Check(exhausted!.State=="failed" && exhausted.Detail?.Contains("déjà été reprise",StringComparison.Ordinal)==true,"The resume limit fails the execution with an explicit reason.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM media_reservations WHERE execution_id=@id",("id",id))==0,"Failure releases the media reservation.");

            await using(var lost=db.CreateCommand("UPDATE cluster_nodes SET max_concurrent_tasks=0 WHERE id=@id")){ lost.Parameters.AddWithValue("id",a); await lost.ExecuteNonQueryAsync(); }
            var retryId=await executions.RetryAsync(id,CancellationToken.None)??throw new InvalidOperationException("An explicit retry must create a new execution.");
            var retry=await runtime.ClaimAsync(b,CancellationToken.None)??throw new InvalidOperationException("The eligible surviving worker must claim the explicit retry.");
            Check(retry.ExecutionId==retryId && retry.ExecutionId!=id && retry.Attempt==first.Attempt+1,"An explicit retry uses a new execution ID and increments the attempt number.");
            Check(retry.Checkpoint is null && retry.EntryNodeId=="entry","A retry starts the flow again instead of resuming the interrupted checkpoint.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM execution_events WHERE execution_id=@id AND state='failed'",("id",id))==1,"The failed state remains in the execution history.");
            Check(await executions.CancelAsync(retryId,CancellationToken.None),"Clean up the explicit retry.");await ExpireAsync(retryId);await RecoverCancellationAsync(retryId);
            Console.WriteLine("PASS: worker loss resumes the execution at its checkpoint on another worker, bounded; explicit retry still starts afresh.");
        }

        public async Task ResumeWaitsForAnEligibleWorkerAsync()
        {
            await ResetNodesAsync();
            var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"variable","base-variable",new(){["name"]=J("kept"),["value"]=J("7")}),Node(registry,"end","base-complete-flow"));
            var(id,first,context)=await EnqueueAsync(flow);
            await CompleteAsync(id,a,first,"entry",context);
            await ExpireAsync(id);
            var recovery=FullRecovery();
            await recovery.ProcessOnceAsync(CancellationToken.None);
            await using(var gone=db.CreateCommand("UPDATE cluster_nodes SET max_concurrent_tasks=0 WHERE id IN (@a,@b,@c)")){ gone.Parameters.AddWithValue("a",a); gone.Parameters.AddWithValue("b",b); gone.Parameters.AddWithValue("c",c); await gone.ExecuteNonQueryAsync(); }
            await recovery.ProcessOnceAsync(CancellationToken.None);
            Check((await executions.GetAsync(id,CancellationToken.None))!.State=="running","A restarting worker has a grace period before a resume is declared impossible.");
            await using(var old=db.CreateCommand("UPDATE executions SET updated_at=clock_timestamp()-interval '10 minutes' WHERE id=@id")){ old.Parameters.AddWithValue("id",id); await old.ExecuteNonQueryAsync(); }
            await recovery.ProcessOnceAsync(CancellationToken.None);
            var failed=await executions.GetAsync(id,CancellationToken.None);
            Check(failed!.State=="failed" && failed.Detail?.Contains("Aucun worker éligible",StringComparison.Ordinal)==true,"With no eligible worker left the resume fails with an explicit reason.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM media_reservations WHERE execution_id=@id",("id",id))==0,"The failure releases the media reservation.");
            Console.WriteLine("PASS: a resume waits for an eligible worker, then fails explicitly.");
        }

        /// <summary>Le seul destinataire capable du relais est occupé : l'exécution attend qu'il se libère au lieu d'échouer.</summary>
        public async Task RelayWaitsForABusyWorkerAsync()
        {
            await ResetNodesAsync();
            var(id,lease,context)=await EnqueueAsync(RelayFlow());
            await CompleteAsync(id,a,lease,"entry",context);await CompleteAsync(id,a,lease,"variable",context);await CompleteAsync(id,a,lease,"relay",context);
            var(busyId,_,_)=await EnqueueAsync(RelayFlow(),clearUnassignedRunning:false);
            await using(var setup=db.CreateCommand("UPDATE cluster_nodes SET max_concurrent_tasks=CASE WHEN id=@b THEN 1 ELSE 0 END; UPDATE execution_work SET worker_node_id=@b WHERE execution_id=@busy")){ setup.Parameters.AddWithValue("b",b); setup.Parameters.AddWithValue("busy",busyId); await setup.ExecuteNonQueryAsync(); }
            await FullRecovery().ProcessOnceAsync(CancellationToken.None);
            Check((await executions.GetAsync(id,CancellationToken.None))!.State=="running","A relay whose only capable destination is busy waits instead of failing.");
            await ExpireAsync(busyId);
            Check(await runtime.ClaimAsync(b,CancellationToken.None) is { } received && received.ExecutionId==id,"The destination takes the waiting relay once it is free.");
            Check(await executions.CancelAsync(id,CancellationToken.None),"Clean up the relay.");await ExpireAsync(id);await RecoverCancellationAsync(id);
            Check(await executions.CancelAsync(busyId,CancellationToken.None),"Clean up the busy execution.");await RecoverCancellationAsync(busyId);
            Console.WriteLine("PASS: a relay waits for a busy destination.");
        }

        public async Task ReplayableStepIsResumedAfterWorkerLossAsync()
        {
            await ResetNodesAsync();
            var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"plex-refresh","plex-refresh-library"),Node(registry,"end","base-complete-flow"));
            var(id,first,context)=await EnqueueAsync(flow);
            await CompleteAsync(id,a,first,"entry",context);
            var started=await runtime.StartStepAsync(id,a,new(first.Epoch,"plex-refresh"),CancellationToken.None)??throw new InvalidOperationException("The refresh starts.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM execution_steps WHERE execution_id=@id AND node_id='plex-refresh' AND idempotent",("id",id))==1,"Refreshing a media server is classified as replayable.");
            await ExpireAsync(id);
            await new RuntimeRecoveryService(db,NullLogger<RuntimeRecoveryService>.Instance).ProcessOnceAsync(CancellationToken.None);
            var resumed=await runtime.ClaimAsync(b,CancellationToken.None)??throw new InvalidOperationException("Another worker resumes the unfinished replayable step.");
            Check(resumed.ExecutionId==id && resumed.Checkpoint?.NextNodeId=="plex-refresh","The resume re-enters the interrupted step from the previous checkpoint.");
            var again=await runtime.StartStepAsync(id,b,new(resumed.Epoch,"plex-refresh"),CancellationToken.None);
            Check(again is not null && again.InvocationId==started.InvocationId,"The replayed step keeps its invocation identity.");
            Check(await executions.CancelAsync(id,CancellationToken.None),"Clean up.");await ExpireAsync(id);await RecoverCancellationAsync(id);
            Console.WriteLine("PASS: an interrupted replayable step is replayed by the resuming worker.");
        }

        public async Task ResumeKeepsTheRelayDestinationAsync()
        {
            await ResetNodesAsync();
            var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"variable","base-variable",new(){["name"]=J("kept"),["value"]=J("7")}),Node(registry,"end","base-complete-flow"));
            var(id,first,context)=await EnqueueAsync(flow);
            await CompleteAsync(id,a,first,"entry",context);
            // Le dernier relais du flow désignait le nœud b (encodage confié à un seul nœud) : sa destination reste la règle de la reprise.
            await using(var relay=db.CreateCommand("UPDATE execution_work SET handoff_selector=@selector WHERE execution_id=@id"))
            { relay.Parameters.AddWithValue("id",id); relay.Parameters.AddWithValue("selector",SqlKind.Json,JsonSerializer.Serialize(new RuntimeHandoffSelector("node",b),new JsonSerializerOptions(JsonSerializerDefaults.Web))); await relay.ExecuteNonQueryAsync(); }
            await ExpireAsync(id);
            var recovery=FullRecovery();
            await recovery.ProcessOnceAsync(CancellationToken.None);
            Check(await runtime.ClaimAsync(c,CancellationToken.None) is null && await runtime.ClaimAsync(a,CancellationToken.None) is null,"A resume never leaves the node chosen by the last relay while that node is available.");
            var resumed=await runtime.ClaimAsync(b,CancellationToken.None)??throw new InvalidOperationException("The relay destination resumes the execution.");
            Check(resumed.ExecutionId==id && resumed.Checkpoint?.NextNodeId=="variable","The destination resumes at the checkpoint.");
            // Si ce nœud est perdu à son tour, la reprise ne part pas ailleurs : elle échoue après le délai de grâce.
            await ExpireAsync(id);
            await recovery.ProcessOnceAsync(CancellationToken.None);
            await using(var aged=db.CreateCommand("UPDATE executions SET updated_at=clock_timestamp()-interval '10 minutes' WHERE id=@id")){ aged.Parameters.AddWithValue("id",id); await aged.ExecuteNonQueryAsync(); }
            await using(var down=db.CreateCommand("UPDATE cluster_nodes SET max_concurrent_tasks=0 WHERE id=@b")){ down.Parameters.AddWithValue("b",b); await down.ExecuteNonQueryAsync(); }
            await recovery.ProcessOnceAsync(CancellationToken.None);
            Check((await executions.GetAsync(id,CancellationToken.None))!.State=="failed","With the relay destination unavailable the resume fails instead of moving to another node.");
            Console.WriteLine("PASS: a resume keeps the destination of the last relay.");
        }

        public async Task RetryPolicyKeepsPriorityOverResumeAsync()
        {
            await ResetNodesAsync();
            var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"retry","nodes-retry-on-worker-loss"),Node(registry,"end","base-complete-flow"));
            var(id,_,_)=await EnqueueAsync(flow);
            await ExpireAsync(id);
            await new RuntimeRecoveryService(db,NullLogger<RuntimeRecoveryService>.Instance).ProcessOnceAsync(CancellationToken.None);
            Check((await executions.GetAsync(id,CancellationToken.None))!.State=="failed","A flow with its own retry rule keeps fail-then-fresh-attempt semantics.");
            var retryId=await ScalarAsync<Guid>("SELECT id FROM executions WHERE retry_of=@id",("id",id));
            Check(await executions.CancelAsync(retryId,CancellationToken.None),"Clean up.");await ExpireAsync(retryId);await RecoverCancellationAsync(retryId);
            Console.WriteLine("PASS: the explicit retry rule of a flow takes priority over the automatic resume.");
        }

        public async Task CancelledExecutionCanBeResumedAsFreshAttemptAsync()
        {
            await ResetNodesAsync();
            var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"middle","base-variable",new(){["name"]=J("step"),["value"]=J("done")}),Node(registry,"end","base-complete-flow"));
            var(id,lease,context)=await EnqueueAsync(flow);
            await CompleteAsync(id,a,lease,"entry",context);
            Check(await executions.CancelAsync(id,CancellationToken.None),"The running execution can be cancelled before resuming.");
            await ExpireAsync(id);
            await RecoverCancellationAsync(id);

            var resumedId=await executions.ResumeAsync(id,CancellationToken.None)??throw new InvalidOperationException("A cancelled execution must create a fresh resumed attempt.");
            var resumed=await runtime.ClaimAsync(a,CancellationToken.None)??throw new InvalidOperationException("A worker must claim the resumed attempt.");
            Check(resumed.ExecutionId==resumedId && resumed.ExecutionId!=id && resumed.Attempt==lease.Attempt+1,"Resume creates a new execution and increments the attempt.");
            Check(resumed.Checkpoint is null && resumed.EntryNodeId=="entry","Resume starts over from the original source and flow entry.");
            Check(await ScalarAsync<Guid>("SELECT retry_of FROM executions WHERE id=@id",("id",resumedId))==id,"The resumed attempt links back to the interrupted execution.");
            Check(await executions.ResumeAsync(id,CancellationToken.None) is null,"A cancelled execution can only be resumed once.");
            Check((await executions.GetAsync(id,CancellationToken.None))!.State=="cancelled","The original execution remains intact for history.");
            Check(await executions.CancelAsync(resumedId,CancellationToken.None),"The resumed fixture can be cancelled for cleanup.");
            await ExpireAsync(resumedId);
            await RecoverCancellationAsync(resumedId);
            Console.WriteLine("PASS: resuming a cancelled execution creates one new attempt from the original source.");
        }

        public async Task QuarantinedCancelledExecutionCannotBeResumedAsync()
        {
            await ResetNodesAsync();
            var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"end","base-complete-flow"));
            var(id,_,_)=await EnqueueAsync(flow);
            Check(await executions.CancelAsync(id,CancellationToken.None),"The synthetic execution is cancelled before resume.");
            await ExpireAsync(id);
            await RecoverCancellationAsync(id);
            await using(var quarantine=db.CreateCommand("INSERT INTO task_quarantines(execution_id,winner_id,reason) VALUES(@id,@winner,'synthetic duplicate')"))
            {quarantine.Parameters.AddWithValue("id",id);quarantine.Parameters.AddWithValue("winner",Guid.NewGuid());await quarantine.ExecuteNonQueryAsync();}

            Check(await executions.ResumeAsync(id,CancellationToken.None) is null,
                "A quarantined execution cannot create a new attempt from the same media.");
            Check(await ScalarAsync<int>("SELECT count(*) FROM executions WHERE retry_of=@id",("id",id))==0,
                "No queued execution bypasses the quarantine decision.");
            Check((await executions.GetAsync(id,CancellationToken.None))!.State=="cancelled",
                "The quarantined source execution remains intact for reconciliation.");
            Console.WriteLine("PASS: quarantined task ownership blocks resume without altering its source execution.");
        }

        public async Task BoundedFlowRetryAsync()
        {
            await ResetNodesAsync();
            var entry=Node(registry,"entry","base-input-file");
            var policy=Node(registry,"retry","nodes-retry-on-worker-loss");
            var flow=Chain(entry,policy,Node(registry,"end","base-complete-flow"));
            var(id,first,_)=await EnqueueAsync(flow);
            await ExpireAsync(id);
            var recovery=new RuntimeRecoveryService(db,NullLogger<RuntimeRecoveryService>.Instance);
            await recovery.ProcessOnceAsync(CancellationToken.None);
            Check((await executions.GetAsync(id,CancellationToken.None))!.State=="failed","The original lost attempt is recorded as failed.");
            var retryId=await ScalarAsync<Guid>("SELECT id FROM executions WHERE retry_of=@id",("id",id));
            Check(retryId!=Guid.Empty,"The flow policy creates a fresh attempt automatically.");
            Check(await ScalarAsync<int>("SELECT attempt FROM executions WHERE id=@id",("id",retryId))==first.Attempt+1,"The automatic retry increments the attempt count.");
            Check(await ScalarAsync<Guid>("SELECT previous_worker_node_id FROM execution_work WHERE execution_id=@id",("id",retryId))==a,"The failed worker is excluded from its automatic retry.");
            await using(var unavailable=db.CreateCommand("UPDATE cluster_nodes SET max_concurrent_tasks=0 WHERE id=@id")){ unavailable.Parameters.AddWithValue("id",a); await unavailable.ExecuteNonQueryAsync(); }
            var retry=await runtime.ClaimAsync(b,CancellationToken.None)??throw new InvalidOperationException("The retry must go to another eligible worker.");
            Check(retry.ExecutionId==retryId && retry.Checkpoint is null && retry.EntryNodeId=="entry","A flow retry starts at its own entry with isolated artifacts.");
            await ExpireAsync(retryId);
            await recovery.ProcessOnceAsync(CancellationToken.None);
            Check((await executions.GetAsync(retryId,CancellationToken.None))!.State=="failed","A second lost attempt also fails.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM executions WHERE retry_of=@id",("id",retryId))==0,"The flow's one-retry limit prevents an unbounded loop.");
            Console.WriteLine("PASS: flow retry policy is opt-in, excludes the failed worker, starts a new execution, and obeys its retry limit.");
        }

        public async Task UncertainPluginStepIsNotRepeatedAfterWorkerLossAsync()
        {
            await ResetNodesAsync();
            var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"plex-refresh","plex-refresh-library"),Node(registry,"end","base-complete-flow"));
            var(id,first,context)=await EnqueueAsync(flow);
            await CompleteAsync(id,a,first,"entry",context);
            Check(await runtime.StartStepAsync(id,a,new(first.Epoch,"plex-refresh"),CancellationToken.None) is not null,
                "The external plugin step starts and is recorded before its side effect.");
            // Le classement par composant est vérifié à part (RuntimeStepEffectsTests) ; ici l'étape est marquée à effet incertain.
            await using(var mark=db.CreateCommand("UPDATE execution_steps SET idempotent=false WHERE execution_id=@id AND node_id='plex-refresh'")){ mark.Parameters.AddWithValue("id",id); await mark.ExecuteNonQueryAsync(); }

            await ExpireAsync(id);
            Check(await runtime.ClaimAsync(b,CancellationToken.None) is null,
                "A new worker does not repeat a plugin step whose external side effect is uncertain.");
            var recovery=new RuntimeRecoveryService(db,NullLogger<RuntimeRecoveryService>.Instance);
            await recovery.ProcessOnceAsync(CancellationToken.None);
            var result=await executions.GetAsync(id,CancellationToken.None)??throw new InvalidOperationException("The plugin execution remains observable.");
            Check(result.State=="failed" && result.Detail?.Contains("incertain",StringComparison.OrdinalIgnoreCase)==true,
                "The execution fails with an explicit uncertain-side-effect reason and requires a deliberate retry.");
            Check(await runtime.ClaimAsync(a,CancellationToken.None) is null && await runtime.ClaimAsync(c,CancellationToken.None) is null,
                "No worker replays the unfinished plugin invocation automatically.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM execution_steps WHERE execution_id=@id AND node_id='plex-refresh' AND completed_at IS NULL AND NOT idempotent",("id",id))==1,
                "The unfinished non-idempotent plugin invocation remains recorded for diagnosis.");
            Check(await ScalarAsync<long>("SELECT count(*) FROM media_reservations WHERE execution_id=@id",("id",id))==0,
                "The failed uncertain plugin execution releases its media reservation.");
        }

        private async Task<Guid> QueueAsync(FlowDefinition flow)
        {
            var library=Guid.NewGuid(); var id=Guid.NewGuid();
            await using var command=db.CreateCommand("""
                INSERT INTO libraries(id,name) VALUES(@library,@name);
                INSERT INTO library_node_accesses(library_id,node_id,mode,local_path,can_read,can_write) VALUES(@library,@a,'direct',@root,true,true);
                INSERT INTO library_node_accesses(library_id,node_id,mode,can_read,can_write,transfer_source_node_id) VALUES(@library,@b,'transfer',true,true,@a),(@library,@c,'transfer',true,true,@a);
                """);
            command.Parameters.AddWithValue("library",library); command.Parameters.AddWithValue("name",library.ToString()); command.Parameters.AddWithValue("root",directory);
            command.Parameters.AddWithValue("a",a); command.Parameters.AddWithValue("b",b); command.Parameters.AddWithValue("c",c); await command.ExecuteNonQueryAsync();
            Check(await runtime.EnqueueAsync(new(id,library,"queued-"+id.ToString("N")+".mkv",flow.Id,flow.Version),flow,CancellationToken.None),"Queue fixture.");
            return id;
        }

    }
}
