using System.Globalization;
using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Nodes;
using Encoding.NodeAgent.Services;
using Microsoft.Extensions.Logging;

namespace Encoding.NodeAgent.Runtime.Media;

public sealed partial class MediaActions
{
    public async Task<MediaActionOutcome> FfmpegPlanAsync(IReadOnlyList<RuntimePlanStep> plan,string current,string directory,string node,Guid workerId,
        CancellationToken ct,bool preserve=true,IReadOnlySet<int>? forceCpuFallbackSteps=null,JsonElement? knownMetadata=null)
    {
        var (preparedPlan, inputMetadata) = await PrepareFfmpegPlanAsync(plan, current, knownMetadata, ct);
        plan = preparedPlan;
        if (plan.Count == 0) return new(null, []);

        var output = Output(directory, node, Path.GetExtension(current) is ".mp4" or ".mkv" or ".mka" or ".flac" ? Path.GetExtension(current) : ".mkv");
        var input = new List<string> { "-nostdin", "-y", "-v", "error" };
        var args = new List<string> { "-c", "copy" };
        var videoFilters = new List<string>();
        var hardwareMode = "cpu";
        var resolutions = new List<ResolvedVideoEncoding>();
        var hardwareFallbackSteps = new HashSet<int>();

        ConfigureStreamMapping(plan, args, preserve);

        for (var stepIndex = 0; stepIndex < plan.Count; stepIndex++)
        {
            var step = plan[stepIndex];
            var c = step.Configuration;
            switch (step.ComponentId)
            {
                case "base-transcode-video" or "video-transcode":
                    hardwareMode = await ApplyVideoTranscodeStepAsync(step, stepIndex, inputMetadata, workerId, input, args, resolutions, hardwareFallbackSteps, forceCpuFallbackSteps, ct);
                    break;
                case "base-transcode-audio" or "audio-transcode" or "base-normalize-audio" or "audio-normalize":
                    break;
                case "base-resize-video" or "video-resize":
                    videoFilters.Add(VideoResizePolicy.Filter(c));
                    if (!plan.Any(p => p.ComponentId is "base-transcode-video" or "video-transcode")) args.AddRange(["-c:v", "libx264"]);
                    break;
                case "base-auto-crop" or "video-auto-crop":
                    var cropF = Text(c, "cropFilter");
                    if (!string.IsNullOrEmpty(cropF)) videoFilters.Add(cropF);
                    if (!plan.Any(p => p.ComponentId is "base-transcode-video" or "video-transcode")) args.AddRange(["-c:v", "libx264"]);
                    break;
                case "base-deinterlace-video" or "video-deinterlace":
                    var deintAlgo = Text(c, "algorithm", "bwdif");
                    if (!string.IsNullOrEmpty(deintAlgo)) videoFilters.Add(deintAlgo);
                    if (!plan.Any(p => p.ComponentId is "base-transcode-video" or "video-transcode")) args.AddRange(["-c:v", "libx264"]);
                    break;
                case "base-remux-media" or "video-remux-media":
                    output = Output(directory, node, "." + Text(c, "container", "mkv"));
                    break;
                default:
                    throw new IOException("Ce réglage ne fait pas partie d’un plan FFmpeg.");
            }
        }

        if (await AppendAudioPlanAsync(plan, current, directory, input, args, preserve, ct) is { } audioExtension
            && Path.GetExtension(output) == Path.GetExtension(current))
            output = Output(directory, node, audioExtension);
        if (hardwareMode == "vaapi") videoFilters.AddRange(["format=nv12", "hwupload"]);
        if (videoFilters.Count > 0) args.AddRange(["-vf", string.Join(',', videoFilters)]);
        input.AddRange(["-i", current]);
        input.AddRange(args);
        input.Add(output);

        try
        {
            await tools.CheckedAsync("ffmpeg", input, directory, ct);
        }
        catch (MediaToolException) when (hardwareFallbackSteps.Count > 0 && forceCpuFallbackSteps is null)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(output)) File.Delete(output);
            logger?.LogWarning("Échec d’un encodage matériel dans {Node} ; nouvel essai avec les encodeurs CPU compatibles.", node);
            return await FfmpegPlanAsync(plan, current, directory, node, workerId, ct, preserve, hardwareFallbackSteps, inputMetadata);
        }
        return new(output, [], null, resolutions);
    }

    private async Task<(IReadOnlyList<RuntimePlanStep> Plan, JsonElement? InputMetadata)> PrepareFfmpegPlanAsync(
        IReadOnlyList<RuntimePlanStep> plan, string current, JsonElement? knownMetadata, CancellationToken ct)
    {
        var inputMetadata = knownMetadata;
        async Task<JsonElement> InspectAsync() => inputMetadata ??= await scanner.ScanAsync(current, ct);
        if (plan.Any(step => step.Configuration.ContainsKey("trackSelection")))
            plan = MediaTrackSelection.Expand(plan, await InspectAsync());
        if (plan.Any(step => step.ComponentId is "base-resize-video" or "video-resize"))
        {
            var metadata = await InspectAsync();
            var video = metadata.GetProperty("streams").EnumerateArray().FirstOrDefault(stream => MediaTrackSelection.Text(stream, "codec_type") == "video");
            var width = video.ValueKind == JsonValueKind.Object ? MediaTrackSelection.Number(video, "width") : 0;
            var height = video.ValueKind == JsonValueKind.Object ? MediaTrackSelection.Number(video, "height") : 0;
            plan = plan.Where(step => step.ComponentId is not ("base-resize-video" or "video-resize") || VideoResizePolicy.ShouldResize(step.Configuration, width, height)).ToArray();
        }
        return (plan, inputMetadata);
    }

    private static void ConfigureStreamMapping(IReadOnlyList<RuntimePlanStep> plan, List<string> args, bool preserve)
    {
        if (preserve || !plan.Any(p => p.ComponentId is
            "base-transcode-video" or "video-transcode" or
            "base-transcode-audio" or "audio-transcode" or
            "base-normalize-audio" or "audio-normalize" or
            "base-resize-video" or "video-resize" or
            "base-auto-crop" or "video-auto-crop" or
            "base-deinterlace-video" or "video-deinterlace"))
        {
            args.AddRange(["-map", "0"]);
        }
        else
        {
            if (plan.Any(p => p.ComponentId is
                "base-transcode-video" or "video-transcode" or
                "base-resize-video" or "video-resize" or
                "base-auto-crop" or "video-auto-crop" or
                "base-deinterlace-video" or "video-deinterlace"))
                args.AddRange(["-map", "0:v?"]);
            var audioTracks = plan
                .Where(p => p.ComponentId is "base-transcode-audio" or "audio-transcode" or "base-normalize-audio" or "audio-normalize")
                .Select(p => Number(p.Configuration, "trackId")).Distinct();
            foreach (var track in audioTracks)
                args.AddRange(["-map", "0:a:" + track]);
        }
    }

    private async Task<string> ApplyVideoTranscodeStepAsync(
        RuntimePlanStep step, int stepIndex, JsonElement? currentMetadata, Guid workerId,
        List<string> input, List<string> args, List<ResolvedVideoEncoding> resolutions,
        HashSet<int> hardwareFallbackSteps, IReadOnlySet<int>? forceCpuFallbackSteps, CancellationToken ct)
    {
        var c = step.Configuration;
        var codec = Text(c, "videoCodec", "hevc");
        var ffmpeg = (await capabilities.DiscoverAsync(ct)).FirstOrDefault(capability => capability.ToolId == "ffmpeg")
            ?? throw new MediaToolException("FFmpeg", 1);
        var (vStream, sourceWidth, sourceHeight, sourcePixFmt) = ExtractVideoProperties(currentMetadata);

        if (c.TryGetValue("encodingProfile", out var profileValue) && profileValue.ValueKind == JsonValueKind.Object)
        {
            return ApplyProfileVideoTranscode(
                step, stepIndex, codec, profileValue, ffmpeg,
                sourceWidth, sourceHeight, sourcePixFmt, vStream, c, workerId,
                input, args, resolutions, hardwareFallbackSteps, forceCpuFallbackSteps);
        }
        return ApplyManualVideoTranscode(stepIndex, codec, ffmpeg, sourceWidth, sourceHeight, sourcePixFmt, vStream, c, input, args, hardwareFallbackSteps, forceCpuFallbackSteps);
    }

    private static (JsonElement? VideoStream, int SourceWidth, int SourceHeight, string? SourcePixFmt) ExtractVideoProperties(JsonElement? currentMetadata)
    {
        if (currentMetadata is { ValueKind: JsonValueKind.Object } metaObj && metaObj.TryGetProperty("streams", out var streamList) && streamList.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streamList.EnumerateArray())
            {
                if (s.TryGetProperty("codec_type", out var ctProp) && ctProp.GetString() == "video")
                {
                    var width = s.TryGetProperty("width", out var w) && w.TryGetInt32(out var widthVal) ? widthVal : 0;
                    var height = s.TryGetProperty("height", out var h) && h.TryGetInt32(out var heightVal) ? heightVal : 0;
                    var pixFmt = s.TryGetProperty("pix_fmt", out var pf) && pf.ValueKind == JsonValueKind.String ? pf.GetString() : null;
                    return (s, width, height, pixFmt);
                }
            }
        }
        return (null, 0, 0, null);
    }

    private static string ApplyProfileVideoTranscode(
        RuntimePlanStep step, int stepIndex, string codec, JsonElement profileValue, NodeToolCapability ffmpeg,
        int sourceWidth, int sourceHeight, string? sourcePixFmt, JsonElement? vStream,
        IReadOnlyDictionary<string, JsonElement> c, Guid workerId,
        List<string> input, List<string> args, List<ResolvedVideoEncoding> resolutions,
        HashSet<int> hardwareFallbackSteps, IReadOnlySet<int>? forceCpuFallbackSteps)
    {
        var profile = profileValue.Deserialize<EncodingProfileSnapshot>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new MediaToolException("profil d’encodage", 1);
        var mode = Text(c, "encoderMode", "automatic");
        var selected = Text(c, "encoderId");
        var forceCpu = forceCpuFallbackSteps?.Contains(stepIndex) == true;
        var retryWithCpu = Flag(c, "retryWithCpu");
        var resolved = forceCpu
            ? MediaCodecPolicy.ResolveCpuFallbackVideoEncoder(codec, profile, ffmpeg.Encoders ?? [], ffmpeg.AccelerationTypes, ffmpeg.EncoderChecks)
            : MediaCodecPolicy.ResolveVideoEncoder(codec, mode, selected, profile, ffmpeg.Encoders ?? [], ffmpeg.AccelerationTypes, ffmpeg.EncoderChecks);
        var selectedAcceleration = mode == "specific" ? VideoEncoderCatalog.Find(selected)?.Acceleration : null;
        var selectedUsesHardware = selectedAcceleration is not null && selectedAcceleration != "cpu";
        var profileHasHardwareTarget = profile.Targets.Any(target => VideoEncoderCatalog.Find(target.EncoderId) is { } descriptor
            && descriptor.Codec == codec && descriptor.Acceleration != "cpu");
        if (resolved is null && !forceCpu && retryWithCpu && (selectedUsesHardware || mode == "automatic" && profileHasHardwareTarget))
            resolved = MediaCodecPolicy.ResolveCpuFallbackVideoEncoder(codec, profile, ffmpeg.Encoders ?? [], ffmpeg.AccelerationTypes, ffmpeg.EncoderChecks);
        if (resolved is null) throw new MediaToolException("encodeur vidéo compatible avec le profil", 1);

        var selectedTarget = resolved.Value;
        var encoder = selectedTarget.Encoder;
        var target = selectedTarget.Target;
        var quality = MediaCodecPolicy.ResolveQuality(encoder, Text(c, "qualityTier", "custom"), sourceWidth, sourceHeight, target.Quality);
        var hardwareMode = encoder.Acceleration;
        if (hardwareMode != "cpu" && retryWithCpu) hardwareFallbackSteps.Add(stepIndex);
        if (hardwareMode == "vaapi") input.AddRange(["-vaapi_device", GpuDeviceSelector.RenderDevice()]);
        args.AddRange(["-c:v", encoder.Id, encoder.QualityOption, quality.ToString(CultureInfo.InvariantCulture)]);
        var preset = encoder.Presets.FirstOrDefault(candidate => candidate.Id == target.Preset)?.Value;
        if (!string.IsNullOrEmpty(encoder.PresetOption) && !string.IsNullOrEmpty(preset))
            args.AddRange([encoder.PresetOption, preset]);
        var pixFmt = MediaCodecPolicy.ResolvePixelFormat(encoder, Text(c, "colorDepth", "auto"), sourcePixFmt);
        if (!string.IsNullOrEmpty(pixFmt)) args.AddRange(["-pix_fmt", pixFmt]);
        if (Flag(c, "preserveHdr", true)) MediaCodecPolicy.ApplyHdrMetadata(args, vStream, encoder.Id);
        if (step.FlowNodeId is { } flowNodeId)
            resolutions.Add(new(flowNodeId, workerId, codec, encoder.Id, ffmpeg.Version, encoder.Acceleration,
                encoder.QualityMode, encoder.QualityOption, quality, target.Preset,
                string.IsNullOrEmpty(encoder.PresetOption) ? null : encoder.PresetOption, preset,
                profile.Id, profile.Version, profile.Name, target.Priority, DateTimeOffset.UtcNow));
        return hardwareMode;
    }

    private static string ApplyManualVideoTranscode(
        int stepIndex, string codec, NodeToolCapability ffmpeg,
        int sourceWidth, int sourceHeight, string? sourcePixFmt, JsonElement? vStream,
        IReadOnlyDictionary<string, JsonElement> c,
        List<string> input, List<string> args,
        HashSet<int> hardwareFallbackSteps, IReadOnlySet<int>? forceCpuFallbackSteps)
    {
        var forceCpu = forceCpuFallbackSteps?.Contains(stepIndex) == true;
        var retryWithCpu = Flag(c, "retryWithCpu");
        var hardware = forceCpu ? "cpu" : Text(c, "hardwareAcceleration", "cpu");
        if (!forceCpu && hardware != "cpu" && retryWithCpu && MediaCodecPolicy.VideoEncoder(codec, hardware, ffmpeg.Encoders ?? []) is null)
            hardware = "cpu";
        var available = ffmpeg.Encoders ?? [];
        var encoder = MediaCodecPolicy.VideoEncoder(codec, hardware, available) ?? throw new MediaToolException("encodeur vidéo", 1);
        if (hardware != "cpu" && retryWithCpu) hardwareFallbackSteps.Add(stepIndex);
        var encDesc = VideoEncoderCatalog.Find(encoder);
        var fallbackQ = decimal.Parse(Number(c, "quality", 23), CultureInfo.InvariantCulture);
        var quality = encDesc is not null ? MediaCodecPolicy.ResolveQuality(encDesc, Text(c, "qualityTier", "custom"), sourceWidth, sourceHeight, fallbackQ) : fallbackQ;
        if (hardware == "vaapi") input.AddRange(["-vaapi_device", GpuDeviceSelector.RenderDevice()]);
        args.AddRange(["-c:v", encoder, hardware == "cpu" ? "-crf" : hardware == "nvenc" ? "-cq" : hardware == "qsv" ? "-global_quality" : "-qp", quality.ToString(CultureInfo.InvariantCulture)]);
        var preset = Text(c, "preset", "medium");
        if (encoder == "libsvtav1") preset = preset == "slow" ? "4" : preset == "fast" ? "10" : "8";
        if (hardware == "nvenc") preset = preset == "slow" ? "p7" : preset == "fast" ? "p1" : "p4";
        if (hardware != "vaapi") args.AddRange(["-preset", preset]);
        if (encDesc is not null)
        {
            var pixFmt = MediaCodecPolicy.ResolvePixelFormat(encDesc, Text(c, "colorDepth", "auto"), sourcePixFmt);
            if (!string.IsNullOrEmpty(pixFmt)) args.AddRange(["-pix_fmt", pixFmt]);
        }
        if (Flag(c, "preserveHdr", true)) MediaCodecPolicy.ApplyHdrMetadata(args, vStream, encoder);
        return hardware;
    }
}
