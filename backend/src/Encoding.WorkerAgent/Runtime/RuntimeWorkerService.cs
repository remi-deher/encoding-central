using Encoding.Contracts.Executions;
using Encoding.Contracts.Workers;
using Encoding.WorkerAgent.Services;

namespace Encoding.WorkerAgent.Runtime;

public sealed class RuntimeWorkerService(WorkerAgentOptions options,WorkerIdentityStore identities,RuntimeFlowRunner runner,WorkerWakeSignal wake,ILogger<RuntimeWorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var tasks=new List<Task>();
        var waiter=wake.CreateWaiter();
        try
        {
            await WorkerPolling.DelayAsync(0,ct);
            while(!ct.IsCancellationRequested)
            {
                tasks.RemoveAll(task=>task.IsCompleted);
                var claimed=false;
                try
                {
                    var identity=await identities.GetAsync(ct);
                    if(identity.IsEnrolled && options.Roles.Contains(WorkerRole.Worker))
                    {
                        foreach(var serverUrl in identity.GetCandidateServerUrls(options))
                        {
                            try
                            {
                                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
                                using var client=new RuntimeClient(identity,serverUrl);
                                var capacity=await client.GetAsync<RuntimeCapacity>("capacity",timeout.Token);
                                if(tasks.Count>=capacity.MaxConcurrentTasks) break;
                                var lease=await client.PostAsync<RuntimeLease>("claim",new { },timeout.Token);
                                if(lease is not null)
                                {
                                    var boundUrl=serverUrl;
                                    tasks.Add(Task.Run(async()=>
                                    {
                                        using var executionClient=new RuntimeClient(identity,boundUrl);
                                        try { await runner.RunAsync(lease,executionClient,ct); }
                                        finally { wake.Pulse(); }
                                    },CancellationToken.None));
                                    claimed=true;
                                    break;
                                }
                            }
                            catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
                            catch(Exception exception) { logger.LogDebug(exception, "Claim worker momentanément refusé."); }
                        }
                    }
                }
                catch(OperationCanceledException) when(ct.IsCancellationRequested) { break; }
                catch(Exception exception) { logger.LogWarning("Attribution worker momentanément indisponible : {Type}.",exception.GetType().Name); }
                // Un claim réussi peut en annoncer un autre (capacité restante) : on repasse tout de suite, sinon on attend un signal.
                if(!claimed) await waiter.WaitAsync(2000,ct);
            }
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
        finally { await Task.WhenAll(tasks); }
    }
}
