using System.Text.Json;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Flows.Conditions;
using Encoding.NodeAgent.Runtime;

namespace Encoding.Runtime.Tests;

internal static partial class Harness
{
    internal sealed partial class Fixture
    {
        private int artifactDownloads;
        public async Task BuilderMetadataAndDecodeAsync(bool failFirst)
        {
            var tools=new ProcessToolRunner();
            var source=Path.Combine(directory,"builder-source.mkv");
            await tools.CheckedAsync("ffmpeg",["-v","error","-y","-f","lavfi","-i","testsrc=size=128x72:duration=0.4:rate=5","-c:v",failFirst?"ffv1":"mpeg4",source],directory,CancellationToken.None);
            FlowNodeDefinition Condition(string id,int width) => Node(registry,id,"conditions-match",new(){["rules"]=ConditionTree.Serialize(new ConditionGroup("all",[
                new ConditionRule("video.width","eq",J(width)),new ConditionRule("video.width","eq",J(128),Scope:"source")]))});
            var first=Node(registry,"setting1",failFirst?"video-transcode":"video-resize",failFirst
                ?new(){["videoCodec"]=J("hevc"),["processingMode"]=J("direct")}
                :new(){["width"]=J(64),["height"]=J(36),["processingMode"]=J("direct")});
            var second=Node(registry,"setting2",failFirst?"video-remux-media":"video-resize",failFirst
                ?new(){["container"]=J("mkv")}
                :new(){["width"]=J(32),["height"]=J(18)});
            var flow=Chain(Node(registry,"entry","base-input-video"),Node(registry,"start1",FfmpegBuilderPolicy.Start),first,
                Node(registry,"execute1",FfmpegBuilderPolicy.Execute),Condition("condition1",failFirst?128:64),
                Node(registry,"validate1","probe-validate-output",new(){["verifyDecoding"]=J(true)}),
                Node(registry,"validateAgain","probe-validate-output",new(){["verifyDecoding"]=J(true)}),
                Node(registry,"start2",FfmpegBuilderPolicy.Start),second,Node(registry,"execute2",FfmpegBuilderPolicy.Execute),
                Condition("condition2",failFirst?128:32),Node(registry,"validate2","probe-validate-output",new(){["verifyDecoding"]=J(true)}),
                Node(registry,"end","base-complete-flow",new(){["result"]=J("unchanged")}));
            flow=flow with { Connections=flow.Connections.Select(edge=>edge.SourceNodeId switch {
                "execute1" when failFirst => edge with { SourceOutputId="error" },
                "condition1" or "condition2" => edge with { SourceOutputId="yes" },
                "validate1" or "validateAgain" or "validate2" => edge with { SourceOutputId="valid" },
                _=>edge }).ToArray() };
            var bytes=await File.ReadAllBytesAsync(source);
            var(id,lease,_)=await EnqueueAsync(flow,bytes);
            using var client=Client(a);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await Runner("builder",out _,failFirst?new FixedProbe():null).RunAsync(lease,client,deadline.Token);
            var result=(await executions.GetAsync(id,CancellationToken.None))!;
            Check(result.State=="succeeded",$"Builder sequence finishes: {result.Detail}");
            Check(artifactDownloads==1,"The source/current identity needs only one verified download, not two HTTP transfers.");
            var steps=await executions.PipelineAsync(id,CancellationToken.None);
            Check(steps.Single(step=>step.NodeId=="condition1").OutputId=="yes","The first decision sees a validated output (or preserved input on error).");
            Check(steps.Single(step=>step.NodeId=="condition2").OutputId=="yes","The second conversion reads updated current metadata while source metadata is immutable.");
            Check(!steps.Single(step=>step.NodeId=="setting1").Commands.Any(command=>command.Command.Contains("ffmpeg")),"A direct-configured setting inside the builder only prepares the plan.");
            Check(steps.Single(step=>step.NodeId=="execute1").OutputId==(failFirst?"error":"success"),"An executor error is an explicit, routable result.");
            var commands=steps.SelectMany(step=>step.Commands).ToArray();
            Check(commands.Count(command=>command.Command.Contains("ffprobe"))==(failFirst?2:3),"One source scan and one per successful output; command composition and metadata readers reuse the cache.");
            Check(!steps.Single(step=>step.NodeId=="validateAgain").Commands.Any(command=>command.Command.Contains("-f null")),
                "Repeated validation of the same result does not decode it again.");
            await using var read=db.CreateCommand("SELECT checkpoint::text FROM execution_steps WHERE execution_id=@id AND node_id='end'");
            read.Parameters.AddWithValue("id",id);
            var checkpoint=JsonSerializer.Deserialize<RuntimeCheckpoint>((string)(await read.ExecuteScalarAsync())!,Json)!;
            Check(checkpoint.Context.Plan.Count==0 && !FfmpegBuilderPolicy.Active(checkpoint.Context.Variables),"Builders close after success/error without leaking a previous plan into the next conversion.");
            var after=await File.ReadAllBytesAsync(source);
            Check(bytes.SequenceEqual(after),"The original library source stays intact.");
        }
        public async Task EmptyBuilderInspectsWithoutPreparingArtifactsAsync()
        {
            var source=Path.Combine(directory,"empty-builder.mkv"); var tools=new ProcessToolRunner();
            await tools.CheckedAsync("ffmpeg",["-v","error","-y","-f","lavfi","-i","color=size=32x32:duration=0.2","-c:v","mpeg4",source],directory,default);
            var flow=Chain(Node(registry,"entry","base-input-video"),Node(registry,"start",FfmpegBuilderPolicy.Start),
                Node(registry,"execute",FfmpegBuilderPolicy.Execute),Node(registry,"end","base-complete-flow"));
            flow=flow with { Connections=flow.Connections.Select(edge=>edge.SourceNodeId=="execute"?edge with { SourceOutputId="skipped" }:edge).ToArray() };
            var(id,lease,_)=await EnqueueAsync(flow,await File.ReadAllBytesAsync(source),directRead:true);
            using var client=Client(a); using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await Runner("empty-builder",out var options).RunAsync(lease,client,timeout.Token);
            Check((await executions.GetAsync(id,default))?.State=="succeeded","A no-op builder succeeds on the direct source.");
            Check((await runtime.SourceAsync(id,default)).SourceSha256 is null && artifactDownloads==0,"No-op builders do not copy, hash or transfer the media.");
            Check(!Directory.EnumerateFiles(options.DataDirectory,"*.mkv",SearchOption.AllDirectories).Any(),"No media file is materialized in the worker workspace.");
        }
    }
}
