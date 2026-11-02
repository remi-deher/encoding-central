using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Executions;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class MediaProcessingOptionTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static Dictionary<string, JsonElement> Config(object value) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;
    private static readonly JsonElement Metadata = Json(new { streams = new object[] {
        new { codec_type = "audio", codec_name = "dts", channels = 8, channel_layout = "7.1", tags = new { language = "fre", title = "Film" } },
        new { codec_type = "audio", codec_name = "aac", channels = 2, channel_layout = "stereo", tags = new { language = "jpn", title = "Original" } },
        new { codec_type = "audio", codec_name = "aac", channels = 2, channel_layout = "stereo", tags = new { language = "eng", title = "Commentary" } }
    } });

    [Fact]
    public void ExplicitSampleRateIsPartOfConformity()
    {
        var source = Json(new { streams = new[] { new { codec_type = "audio", codec_name = "aac", channels = 2, channel_layout = "stereo", sample_rate = "44100" } } });
        Assert.Single(AudioRulePlanner.Plan(source, AudioRuleOptions.From(Config(new { sampleRate = 48000 }))).Conversions);
        Assert.Empty(AudioRulePlanner.Plan(source, AudioRuleOptions.From(Config(new { sampleRate = 0 }))).Conversions);
    }

    [Fact]
    public void UnselectedUnknownChannelsAreRetainedAndFlacConformityIgnoresLossyBitrate()
    {
        var unknown = Json(new { streams = new[] { new { codec_type = "audio", codec_name = "unknown" } } });
        Assert.Empty(AudioRulePlanner.Plan(unknown, AudioRuleOptions.From(Config(new { rules = new[] { new { action = "copy" } } }))).Conversions);
        Assert.Empty(AudioRulePlanner.Plan(unknown, AudioRuleOptions.From(Config(new { rules = new[] { new { action = "convert", codec = "aac", languages = "fra" } } }))).Conversions);
        var flac = Json(new { streams = new[] { new { codec_type = "audio", codec_name = "flac", channels = 2, channel_layout = "stereo", sample_rate = "48000" } } });
        Assert.Empty(AudioRulePlanner.Plan(flac, AudioRuleOptions.From(Config(new { rules = new[] { new { action = "convert", codec = "flac", bitrateKbps = 256 } } }))).Conversions);
    }

    [Fact]
    public void RulesUseFirstMatchAndRetainUnmatchedTracksWithLfe()
    {
        var options = AudioRuleOptions.From(Config(new { rules = new object[] {
            new { languages = "fra", minChannels = 3, action = "convert", codec = "ac3", targetChannels = 6, bitrateKbps = 640 },
            new { languages = "fre", action = "copy" }
        } }));
        var plan = AudioRulePlanner.Plan(Metadata, options);
        var conversion = Assert.Single(plan.Conversions);
        Assert.Equal(0, conversion.AudioIndex);
        Assert.Equal("5.1", conversion.OutputLayout);
        Assert.Equal(640, conversion.BitrateKbps);
        Assert.Equal(2, plan.Decisions.Count(item => !item.Convert));
        Assert.Single(plan.Steps("audio", options));
    }

    [Fact]
    public void AudioSelectionUsesTypeLocalIndicesAndPreservesHistoricalIndexedSteps()
    {
        RuntimePlanStep selected = new("audio-transcode", Config(new { trackSelection = new[] { new { languages = "eng,jpn", excludeCommentary = "yes" } }, audioCodec = "ac3" }));
        RuntimePlanStep indexed = new("audio-normalize", Config(new { trackId = 2 }));
        var plan = MediaTrackSelection.Expand([selected, indexed], Metadata);
        Assert.Equal(2, plan.Count);
        Assert.Equal(1, plan[0].Configuration["trackId"].GetInt32());
        Assert.Same(indexed, plan[1]);
        var noMatch = selected with { Configuration = Config(new { trackSelection = new[] { new { languages = "ita" } } }) };
        Assert.Empty(MediaTrackSelection.Expand([noMatch], Metadata));
    }

    [Theory]
    [InlineData(1000, 6)]
    [InlineData(640, 4)]
    public void InvalidAudioRulesAreRejected(int bitrate, int channels) =>
        Assert.Throws<AudioRuleException>(() => AudioRuleOptions.From(Config(new { rules = new[] { new { action = "convert", codec = "ac3", bitrateKbps = bitrate, targetChannels = channels } } })));

    [Fact]
    public void ResizePreservesLegacyBehaviorAndCanPreventUpscaling()
    {
        Assert.Equal("scale=1920:1080:force_original_aspect_ratio=decrease", VideoResizePolicy.Filter(Config(new { })));
        var options = Config(new { resolution = "1080p", onlyIfLarger = true });
        Assert.False(VideoResizePolicy.ShouldResize(options, 1280, 720));
        Assert.True(VideoResizePolicy.ShouldResize(options, 3840, 2160));
        Assert.Contains("min(iw,1920)", VideoResizePolicy.Filter(options));
    }

    [Fact]
    public void NormalizationProfilesAndMeasuredValuesAreExplicit()
    {
        var filter = AudioNormalizationPolicy.Filter(Config(new { normalizationProfile = "night", targetLoudness = -30 }));
        Assert.Contains("I=-18", filter);
        Assert.Contains("LRA=4", filter);
        var measured = AudioNormalizationPolicy.MeasuredFilter(filter, "log\n{\"input_i\":\"-20\",\"input_tp\":\"-2\",\"input_lra\":\"3\",\"input_thresh\":\"-30\",\"target_offset\":\"0.5\"}");
        Assert.Contains("measured_I=-20", measured);
        Assert.Contains("linear=true", measured);
        Assert.Throws<IOException>(() => AudioNormalizationPolicy.MeasuredFilter(filter, "{}".Replace("{}", "{\"input_i\":\"-inf\"}")));
    }

    [Fact]
    public void CustomAssStyleIsScaledAndRecognizedWithoutChangingEvents()
    {
        var settings = new AssStyleSettings("Arial", 24, 2, 18);
        var header = AssProfile.Header(1920, 1080, settings);
        Assert.Contains("Style: Default,Arial,72,", header);
        Assert.True(AssProfile.HasCurrent(header, 1920, 1080, settings));
        Assert.False(AssProfile.HasCurrent(header, 1920, 1080));
        Assert.Throws<SubtitleException>(() => AssStyleSettings.From(Config(new { fontFamily = "Arial,invalid" })));
    }
}
