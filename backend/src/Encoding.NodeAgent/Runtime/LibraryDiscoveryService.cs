
using Encoding.Contracts.Libraries;
using Encoding.NodeAgent.Services;
namespace Encoding.NodeAgent.Runtime;

public sealed class LibraryDiscoveryService(NodeAgentOptions options,WorkerIdentityStore identities,ILogger<LibraryDiscoveryService> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await WorkerPolling.DelayAsync(0,ct); } catch(OperationCanceledException) when(ct.IsCancellationRequested) { return; }
        while(!ct.IsCancellationRequested)
        {
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
                            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
                            var lease=await client.PostAsync<LibraryScanLease>("library-scans/claim",new{},timeout.Token);
                            if(lease is not null) { await ScanAsync(lease,client,ct); break; }
                        }
                        catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
                        catch(Exception exception) { logger.LogDebug(exception, "Claim de scan bibliothèque momentanément indisponible."); }
                    }
                }
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) {break;}
            catch(Exception e) {logger.LogWarning("Découverte momentanément indisponible : {Type}.",e.GetType().Name);}
            try {await WorkerPolling.DelayAsync(3000,ct);} catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
        }
    }
    private static async Task ScanAsync(LibraryScanLease lease,RuntimeClient client,CancellationToken ct)
    {
        using var job=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renew=Task.Run(async()=>
        {
            try
            {
                while(true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15),job.Token);
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(job.Token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    await client.PostAsync<object>($"library-scans/{lease.Id}/renew",new LibraryScanFence(lease.Epoch),timeout.Token);
                }
            }
            catch {job.Cancel();}
        },CancellationToken.None);
        try
        {
            List<DiscoveredLibraryFile> files=[]; string? error=null;
            try
            {
                await Task.Run(()=>
                {
                    var pending=new Stack<string>(); pending.Push(Path.GetFullPath(lease.Root));
                    while(pending.TryPop(out var directory))
                    {
                        job.Token.ThrowIfCancellationRequested();
                        foreach(var entry in Directory.EnumerateFileSystemEntries(directory))
                        {
                            job.Token.ThrowIfCancellationRequested();
                            var attributes=File.GetAttributes(entry);
                            if((attributes&FileAttributes.ReparsePoint)!=0) continue;
                            if((attributes&FileAttributes.Directory)!=0)
                            {
                                if(!Path.GetFileName(entry).StartsWith(".encoding",StringComparison.OrdinalIgnoreCase)) pending.Push(entry);
                                continue;
                            }
                            if(!LibraryMediaTypes.Matches(entry,lease.MediaType)) continue;
                            var relative=MediaPathRules.Normalize(Path.GetRelativePath(lease.Root,entry));
                            var info=new FileInfo(SafeMediaPaths.Resolve(lease.Root,relative));
                            files.Add(new(relative,info.Length,new DateTimeOffset(info.LastWriteTimeUtc)));
                            if(files.Count>10000) throw new InvalidDataException("La découverte dépasse la limite de 10 000 fichiers. Réduisez le périmètre de cette bibliothèque.");
                        }
                    }
                },job.Token);
            }
            catch(OperationCanceledException) {throw;}
            catch(InvalidDataException e) {files=[];error=e.Message;}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException) {files=[];
                error="Découverte impossible : vérifiez le montage et les droits du nœud. L’inventaire précédent est conservé.";}
            using var completeTimeout=CancellationTokenSource.CreateLinkedTokenSource(job.Token); completeTimeout.CancelAfter(TimeSpan.FromSeconds(45));
            await client.PostAsync<object>($"library-scans/{lease.Id}/complete",new CompleteLibraryScan(lease.Epoch,files,error),completeTimeout.Token);
        }
        finally {job.Cancel();await renew;}
    }
}
