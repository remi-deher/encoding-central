using System.Text.Json;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Flows.Conditions;
using Encoding.NodeAgent.Runtime.Media;

namespace Encoding.NodeAgent.Runtime;

public sealed class RuntimeFlowRunner(NodeAgentOptions options,MediaScanner scanner,MediaActions actions,ProcessToolRunner tools,PluginProcessHost plugins,
    PublicationGuardRunner guards,Encoding.NodeAgent.Services.IToolCapabilityProbe capabilities,LocalPublicationArtifactStore localPublicationArtifacts,
    ILogger<RuntimeFlowRunner> logger)
{
    public async Task RunAsync(RuntimeLease lease,RuntimeClient client,CancellationToken stoppingToken)
    {
        using var execution=new RuntimeFlowExecution(options,scanner,actions,tools,plugins,guards,capabilities,localPublicationArtifacts,logger,lease,client,stoppingToken);
        await execution.RunAsync();
    }
}
