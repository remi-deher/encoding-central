using System.Text.Json;
using Encoding.Contracts.Flows;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class FlowOperationTests
{
    private static Dictionary<string, JsonElement> Values(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    [Theory]
    [InlineData("subtitles-convert-ass")]
    [InlineData("subtitles-style-ass")]
    [InlineData("subtitles-remove-duplicates")]
    [InlineData("subtitles-detect-forced")]
    public void FocusedSubtitleOperationsCannotAcquireUnrelatedEffects(string id)
    {
        var options = SubtitleOptions.From(FlowOperationConfiguration.Resolve(id,
            Values(new { frenchDefault = true, setTitles = true, removeDuplicates = true, styleProfile = "crunchyroll" })));
        Assert.False(options.FrenchDefault);
        Assert.False(options.SetTitles);
        Assert.Equal(id == "subtitles-remove-duplicates", options.RemoveDuplicates);
        Assert.Equal(id == "subtitles-detect-forced", options.InferForced);
        Assert.Equal(id == "subtitles-convert-ass", options.ConvertSrt);
        Assert.Equal(id == "subtitles-style-ass", options.UpgradeAss);
    }

    [Fact]
    public void ConversionWithoutStyleStillProducesAss()
    {
        const string text = "[Script Info]\nTitle: Original\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Bonjour";
        var track = new SubtitleTrack(0, "subrip", "fra", "French", true, false, false, text);
        var options = SubtitleOptions.From(FlowOperationConfiguration.Resolve("subtitles-convert-ass", Values(new { styleProfile = "none" })));
        var plan = SubtitlePlanner.Plan([track], null, (1920, 1080), options);
        Assert.True(plan.Changed);
        Assert.Contains("Title: Original", Assert.Single(plan.Decisions).ReplacementAss);
    }

    [Fact]
    public void NamingAndDefaultSelectionAreIndependent()
    {
        var names = TrackNameOptions.From(FlowOperationConfiguration.Resolve("subtitles-name-tracks", Values(new { singleDefaultAudio = true })));
        Assert.True(names.RenameTracks);
        Assert.False(names.SingleDefaultAudio);
        var defaults = TrackNameOptions.From(FlowOperationConfiguration.Resolve("subtitles-default-audio", Values(new { renameTracks = true })));
        Assert.False(defaults.RenameTracks);
        Assert.True(defaults.SingleDefaultAudio);
    }

    [Fact]
    public void DuplicateRemovalPreservesLanguageNamesAndForcedFlags()
    {
        var track = new SubtitleTrack(0, "ass", "frf", "French forced", true, false, false,
            "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Bonjour");
        var options = SubtitleOptions.From(FlowOperationConfiguration.Resolve("subtitles-remove-duplicates", new Dictionary<string, JsonElement>()));
        var plan = SubtitlePlanner.Plan([track], null, (1920, 1080), options);
        var decision = Assert.Single(plan.Decisions);
        Assert.Equal(track.Language, decision.Language);
        Assert.Equal(track.Title, decision.Title);
        Assert.Equal(track.Forced, decision.Forced);
        Assert.Equal(track.Default, decision.Default);
        Assert.False(plan.Changed);
    }

    [Fact]
    public void HistoricalConfigurationIsReturnedUnchanged()
    {
        var values = Values(new { frenchDefault = true, mode = "snapshot" });
        Assert.Same(values, FlowOperationConfiguration.Resolve("base-subtitle-manage", values));
        Assert.Same(values, FlowOperationConfiguration.Resolve("subtitles-track-names", values));
        Assert.True(SubtitleOptions.From(new Dictionary<string, JsonElement>()).FrenchDefault);
    }
}
