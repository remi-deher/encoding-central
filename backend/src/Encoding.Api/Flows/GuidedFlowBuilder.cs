using System.Text.Json;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Flows;

public sealed record GuidedFlowRequest(Guid Id, string Name, string Codec = "hevc",
    string Encoder = "automatic", string Quality = "balanced", string Audio = "keep", bool RetryWithCpu = false);

/// <summary>Produces an ordinary, editable graph using installed contracts; never executes or publishes it.</summary>
public static class GuidedFlowBuilder
{
    public static FlowDefinition Build(GuidedFlowRequest request, FlowComponentRegistry registry)
    {
        if (request.Id == Guid.Empty || string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128
            || request.Codec is not ("hevc" or "h264" or "av1")
            || request.Encoder is not ("automatic" or "cpu")
            || request.Quality is not ("balanced" or "high" or "compact")
            || request.Audio is not ("keep" or "adapt"))
            throw new ArgumentException("Les choix de l’assistant sont invalides.");

        var nodes = new List<FlowNodeDefinition>();
        var connections = new List<FlowConnectionDefinition>();
        void Add(string id, string pluginId, string componentId, object configuration)
        {
            var plugin = registry.Plugins.Single(p => p.Id == pluginId);
            var component = plugin.Components.Single(c => c.Id == componentId);
            nodes.Add(new(id, null, new(plugin.Id, plugin.Version, component.Id, component.Version,
                component.ConfigurationSchema.Version), JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    JsonSerializer.Serialize(configuration, FlowRepository.JsonOptions))!));
        }
        void Link(string from, string output, string to) => connections.Add(new($"link-{connections.Count + 1}", from, output, to));
        Add("input", "base", "base-input-video", new { });
        Add("prepare", "base", FfmpegBuilderPolicy.Start, new { preserveUnchangedTracks = true });
        Add("video", "video", "video-transcode", new {
            processingMode = "plan", videoCodec = request.Codec,
            encodingProfile = BuiltInEncodingProfiles.Balanced,
            encoderMode = request.Encoder == "cpu" ? "specific" : "automatic",
            encoderId = request.Codec switch { "h264" => "libx264", "av1" => "libsvtav1", _ => "libx265" },
            qualityTier = request.Quality, colorDepth = "auto", preserveHdr = true,
            retryWithCpu = request.Encoder == "automatic" && request.RetryWithCpu
        });
        Link("input", "success", "prepare");
        Link("prepare", "success", "video");
        var previous = "video";
        if (request.Audio == "adapt")
        {
            Add("audio", "audio", "audio-rules", new { processingMode = "plan", keepStereoAc3 = true, unknownLayout = "fail", sampleRate = 48000 });
            Link(previous, "success", "audio");
            previous = "audio";
        }
        Add("container", "video", "video-remux-media", new { processingMode = "plan", container = "mkv" });
        Link(previous, "success", "container");
        if (request.Audio == "adapt") Link("audio", "unchanged", "container");
        Add("execute", "base", FfmpegBuilderPolicy.Execute, new { });
        Add("publish", "base", "base-publish-result", new { });
        Link("container", "success", "execute");
        Link("execute", "success", "publish");
        Link("execute", "skipped", "publish");
        return new(1, request.Id, 1, request.Name.Trim(), "video", nodes, connections);
    }
}
