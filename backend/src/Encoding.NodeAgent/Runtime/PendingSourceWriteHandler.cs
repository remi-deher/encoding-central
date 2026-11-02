using Encoding.Contracts.Executions;

namespace Encoding.NodeAgent.Runtime;

internal static class PendingSourceWriteHandler
{
    public static async Task<bool> HandleAsync(PendingSourceWrite task,RuntimeClient client,PublicationGuardRunner guards,CancellationToken ct)
    {
        using var watch=CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task<bool> Authorized(CancellationToken token)
        {
            try
            {
                await client.PostAsync<object>($"source-writes/{task.ExecutionId:D}/authorize",new PendingSourceWriteFence(task.ClaimId,task.Root),token);
                return true;
            }
            catch(HttpRequestException) { return false; }
        }
        var renewal=Task.Run(async()=>
        {
            try
            {
                while(!watch.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15),watch.Token);
                    if(!await Authorized(watch.Token)) { watch.Cancel();return; }
                }
            }
            catch(OperationCanceledException) when(watch.IsCancellationRequested) { }
            catch(Exception) { watch.Cancel(); }
        },CancellationToken.None);
        SourceReplacementReport? report=null;
        try
        {
            if(await Authorized(watch.Token))
            {
                var transfer=new RuntimeTransfer(task.ExecutionId,0,task.Root,task.OriginalPath,task.SourceSha256,task.ResultSha256,task.ResultExtension,true,
                    LibraryId:task.LibraryId,Guards:task.Guards);
                var verdict=await guards.CheckAsync(transfer,watch.Token);
                if(!verdict.Allowed) report=new("failed",null,null,0,DateTimeOffset.UtcNow,verdict.Detail);
                else report=await SourceReplacer.ReplaceAsync(task.Root,task.ExecutionId,task.OriginalPath,
                    SafeMediaPaths.Resolve(task.Root,task.PublishedPath),task.SourceSha256,task.ResultSha256,task.ResultExtension,
                        async token=>await Authorized(token)?true:throw new SourceWriteDeferredException(),watch.Token);
            }
        }
        catch(SourceWriteDeferredException) { }
        catch(SafeMediaPaths.MediaLockBusyException) { }
        catch(Exception exception) when(exception is IOException or InvalidOperationException)
        { report=new("failed",null,null,0,DateTimeOffset.UtcNow,"Écriture interrompue ("+exception.GetType().Name+") ; vérifier la source et les sauvegardes avant relance."); }
        catch(OperationCanceledException) when(watch.IsCancellationRequested && !ct.IsCancellationRequested) { }
        finally { watch.Cancel();await renewal; }
        ct.ThrowIfCancellationRequested();
        await client.PostAsync<object>($"source-writes/{task.ExecutionId:D}/complete",new PendingSourceWriteCompletion(task.ClaimId,report),ct);
        return report is not null;
    }
}
