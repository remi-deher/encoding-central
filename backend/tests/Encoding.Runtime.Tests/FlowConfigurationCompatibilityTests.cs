using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Nodes;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class FlowConfigurationCompatibilityTests
{
    private static Dictionary<string, JsonElement> Config(object value) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    [Fact]
    public void HistoricalDefaultsDoNotRequireNewRuntimeFeatures()
    {
        var (registry, _, _) = Harness.Services();
        var audio = new FlowComponentReference("base", "1.2.0", "base-audio-rules", "1.2.0", 1);
        Assert.NotNull(registry.Resolve(audio));
        Assert.Null(registry.MinimumRuntime(audio, Config(new { processingMode = "plan" })));
        var resize = registry.FindReference("video-resize")!;
        Assert.Null(registry.MinimumRuntime(resize, Config(new { resolution = "custom", onlyIfLarger = false })));
        Assert.Equal("1.2.0", registry.MinimumRuntime(resize, Config(new { onlyIfLarger = true }))!.Value.Version);
        var subtitles = registry.FindReference("subtitles-manage")!;
        Assert.Null(registry.MinimumRuntime(subtitles, Config(new { styleProfile = "none" })));
        Assert.Equal("1.2.0", registry.MinimumRuntime(subtitles, Config(new { styleProfile = "custom" }))!.Value.Version);
    }

    [Fact]
    public void NewTrackSelectionCannotRunOnAnOldWorkerThroughABaseAlias()
    {
        var (registry, validator, eligibility) = Harness.Services();
        var input = registry.FindReference("base-input-file")!;
        var audio = new FlowComponentReference("base", "1.0.0", "base-transcode-audio", "1.0.0", 1);
        var values = Config(new { processingMode = "plan", audioCodec = "aac", trackSelection = new[] { new { languages = "fra" } } });
        var flow = new FlowDefinition(1, Guid.NewGuid(), 1, "Compatibilité", "video",
            [new("input", null, input, new Dictionary<string, JsonElement>()), new("audio", null, audio, values)], [new("next", "input", "success", "audio")]);
        Assert.True(validator.Validate(flow).IsValid);
        NodeToolCapability[] capabilities = [new("plugin/base", "1.7.0", ["general"], []), new("plugin/audio", "1.0.0", ["general"], []),
            new("ffprobe", "8.0", ["video"], []), new("ffmpeg", "8.0", ["video"], ["cpu"], ["aac", "ac3"])];
        Assert.False(eligibility.CanRun(flow, capabilities, Guid.NewGuid()));
        capabilities[1] = capabilities[1] with { Version = "1.1.0" };
        Assert.True(eligibility.CanRun(flow, capabilities, Guid.NewGuid()));
        var requirement = registry.MinimumRuntime(audio, values)!.Value;
        Assert.Equal("audio", requirement.PluginId);
        Assert.Equal("1.1.0", requirement.Version);
    }

    [Fact]
    public void FocusedOperationsDeclareTheirWorkerFloor()
    {
        var (registry, _, _) = Harness.Services();
        foreach (var id in new[] { "subtitles-convert-ass", "subtitles-default-audio", "subtitles-snapshot-track-titles", "audio-convert-by-rules" })
        {
            var reference = registry.FindReference(id)!;
            Assert.NotNull(reference);
            Assert.Equal("1.1.0", registry.MinimumRuntime(reference, new Dictionary<string, JsonElement>())!.Value.Version);
        }
    }
}
