using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.NodeAgent.Runtime;
using Encoding.NodeAgent.Runtime.Media;
using Encoding.NodeAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Encoding.Runtime.Tests;

internal static partial class Harness
{
    internal sealed partial class Fixture
    {
        public async Task ExplicitQuickIntegrityPublicationAsync()
        {
            var tools = new ProcessToolRunner();
            var source = Path.Combine(directory,"source.mkv");
            await tools.CheckedAsync("ffmpeg",["-y","-nostdin","-v","error","-f","lavfi","-i","color=size=32x32:rate=10","-t","2","-c:v","mpeg4",source],directory,default);
            var bytes = await File.ReadAllBytesAsync(source);
            var flow = Chain(Node(registry,"entry","base-input-file"),
                Node(registry,"remux","video-remux-media",new(){["container"]=J("mp4"),["processingMode"]=J("direct")}),
                Node(registry,"check-1",MediaIntegrityPolicy.Component), Node(registry,"check-2",MediaIntegrityPolicy.Component),
                Node(registry,"end","base-complete-flow",new(){["result"]=J("success")}));
            flow = flow with { Connections = flow.Connections.Select(edge => edge.SourceNodeId.StartsWith("check-",StringComparison.Ordinal) ? edge with { SourceOutputId="valid" } : edge).ToArray() };
            var (id,lease,_) = await EnqueueAsync(flow,bytes);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            using var client = Client(a);
            var run = Runner("integrity-worker",out _).RunAsync(lease,client,deadline.Token);
            Runner("integrity-source",out var sourceOptions);
            Directory.CreateDirectory(sourceOptions.DataDirectory);
            await File.WriteAllTextAsync(Path.Combine(sourceOptions.DataDirectory,"node-identity.json"),
                JsonSerializer.Serialize(new WorkerIdentityState(a,"fixture","fixture",app.Urls.Single()+"/",Token,true),Json));
            using var sourceService = new RuntimeTransferService(sourceOptions,new WorkerIdentityStore(sourceOptions),
                new MediaScanner(tools),new PublicationGuardRunner(new NoGuardInvoker(),NullLogger<PublicationGuardRunner>.Instance),localPublicationArtifacts,NullLogger<RuntimeTransferService>.Instance);
            await sourceService.StartAsync(deadline.Token);
            try { await run; }
            finally { await sourceService.StopAsync(default); }
            var execution = (await executions.GetAsync(id,default))!;
            Check(execution.State=="succeeded", "Explicit integrity publication succeeds: " + execution.Detail);
            Check(execution.PublishedPath is not null,"Publication has durable confirmation.");
            var after = await File.ReadAllBytesAsync(source);
            Check(bytes.SequenceEqual(after),"The source is preserved byte for byte.");
            var pipeline = await executions.PipelineAsync(id,default);
            var checks = pipeline.Where(step=>step.NodeId.StartsWith("check-",StringComparison.Ordinal)).ToArray();
            Check(checks.Length==2 && checks.All(step=>step.OutputId=="valid"),"Both explicit checks succeed.");
            Check(checks[1].Commands.Count==0,"The second check reuses the exact artifact receipt.");
            Check(pipeline.SelectMany(step=>step.Commands).Count(command=>command.Command.Contains("ffmpeg",StringComparison.Ordinal))==2,
                "Only remux and one representative decode run; publication adds no hidden full decode.");
        }
    }
}
