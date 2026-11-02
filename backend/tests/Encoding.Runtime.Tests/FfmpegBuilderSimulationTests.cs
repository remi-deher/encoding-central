using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Flows.Conditions;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class FfmpegBuilderSimulationTests
{
    private static JsonElement J(object value)=>JsonSerializer.SerializeToElement(value,new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static JsonElement Metadata(string codec)=>J(new { format=new { duration="1",size="100" },streams=new[] { new { codec_type="video",codec_name=codec,width=128,height=72 } } });
    [Theory]
    [InlineData("video-remux-media")]
    [InlineData("audio-rules")]
    public void SuccessfulExecutionRequiresResultMetadataAndUsesItForTheNextDecision(string setting)
    {
        var(registry,validator,_)=Harness.Services();
        FlowNodeDefinition Node(string id,string component,Dictionary<string,JsonElement>? configuration=null)=>new(id,null,registry.FindReference(component)!,configuration??new());
        var nodes=new[] { Node("entry","base-input-video"),Node("start",FfmpegBuilderPolicy.Start),
            Node("settings",setting,setting=="video-remux-media"?new(){["container"]=J("mkv")}:new()),Node("execute",FfmpegBuilderPolicy.Execute),
            Node("decision","conditions-video-codec",new(){["codec"]=J("hevc")}),Node("end","base-complete-flow") };
        var edges=nodes.Zip(nodes.Skip(1),(a,b)=>new FlowConnectionDefinition(a.Id+"-"+b.Id,a.Id,a.Id=="decision"?"yes":"success",b.Id)).ToArray();
        var flow=new FlowDefinition(1,Guid.NewGuid(),1,"Simulation","video",nodes,edges);
        var file=new FlowSimulationFile("source.mkv",Metadata("h264"),new Dictionary<string,JsonElement>(),Guid.NewGuid(),Guid.NewGuid());
        var simulator=new FlowSimulator(registry,validator);
        var missing=simulator.Simulate(new(J(flow),file,new Dictionary<string,FlowSimulationAssumption>{["execute"]=new("success")}));
        Assert.Equal("waiting",missing.Status);Assert.Equal("execute",missing.WaitingNodeId);
        var completed=simulator.Simulate(new(J(flow),file,new Dictionary<string,FlowSimulationAssumption>{["execute"]=new("success",Metadata("hevc"),Modified:true)}));
        Assert.Equal("completed",completed.Status);
        Assert.Equal("yes",completed.Steps.Single(step=>step.NodeId=="decision").OutputId);
        var context=completed.Steps.Last().Context;
        Assert.Equal("h264",context.BeforeMetadata!.Value.GetProperty("streams")[0].GetProperty("codec_name").GetString());
        Assert.Equal("hevc",context.CurrentMetadata!.Value.GetProperty("streams")[0].GetProperty("codec_name").GetString());
    }
}
