using Encoding.Api.Flows;
using Encoding.Contracts.Flows;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class GuidedFlowTests
{
    private static FlowComponentRegistry Registry() => StoreCatalogFixture.Create();

    [Theory]
    [InlineData("hevc", "automatic", "keep", "balanced")]
    [InlineData("h264", "cpu", "adapt", "high")]
    [InlineData("av1", "automatic", "adapt", "compact")]
    public void GeneratedGraphUsesInstalledVersionsAndPassesFullValidation(string codec, string encoder, string audio, string quality)
    {
        var registry = Registry();
        var flow = GuidedFlowBuilder.Build(new(Guid.NewGuid(), " Test ", codec, encoder, quality, audio, true), registry);
        var validation = new FlowDefinitionValidator(registry).Validate(flow);
        Assert.True(validation.IsValid, string.Join("\n", validation.Issues.Select(i => $"{i.Path}: {i.Message}")));
        Assert.Equal("Test", flow.Name);
        Assert.Single(flow.Nodes, n => n.Component.ComponentId == FfmpegBuilderPolicy.Execute);
        Assert.Single(flow.Nodes, n => n.Component.ComponentId == "base-publish-result");
        Assert.All(flow.Nodes, n => Assert.Equal(registry.Plugins.Single(p => p.Id == n.Component.PluginId).Version, n.Component.PluginVersion));
        Assert.DoesNotContain(flow.Connections, c => c.SourceOutputId == "error");
        var video = flow.Nodes.Single(n => n.Id == "video").Configuration;
        Assert.Equal(codec, video["videoCodec"].GetString());
        Assert.Equal(encoder == "automatic", video["retryWithCpu"].GetBoolean());
        Assert.Equal("plan", video["processingMode"].GetString());
        Assert.True(flow.Nodes.Single(n => n.Id == "prepare").Configuration["preserveUnchangedTracks"].GetBoolean());
        if (audio == "adapt") Assert.Contains(flow.Connections, c => c.SourceNodeId == "audio" && c.SourceOutputId == "unchanged" && c.TargetNodeId == "container");
        else Assert.DoesNotContain(flow.Nodes, n => n.Component.PluginId == "audio");
    }

    [Fact]
    public void PreviousVideoReferencesStillValidate()
    {
        var registry = Registry();
        var flow = GuidedFlowBuilder.Build(new(Guid.NewGuid(), "Historique"), registry);
        var historical = flow with { Nodes = flow.Nodes.Select(node => node.Component.PluginId == "video"
            ? node with { Component = node.Component with { PluginVersion = "1.2.0" } } : node).ToArray() };
        Assert.True(new FlowDefinitionValidator(registry).Validate(historical).IsValid);
    }

    [Theory]
    [InlineData("", "hevc", "automatic", "balanced", "keep")]
    [InlineData("Test", "bad", "automatic", "balanced", "keep")]
    [InlineData("Test", "hevc", "nvenc", "balanced", "keep")]
    [InlineData("Test", "hevc", "cpu", "bad", "keep")]
    [InlineData("Test", "hevc", "cpu", "balanced", "drop")]
    public void InvalidChoicesAreRejected(string name, string codec, string encoder, string quality, string audio) =>
        Assert.Throws<ArgumentException>(() => GuidedFlowBuilder.Build(new(Guid.NewGuid(), name, codec, encoder, quality, audio), Registry()));
}
