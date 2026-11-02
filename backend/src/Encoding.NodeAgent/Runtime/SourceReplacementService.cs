using Encoding.Contracts.Executions;
using Encoding.NodeAgent.Services;

namespace Encoding.NodeAgent.Runtime;

/// <summary>Exécute, sur le nœud ayant l'accès direct, les restaurations demandées et la purge des sauvegardes après la période de grâce.</summary>
public sealed class SourceReplacementService(NodeAgentOptions options,WorkerIdentityStore identities,PublicationGuardRunner guards,ILogger<SourceReplacementService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            var handled=false;
            try
            {
                var identity=await identities.GetAsync(ct);
                if(identity.IsEnrolled)
                {
                    foreach(var serverUrl in identity.GetCandidateServerUrls(options))
                    {
                        try
                        {
                            using var client=new RuntimeClient(identity,serverUrl);
                            try
                            {
                                if(await client.PostAsync<PendingSourceWrite>("source-writes/claim",new {},ct) is {} pending)
                                {
                                    handled|=await PendingSourceWriteHandler.HandleAsync(pending,client,guards,ct);
                                }
                            }
                            catch(HttpRequestException exception) when(exception.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed) { }

                            foreach(var task in await client.GetAsync<RuntimeReplacementTask[]>("replacements",ct)) {handled=true;await HandleAsync(task,client,ct);}
                        }
                        catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
                        catch(Exception exception) { logger.LogDebug(exception, "Claim ou liste de remplacement momentanément indisponible."); }
                    }
                }
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { break; }
            catch(Exception exception) { logger.LogWarning("Canal de remplacement momentanément indisponible : {Type}.",exception.GetType().Name); }
            try { await WorkerPolling.DelayAsync(handled?2000:30000,ct); } catch(OperationCanceledException) { break; }
        }
    }
    private async Task HandleAsync(RuntimeReplacementTask task,RuntimeClient client,CancellationToken ct)
    {
        ReportReplacementTask report;
        try
        {
            using var mediaLock=SafeMediaPaths.Lock(SafeMediaPaths.Resolve(task.Root,task.OriginalPath),Path.Combine(options.DataDirectory,"media-locks"));
            if(task.SourceWriteGuardsSupported) await client.PostAsync<object>($"replacements/{task.ExecutionId:D}/authorize",new SourceWriteLocation(task.Root),ct);
            if(task.Action=="rollback")
            {
                var result=await SourceReplacer.RollbackAsync(task.Root,task,ct,async token=> { if(!task.SourceWriteGuardsSupported) return true;
                    try { await client.PostAsync<object>($"replacements/{task.ExecutionId:D}/authorize",new SourceWriteLocation(task.Root),token);return true;
                    } catch(HttpRequestException) { return false; } });
                report=result.Status=="restored"?new("rolled_back",null,result.ByteLength,result.ModifiedAt):new("failed",result.Detail);
            }
            else
            {
                report=SourceReplacer.Finalize(task.Root,task,out var detail)?new("finalized",null):new("failed",detail);
            }
        }
        catch(SourceWriteDeferredException) { return; }
        catch(HttpRequestException) { return; }
        catch(SafeMediaPaths.MediaLockBusyException) { return; }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
        catch(Exception exception) { logger.LogWarning("Remplacement {ExecutionId} : {Type}.",task.ExecutionId,exception.GetType().Name); report=new("failed",
            "Opération impossible ("+exception.GetType().Name+") ; la sauvegarde est conservée."); }
        await client.PostAsync<object>($"replacements/{task.ExecutionId:D}/report",report,ct);
    }
}
