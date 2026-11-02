using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Nodes;
using Encoding.NodeAgent.Runtime;
using Xunit;

namespace Encoding.Runtime.Tests;

[Collection(RuntimeCollection.Name)]
public sealed class FfmpegBuilderTests
{
    private static JsonElement J(object value)=>JsonSerializer.SerializeToElement(value);
    private static FlowNodeDefinition Node(FlowComponentRegistry registry,string id,string component,Dictionary<string,JsonElement>? configuration=null)
        =>new(id,null,registry.FindReference(component)!,configuration??new());
    private static FlowDefinition Chain(params FlowNodeDefinition[] nodes)=>new(1,Guid.NewGuid(),1,"Builder test","video",nodes,
        nodes.Zip(nodes.Skip(1),(a,b)=>new FlowConnectionDefinition(a.Id+"-"+b.Id,a.Id,"success",b.Id)).ToArray());

    [MediaToolsFact]
    public async Task TwoExecutorsReplaceCurrentMetadataAndShareDecodeChecks()
    {
        var(registry,validator,eligibility)=Harness.Services();
        await using var fixture=await Harness.Fixture.CreateAsync("sqlite",registry,validator,eligibility);
        await fixture.BuilderMetadataAndDecodeAsync(false);
    }

    [MediaToolsFact]
    public async Task FailedExecutorPreservesItsInputForAnExplicitFallbackBuilder()
    {
        var(registry,validator,eligibility)=Harness.Services();
        await using var fixture=await Harness.Fixture.CreateAsync("sqlite",registry,validator,eligibility);
        await fixture.BuilderMetadataAndDecodeAsync(true);
    }

    [MediaToolsFact]
    public async Task EmptyBuilderDoesNotMaterializeAnUnchangedSource()
    {
        var(registry,validator,eligibility)=Harness.Services();
        await using var fixture=await Harness.Fixture.CreateAsync("sqlite",registry,validator,eligibility);
        await fixture.EmptyBuilderInspectsWithoutPreparingArtifactsAsync();
    }

    [MediaToolsFact]
    public async Task OpenBuilderSurvivesAHandoffAndExecutesOnTheNextWorker()
    {
        var(registry,validator,eligibility)=Harness.Services();
        await using var fixture=await Harness.Fixture.CreateAsync("sqlite",registry,validator,eligibility);
        await fixture.MediaRelayAsync(true,strictBuilder:true);
    }

    [Theory]
    [InlineData("nested")]
    [InlineData("missing-start")]
    [InlineData("unclosed")]
    [InlineData("script")]
    public void InvalidScopesAreRejectedButCanBeSavedAsDrafts(string scenario)
    {
        var(registry,validator,_)=Harness.Services();
        var start=Node(registry,"start",FfmpegBuilderPolicy.Start);
        var execute=Node(registry,"execute",FfmpegBuilderPolicy.Execute);
        var entry=Node(registry,"entry","base-input-file");
        var end=Node(registry,"end","base-complete-flow");
        var flow=scenario switch {
            "nested"=>Chain(entry,start,start with { Id="nested" },execute,end),
            "missing-start"=>Chain(entry,execute,end),
            "unclosed"=>Chain(entry,start,end),
            _=>Chain(entry,start,Node(registry,"script","base-script",new(){["code"]=J("return {};"),["language"]=J("javascript")}),execute,end)
        };
        Assert.Contains(validator.Validate(flow).Issues,issue=>issue.Code.StartsWith("builder.scope"));
        Assert.DoesNotContain(validator.ValidateDraft(flow).Issues,issue=>issue.Code.StartsWith("builder.scope"));
    }

    [Fact]
    public void ExecutorRequiresAnOpenBuilderOnEveryIncomingBranch()
    {
        var(registry,validator,_)=Harness.Services();
        var entry=Node(registry,"entry","base-input-video");
        var decision=Node(registry,"decision","conditions-video-codec",new(){["codec"]=J("hevc")});
        var start=Node(registry,"start",FfmpegBuilderPolicy.Start);
        var execute=Node(registry,"execute",FfmpegBuilderPolicy.Execute);
        var end=Node(registry,"end","base-complete-flow");
        FlowConnectionDefinition Edge(string from,string output,string to)=>new(from+output,from,output,to);
        var flow=new FlowDefinition(1,Guid.NewGuid(),1,"Branches","video",[entry,decision,start,execute,end],
            [Edge("entry","success","decision"),Edge("decision","yes","start"),Edge("decision","no","execute"),
             Edge("start","success","execute"),Edge("execute","success","end")]);
        Assert.Contains(validator.Validate(flow).Issues,issue=>issue.Code=="builder.scope.invalid" && issue.Path=="/nodes/execute");
        var valid=flow with { Connections=flow.Connections.Select(edge=>edge.SourceNodeId=="decision" && edge.SourceOutputId=="no"?edge with { TargetNodeId="start" }:edge).ToArray() };
        Assert.True(validator.Validate(valid).IsValid);
    }

    [Fact]
    public void NewBuildersRequireAnUpgradedWorkerAndDoNotChangeHistoricalPlans()
    {
        var(registry,validator,eligibility)=Harness.Services();
        var flow=Chain(Node(registry,"entry","base-input-file"),Node(registry,"start",FfmpegBuilderPolicy.Start),
            Node(registry,"execute",FfmpegBuilderPolicy.Execute),Node(registry,"end","base-complete-flow"));
        Assert.True(validator.Validate(flow).IsValid);
        NodeToolCapability[] worker=[new("plugin/base","1.8.0",["video"],[]),new("ffmpeg","7.0",["video"],[]),new("ffprobe","7.0",["video"],[])];
        Assert.False(eligibility.CanRun(flow,worker,Guid.NewGuid()));
        Assert.True(eligibility.CanRun(flow,worker.Select(cap=>cap.ToolId=="plugin/base"?cap with { Version="1.9.0" }:cap).ToArray(),Guid.NewGuid()));
        var legacy=Chain(Node(registry,"entry","base-input-file"),Node(registry,"prepare","base-prepare-processing"),
            Node(registry,"execute","base-execute-processing"),Node(registry,"end","base-complete-flow"));
        Assert.True(validator.Validate(legacy).IsValid);
    }

    [Fact]
    public async Task VerifiedLocalCopyDoesNotAliasOrAcceptCorruptedSource()
    {
        var directory=Path.Combine(Path.GetTempPath(),"builder-copy-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var source=Path.Combine(directory,"source"); var target=Path.Combine(directory,"current");
            await File.WriteAllTextAsync(source,"source");
            var sha=await SafeMediaPaths.DigestAsync(source,default);
            await SafeMediaPaths.CopyVerifiedAsync(source,target,sha,default);
            await File.WriteAllTextAsync(target,"modified");
            Assert.Equal("source",await File.ReadAllTextAsync(source));
            await Assert.ThrowsAsync<IOException>(()=>SafeMediaPaths.CopyVerifiedAsync(source,target,new string('a',64),default));
            Assert.Equal("modified",await File.ReadAllTextAsync(target));
            Assert.False(File.Exists(target+".copy.partial"));
        }
        finally { Directory.Delete(directory,true); }
    }
}
