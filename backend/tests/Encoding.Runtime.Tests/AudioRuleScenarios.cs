using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Nodes;

namespace Encoding.Runtime.Tests;

/// <summary>Règles audio du bloc base-audio-rules (architecture 0055) : décisions, déduction de disposition, refus, plan FFmpeg, éligibilité.</summary>
internal static class AudioRuleScenarios
{
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static JsonElement Streams(params (string Codec, int Channels, string? Layout, string? Title)[] audio) => J(new
    {
        streams = new object[] { new { codec_type = "video", codec_name = "h264" } }.Concat(audio.Select(item => (object)new
        {
            codec_type = "audio", codec_name = item.Codec, channels = item.Channels, channel_layout = item.Layout,
            tags = item.Title is null ? null : new { title = item.Title }
        })).ToArray()
    });

    private static void Refused(Action action, string expected, string message)
    {
        try { action(); }
        catch (AudioRuleException exception) when (exception.Message.Contains(expected, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new InvalidOperationException(message);
    }

    public static void Run(FlowComponentRegistry registry, FlowDefinitionValidator validator, RuntimeEligibility eligibility)
    {
        var options = new AudioRuleOptions();
        var plan = AudioRulePlanner.Plan(Streams(("aac", 2, "stereo", null), ("ac3", 2, "stereo", null), ("eac3", 6, "5.1(side)", null), ("dts", 6, "5.1", null),
            ("flac", 8, "7.1", null), ("aac", 1, "mono", null), ("flac", 1, "mono", null), ("ac3", 6, "5.1(side)", null), ("opus", 2, "stereo", null)), options);
        var byIndex = plan.Decisions.ToDictionary(item => item.AudioIndex);
        Check(!byIndex[0].Convert && !byIndex[1].Convert, "A stereo AAC track and a stereo AC-3 track are kept.");
        Check(byIndex[2] is { Convert: true, Codec: "ac3", Channels: 6, BitrateKbps: 640, OutputLayout: "5.1(side)" }, "5.1 E-AC-3 becomes 5.1 AC-3 with the layout of the source, side layout included.");
        Check(byIndex[3] is { Convert: true, Codec: "ac3", Channels: 6, OutputLayout: "5.1" }, "5.1 DTS becomes 5.1 AC-3.");
        Check(byIndex[4] is { Convert: true, Codec: "ac3", Channels: 6, OutputLayout: "5.1" }, "7.1 is limited to 5.1 and keeps the LFE.");
        Check(!byIndex[5].Convert, "A mono AAC track is kept.");
        Check(byIndex[6] is { Convert: true, Codec: "ac3", Channels: 1, BitrateKbps: 128 }, "A mono track in another codec becomes mono AC-3.");
        Check(!byIndex[7].Convert, "A 5.1 AC-3 track is already conform.");
        Check(byIndex[8] is { Convert: true, Codec: "aac", Channels: 2, BitrateKbps: 256 }, "Stereo Opus becomes stereo AAC.");
        Check(plan.Conversions.Count == 5, "Only the tracks that break the rules are converted.");

        Check(AudioRulePlanner.Plan(Streams(("ac3", 2, "stereo", null)), options with { KeepStereoAc3 = false }).Conversions.Single() is { Codec: "aac" }, "Stereo AC-3 can be converted when the option is off.");
        Check(AudioRulePlanner.Plan(Streams(("flac", 8, "7.0", null)), options).Conversions.Single() is { Channels: 5, OutputLayout: "5.0" }, "7.0 without LFE becomes 5.0, not 5.1.");
        Check(AudioRulePlanner.Plan(Streams(), options).Decisions.Count == 0, "A file without audio has nothing to do.");

        // Disposition inconnue : déduite seulement de références déclarées concordantes.
        var inferred = AudioRulePlanner.Plan(Streams(("eac3", 6, "5.1(side)", null), ("dts", 6, "6 channels", null)), options).Conversions;
        Check(inferred.Count == 2 && inferred[1] is { InputLayout: "5.1(side)", OutputLayout: "5.1(side)" }, "An unknown layout is deduced from a declared track with the same channel count and declared on the input.");
        Check(AudioRulePlanner.Plan(Streams(("eac3", 2, null, null)), options).Conversions.Single() is { Codec: "aac", InputLayout: "stereo" }, "Two-channel E-AC-3 without layout is stereo.");
        Check(AudioRulePlanner.Plan(Streams(("dts", 2, "unknown", "Français Stéréo")), options).Conversions.Single() is { InputLayout: "stereo" }, "A stereo title confirms a two-channel track.");
        Refused(() => AudioRulePlanner.Plan(Streams(("dts", 2, null, "Stéréo"), ("flac", 2, "downmix", null)), options), "contradictoire", "A stereo title contradicting a declared reference is refused.");
        Refused(() => AudioRulePlanner.Plan(Streams(("dts", 6, "6 channels", null)), options), "aucune référence", "No reference means no conversion.");
        Refused(() => AudioRulePlanner.Plan(Streams(("dts", 6, "6 channels", null), ("flac", 6, "5.1", null), ("flac", 6, "5.1(back)", null)), options), "contradictoires", "Contradicting references are refused.");
        Refused(() => AudioRulePlanner.Plan(Streams(("eac3", 6, "5.1(side)", null), ("dts", 6, "6 channels", null)), options with { InferUnknownLayout = false }), "inconnue", "Inference can be turned off.");
        Refused(() => AudioRulePlanner.Plan(Streams(("flac", 5, "5.0(wide)", null)), options), "AC3", "A layout AC-3 cannot carry is refused before anything is replaced.");
        Refused(() => AudioRulePlanner.Plan(J(new { streams = new object[] { new { codec_type = "audio", codec_name = "aac" } } }), options), "canaux", "An unknown channel count is refused.");

        var steps = plan.Steps("rules", options);
        Check(steps.Count == 5 && steps.All(step => step.ComponentId == "base-transcode-audio" && step.FlowNodeId == "rules"), "The rules reuse the common FFmpeg plan.");
        var five = steps.Single(step => step.Configuration["trackId"].GetInt32() == 2).Configuration;
        Check(five["audioCodec"].GetString() == "ac3" && five["channels"].GetString() == "6" && five["bitrateKbps"].GetInt32() == 640
            && five["outputLayout"].GetString() == "5.1(side)" && five["sampleRate"].GetInt32() == 48000, "A plan step carries codec, channels, bitrate, layout and sample rate.");
        Check(!AudioRulePlanner.Plan(Streams(("eac3", 2, null, null)), options with { SampleRate = 0 }).Steps(null, options with { SampleRate = 0 }).Single().Configuration.ContainsKey("sampleRate"), "A zero sample rate keeps the source rate.");

        var audioManifest = registry.Plugins.Single(item => item.Id == "audio");
        var rules = audioManifest.Components.Single(item => item.Id == "audio-rules");
        var baseManifest = registry.Plugins.Single(item => item.Id == "base");
        var input = baseManifest.Components.Single(item => item.Id == "base-input-file");
        Check(audioManifest.Version == "1.1.0" && rules.Version == "1.0.0" && rules.Outputs.Select(item => item.Id).SequenceEqual(["success", "unchanged", "error"]), "The audio rules block is declared in Audio 1.1.0 with its three outputs.");
        var wait = baseManifest.Components.Single(item => item.Id == "base-wait");
        Check(wait.CompatiblePluginVersions!.SequenceEqual(["1.1.0", "1.2.0", "1.3.0", "1.4.0", "1.5.0", "1.6.0", "1.7.0", "1.8.0", "1.9.0"])
            && input.CompatiblePluginVersions!.Contains("1.2.0") && input.CompatiblePluginVersions!.Contains("1.9.0"),
            "Flows that reference Base 1.1.0 to 1.9.0 stay valid after the version bump.");

        FlowDefinition FlowWith() => new(1, Guid.NewGuid(), 1, "Audio rules", "video", [
            new("input", null, new("base", "1.2.0", input.Id, input.Version, 1), new Dictionary<string, JsonElement>()),
            new("rules", null, new("base", "1.2.0", "base-audio-rules", "1.2.0", 1), new Dictionary<string, JsonElement> { ["processingMode"] = J("direct") })],
            [new("edge", "input", "success", "rules")]);
        var worker = Guid.NewGuid();
        NodeToolCapability Ffmpeg(params string[] encoders) => new("ffmpeg", "7.0", ["video"], ["cpu"], encoders);
        NodeToolCapability[] Node(NodeToolCapability ffmpeg) => [new("plugin/base", "1.6.0", ["general"], []), new("ffprobe", "7.0", ["video"], []), ffmpeg];
        Check(validator.Validate(FlowWith()).IsValid, "A flow with the audio rules block is valid.");
        Check(eligibility.CanRun(FlowWith(), Node(Ffmpeg("aac", "ac3")), worker), "A worker with AAC and AC-3 encoders can run the rules.");
        Check(!eligibility.CanRun(FlowWith(), Node(Ffmpeg("aac")), worker), "A worker without an AC-3 encoder never receives the rules.");
        Check(!eligibility.CanRun(FlowWith(), [new("plugin/base", "1.1.0", ["general"], []), new("ffprobe", "7.0", ["video"], []), Ffmpeg("aac", "ac3")], worker), "A worker that does not announce Base 1.2.0 never receives the rules.");
    }
}
