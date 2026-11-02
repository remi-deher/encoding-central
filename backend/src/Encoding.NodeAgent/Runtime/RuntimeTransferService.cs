using System.Collections.Concurrent;
using Encoding.Contracts.Executions;
using Encoding.NodeAgent.Runtime.Media;
using Encoding.NodeAgent.Services;

namespace Encoding.NodeAgent.Runtime;

public sealed class RuntimeTransferService(NodeAgentOptions options,WorkerIdentityStore identities,MediaScanner scanner,PublicationGuardRunner guards,
    ILocalPublicationArtifactStore localArtifacts,ILogger<RuntimeTransferService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid,Task> active=new();
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while(!ct.IsCancellationRequested)
            {
                foreach(var pair in active) if(pair.Value.IsCompleted) active.TryRemove(pair.Key,out _);
                try
                {
                    var identity=await identities.GetAsync(ct);
                    if(identity.IsEnrolled)
                    {
                        foreach(var serverUrl in identity.GetCandidateServerUrls(options))
                        {
                            try
                            {
                                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
                                using var client=new RuntimeClient(identity,serverUrl);
                                var transfers=await client.GetAsync<RuntimeTransfer[]>("transfers",timeout.Token);
                                foreach(var transfer in transfers)
                                {
                                    if(active.Count>=Math.Max(1,options.MaxConcurrentTasks)||active.ContainsKey(transfer.ExecutionId)) continue;
                                    var boundUrl=serverUrl;
                                    var task=Task.Run(async()=> { using var transferClient=new RuntimeClient(identity,boundUrl); await TransferAsync(transfer,
                                        transferClient,ct); },CancellationToken.None);
                                    active.TryAdd(transfer.ExecutionId,task);
                                }
                            }
                            catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
                            catch(Exception exception) { logger.LogDebug(exception, "Liste des transferts momentanément indisponible."); }
                        }
                    }
                }
                catch(OperationCanceledException) when(ct.IsCancellationRequested) { break; }
                catch(Exception exception) { logger.LogWarning("Canal de transfert momentanément indisponible : {Type}.",exception.GetType().Name); }
                await Task.Delay(TimeSpan.FromSeconds(2),ct);
            }
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
        finally { await Task.WhenAll(active.Values); }
    }
    private static async Task<bool> TryRenewSourceAsync(RuntimeClient client,RuntimeTransfer transfer,CancellationToken ct)
    {
        try { await client.GetAsync<RuntimeSourceState>($"executions/{transfer.ExecutionId:D}/source?epoch={transfer.Epoch}",ct); return true; }
        catch(HttpRequestException) { return false; }
    }
    private async Task TransferAsync(RuntimeTransfer transfer,RuntimeClient client,CancellationToken ct)
    {
        using var watch=CancellationTokenSource.CreateLinkedTokenSource(ct); ct=watch.Token;
        var guard=Task.Run(async()=>
        {
            var consecutiveFailures=0;
            try
            {
                while(!watch.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15),watch.Token);
                    try
                    {
                        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(watch.Token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
                        await client.GetAsync<RuntimeSourceState>($"executions/{transfer.ExecutionId:D}/source?epoch={transfer.Epoch}",timeout.Token);
                        if((await client.ExecutionAsync(transfer.ExecutionId,timeout.Token)).State!="running") { watch.Cancel(); break; }
                        consecutiveFailures=0;
                    }
                    catch(OperationCanceledException) when(watch.IsCancellationRequested) { break; }
                    catch(Exception)
                    {
                        consecutiveFailures++;
                        if(consecutiveFailures>=5) { watch.Cancel(); break; }
                    }
                }
            }
            catch(OperationCanceledException) when(watch.IsCancellationRequested) { }
        },CancellationToken.None);
        var directory=Path.Combine(options.DataDirectory,"transfers",transfer.ExecutionId.ToString("D")); Directory.CreateDirectory(directory);
        try
        {
            var original=SafeMediaPaths.Resolve(transfer.Root,transfer.MediaPath);
            if (transfer.CanWrite && (transfer.Publish || transfer.VerifyPublicationAccess))
            {
                var accessId=Guid.NewGuid(); var accessStarted=DateTimeOffset.UtcNow;
                await RecordOperationAsync(transfer,client,"Vérifier l’accès d’écriture à la bibliothèque",accessId,accessStarted,null,null,null,ct);
                await LibraryAccessVerifier.VerifyPublicationAsync(transfer.Root, ct, transfer.VerifyReplacementAccess ? transfer.MediaPath : null);
                await RecordOperationAsync(transfer,client,"Vérifier l’accès d’écriture à la bibliothèque",accessId,accessStarted,DateTimeOffset.UtcNow,0,null,ct);
            }
            using var mediaLock=transfer.CanWrite?SafeMediaPaths.Lock(original,Path.Combine(options.DataDirectory,"media-locks")):null;
            if(!transfer.Publish)
            {
                if(transfer.Observation is {} observed) new MediaFileStamp(observed.Bytes,observed.LastWriteUtcTicks).Verify(original);
                async Task UploadAsync(string relative,bool main)
                {
                    var path=SafeMediaPaths.Resolve(transfer.Root,relative);
                    if(main && transfer.Observation is {} expected) new MediaFileStamp(expected.Bytes,expected.LastWriteUtcTicks).Verify(path);
                    var stamp=MediaFileStamp.Read(path);
                    var sha=stamp.ToDigest();
                    await client.PutAsync(transfer.ExecutionId,transfer.Epoch,path,sha,main,ct,main?null:relative);
                }
                foreach(var input in transfer.InputPaths??[]) await UploadAsync(input,false);
                await UploadAsync(transfer.MediaPath,true);
            }
            else
            {
                if(transfer.ResultSha256 is null || transfer.ResultExtension is null || transfer.SourceSha256 is null) throw new IOException("Résultat de transfert incomplet.");
                if(transfer.ReplacementGraceHours is not null
                    && await SourceReplacer.ResumeAsync(transfer.Root,transfer.ExecutionId,transfer.MediaPath,transfer.SourceSha256,transfer.ResultSha256,transfer.ResultExtension,ct) is {} resumed)
                {
                    var current=await client.ExecutionAsync(transfer.ExecutionId,ct);
                    await client.PostAsync<object>($"executions/{transfer.ExecutionId:D}/publication",new ConfirmRuntimePublication(transfer.Epoch,
                        Guid.NewGuid(),current.Revision,$".encoding-results/{transfer.ExecutionId:D}/result{transfer.ResultExtension}",transfer.ResultSha256,
                        resumed),ct);
                    return;
                }
                var sourceCheckId=Guid.NewGuid(); var sourceCheckStarted=DateTimeOffset.UtcNow;
                await RecordOperationAsync(transfer,client,"Vérifier que la source n’a pas changé",sourceCheckId,sourceCheckStarted,null,null,null,ct);
                var originalStamp=MediaFileStamp.Read(original);
                if(transfer.Observation is {} obs && new MediaFileStamp(obs.Bytes,obs.LastWriteUtcTicks).Matches(original)) { }
                else if(originalStamp.Matches(original)) { }
                else throw new IOException("La source a changé depuis le début du traitement.");
                await RecordOperationAsync(transfer,client,"Vérifier que la source n’a pas changé",sourceCheckId,sourceCheckStarted,DateTimeOffset.UtcNow,0,
                    transfer.Observation?.Bytes??originalStamp.Bytes,ct);
                var incoming=Path.Combine(directory,"result"+transfer.ResultExtension);
                var incomingPath=incoming;
                var downloadId=Guid.NewGuid(); var downloadStarted=DateTimeOffset.UtcNow;
                await RecordOperationAsync(transfer,client,"Télécharger l’artefact depuis le serveur",downloadId,downloadStarted,null,null,null,ct);
                if(localArtifacts.TryGet(transfer.ExecutionId,transfer.Epoch,transfer.ResultSha256,out var localPath))
                {
                    incomingPath=localPath;
                    await RecordOperationAsync(transfer,client,"Réutiliser l’artefact local ; téléchargement réseau évité",downloadId,downloadStarted,DateTimeOffset.UtcNow,0,0,ct);
                }
                else
                {
                    var bytes=await client.DownloadAsync(transfer.ExecutionId,transfer.Epoch,transfer.ResultSha256,incoming,ct);
                    await RecordOperationAsync(transfer,client,"Télécharger l’artefact depuis le serveur",downloadId,downloadStarted,DateTimeOffset.UtcNow,0,bytes,ct);
                }
                var validationId=Guid.NewGuid(); var validationStarted=DateTimeOffset.UtcNow;
                await RecordOperationAsync(transfer,client,"Scanner et vérifier le résultat avant publication",validationId,validationStarted,null,null,null,ct);
                var before=await scanner.ScanAsync(original,ct);
                await scanner.ValidateAsync(incomingPath,transfer.MediaType,before,ct);
                var incomingBytes=new FileInfo(incomingPath).Length;
                await RecordOperationAsync(transfer,client,"Scanner et vérifier le résultat avant publication",validationId,validationStarted,DateTimeOffset.UtcNow,0,incomingBytes,ct);
                var relative=$".encoding-results/{transfer.ExecutionId:D}/result{transfer.ResultExtension}";
                var target=SafeMediaPaths.Resolve(transfer.Root,relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); target=SafeMediaPaths.Resolve(transfer.Root,relative);
                var temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
                Guid? publicationId=null; DateTimeOffset? publicationStarted=null;
                try
                {
                    if(!File.Exists(target))
                    {
                        var currentPublicationId=Guid.NewGuid(); var currentPublicationStarted=DateTimeOffset.UtcNow;
                        publicationId=currentPublicationId; publicationStarted=currentPublicationStarted;
                        await RecordOperationAsync(transfer,client,"Copier le résultat, calculer son SHA-256 et synchroniser le disque",currentPublicationId,
                            currentPublicationStarted,null,null,null,ct);
                        using var shaStream=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                        await using(var source=File.OpenRead(incomingPath))
                        await using(var destination=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,FileOptions.Asynchronous))
                        {
                            var buffer=new byte[81920]; int read;
                            while((read=await source.ReadAsync(buffer,ct))>0)
                            {
                                shaStream.AppendData(buffer,0,read);
                                await destination.WriteAsync(buffer.AsMemory(0,read),ct);
                            }
                            await destination.FlushAsync(ct); destination.Flush(true);
                        }
                        var writtenSha=Convert.ToHexStringLower(shaStream.GetHashAndReset());
                        if(writtenSha!=transfer.ResultSha256) throw new IOException("La sortie écrite ne correspond pas au résultat validé.");
                        var state=await client.ExecutionAsync(transfer.ExecutionId,ct);
                        if(state.State!="running") return;
                        await client.GetAsync<RuntimeSourceState>($"executions/{transfer.ExecutionId:D}/source?epoch={transfer.Epoch}",ct);
                        if(originalStamp is not null && !originalStamp.Matches(original))
                            throw new IOException("La source a changé avant publication.");
                        Encoding.Core.AtomicFilePublication.Publish(temporary,target);
                    }
                    else if(await SafeMediaPaths.DigestAsync(target,ct)!=transfer.ResultSha256) throw new IOException("La sortie existante est différente du résultat validé.");
                    Encoding.Core.AtomicFilePublication.Confirm(target);
                    if(publicationId is { } completedOperation && publicationStarted is { } operationStarted)
                        await RecordOperationAsync(transfer,client,"Copier le résultat, calculer son SHA-256 et synchroniser le disque",completedOperation,
                            operationStarted,DateTimeOffset.UtcNow,0,incomingBytes,ct);
                    SourceReplacementReport? replacement=null;
                    if(transfer.ReplacementGraceHours is not null)
                    {
                        var verdict=await guards.CheckAsync(transfer,ct);
                        replacement=!verdict.Allowed?new SourceReplacementReport("failed",null,null,0,DateTimeOffset.UtcNow,verdict.Detail)
                        :await SourceReplacer.ReplaceAsync(transfer.Root,transfer.ExecutionId,transfer.MediaPath,incomingPath,transfer.SourceSha256,transfer.ResultSha256,transfer.ResultExtension,
                            async token=>(await client.ExecutionAsync(transfer.ExecutionId,token)).State=="running" && await TryRenewSourceAsync(client,transfer,token),ct);
                    }
                    var status=await client.ExecutionAsync(transfer.ExecutionId,ct);
                    await client.PostAsync<object>($"executions/{transfer.ExecutionId:D}/publication",new ConfirmRuntimePublication(transfer.Epoch,
                        Guid.NewGuid(),status.Revision,relative,transfer.ResultSha256,replacement),ct);
                    localArtifacts.Remove(transfer.ExecutionId,transfer.Epoch);
                }
                finally { if(File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        catch(SafeMediaPaths.MediaLockBusyException) { }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
        catch(Exception exception)
        {
            logger.LogWarning("Transfert {ExecutionId} refusé : {Type}.",transfer.ExecutionId,exception.GetType().Name);
            try
            {
                var status=await client.ExecutionAsync(transfer.ExecutionId,ct);
                await client.PostAsync<object>($"executions/{transfer.ExecutionId:D}/source-failure",new FinishRuntimeExecution(transfer.Epoch,Guid.NewGuid(),
                    status.Revision,"failed",
                    exception is LibraryAccessException ? "permission_denied : " + exception.Message : transfer.Publish ? "publication_failed : Échec de publication du résultat ; confirmation de publication non obtenue." : "source_transfer_failed : Échec de préparation ou de transfert de la source ; traitement interrompu.",
                    null,null),ct);
            }
            catch(Exception reportException) { logger.LogDebug(reportException, "Signal d’échec source indisponible pour {ExecutionId}.", transfer.ExecutionId); }
        }
        finally { watch.Cancel(); await guard; }
    }

    private async Task RecordOperationAsync(RuntimeTransfer transfer,RuntimeClient client,string operation,Guid id,DateTimeOffset startedAt,
        DateTimeOffset? finishedAt,int? exitCode,long? bytesProcessed,CancellationToken ct)
    {
        if(transfer.PublicationStepNodeId is not { } nodeId || transfer.PublicationStepInvocationId is not { } invocationId) return;
        if(RuntimeCommandCapture.RecordForExecution(transfer.ExecutionId,nodeId,invocationId,operation,id,startedAt,finishedAt,exitCode,bytesProcessed)) return;
        try
        {
            await client.ReportSourceOperationAsync(transfer.ExecutionId,transfer.Epoch,nodeId,invocationId,
                RuntimeCommandCapture.CreateOperationLog(operation,id,startedAt,finishedAt,exitCode,bytesProcessed),ct);
        }
        catch(HttpRequestException exception)
        {
            logger.LogDebug("Journal de publication indisponible pour {ExecutionId} ({Type}).",transfer.ExecutionId,exception.GetType().Name);
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested)
        {
            logger.LogDebug("Journal de publication expiré pour {ExecutionId}.",transfer.ExecutionId);
        }
    }
}
