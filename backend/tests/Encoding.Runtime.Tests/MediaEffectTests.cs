using System.Text.Json;
using Encoding.Contracts.Plugins;
using Encoding.Contracts.Executions;
using Encoding.NodeAgent.Runtime;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class MediaEffectTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"encoding-effects-"+Guid.NewGuid().ToString("N"));
    public MediaEffectTests()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory,"current.mkv"),"original");
    }

    [Fact]
    public void ReadOnlyRejectsACandidateAndChangedOrDeletedInput()
    {
        var guard=new MediaEffectGuard(MediaEffects.ReadOnly,directory,"current.mkv");
        Assert.Null(guard.Validate(null));
        Assert.Throws<IOException>(()=>guard.Validate("new.mkv"));
        File.AppendAllText(Path.Combine(directory,"current.mkv"),"changed");
        Assert.Throws<IOException>(()=>guard.Validate(null));
        File.Delete(Path.Combine(directory,"current.mkv"));
        Assert.Throws<IOException>(()=>guard.Validate(null));
    }

    [Fact]
    public void NewOutputMustNotBeTheInputAndMustPreserveIt()
    {
        var guard=new MediaEffectGuard(MediaEffects.ProducesOutput,directory,"current.mkv");
        Assert.Equal("new.mkv",guard.Validate("new.mkv"));
        Assert.Throws<IOException>(()=>guard.Validate("current.mkv"));
        File.AppendAllText(Path.Combine(directory,"current.mkv"),"changed");
        Assert.Throws<IOException>(()=>guard.Validate("new.mkv"));
    }

    [Fact]
    public void ExplicitMutationIsReportedEvenWithPreservedSizeAndModificationTime()
    {
        var file=Path.Combine(directory,"current.mkv");
        var before=MediaFileStamp.Read(file);
        var guard=new MediaEffectGuard(MediaEffects.ModifiesCurrent,directory,"current.mkv");
        File.WriteAllText(file,"modified");
        File.SetLastWriteTimeUtc(file,new DateTime(before.LastWriteUtcTicks,DateTimeKind.Utc));
        Assert.Equal(before,MediaFileStamp.Read(file));
        Assert.Equal("current.mkv",guard.Validate(null));
    }

    [Fact]
    public void LegacyEffectsRemainOptionalAndExplicitEffectsChangeTheHandshake()
    {
        var component=new FlowComponentDescriptor("inspect","1.0.0","Inspect","Inspect media","action","video",["video"],
            [new("success","Success","Done")],new(1,[]),[],true,false);
        var old=new PluginManifest(1,"test.plugin","1.0.0","Test",[1],[component]);
        var json=JsonSerializer.Serialize(old,new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("mediaEffect",json);
        Assert.Equal(PluginManifestFingerprint.Compute(old),PluginManifestFingerprint.Compute(JsonSerializer.Deserialize<PluginManifest>(json,new JsonSerializerOptions(JsonSerializerDefaults.Web))!));
        var declared=old with { Components=[component with { MediaEffect=MediaEffects.ReadOnly }] };
        Assert.NotEqual(PluginManifestFingerprint.Compute(old),PluginManifestFingerprint.Compute(declared));
        Assert.Throws<IOException>(()=>new MediaEffectGuard("invalid",directory,"current.mkv"));
        Assert.True(DirectMediaInspection.AllowsMetadataOnly(component with { MediaEffect=MediaEffects.ReadOnly,RequiresMedia=false }));
        Assert.False(DirectMediaInspection.AllowsMetadataOnly(component with { MediaEffect=MediaEffects.ReadOnly }));
        Assert.False(DirectMediaInspection.AllowsMetadataOnly(component with { RequiresMedia=false }));
        Assert.False(DirectMediaInspection.Allows("base","base-script"));
    }

    [Fact]
    public void ServerRejectsAnUnknownEffectInAnInstalledManifest()
    {
        var (registry,_,_)=Harness.Services();
        var example=registry.Plugins.First().Components.First() with { Id="test-inspect",MediaEffect="disableIntegrity" };
        var manifest=new PluginManifest(1,"test.effects","1.0.0","Test",[1],[example]);
        Assert.False(registry.TryInstall(manifest,out var reason));
        Assert.Contains("mediaEffect",reason);
    }

    public void Dispose()=>Directory.Delete(directory,true);
}
