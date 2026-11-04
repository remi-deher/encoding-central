using System.Text.Json;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Flows.Conditions;
using Encoding.WorkerAgent.Runtime.Media;

namespace Encoding.WorkerAgent.Runtime;

public sealed class RuntimeFlowRunner(WorkerAgentOptions options,MediaScanner scanner,MediaActions actions,ProcessToolRunner tools,PluginProcessHost plugins,
    PublicationGuardRunner guards,Encoding.WorkerAgent.Services.IToolCapabilityProbe capabilities,LocalPublicationArtifactStore localPublicationArtifacts,
    ILogger<RuntimeFlowRunner> logger, TimeProvider? time = null)
{
    public async Task RunAsync(RuntimeLease lease,RuntimeClient client,CancellationToken stoppingToken)
    {
        using var execution=new RuntimeFlowExecution(options,scanner,actions,tools,plugins,guards,capabilities,localPublicationArtifacts,logger,lease,client,stoppingToken,time);
        await execution.RunAsync();
    }
}
