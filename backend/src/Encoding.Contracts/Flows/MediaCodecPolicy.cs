using System.Text.Json;

namespace Encoding.Contracts.Flows;

public static class MediaCodecPolicy
{
    /// <summary>Le worker (ffmpeg annoncé) a-t-il un encodeur utilisable du profil de chaque bloc vidéo du flow ? Faux sans bloc vidéo à profil.</summary>
    public static bool ProfileEncoderUsable(FlowDefinition flow, Encoding.Contracts.Nodes.NodeToolCapability? ffmpeg)
    {
        var videoNodes = flow.Nodes.Where(node => node.Component.ComponentId is "base-transcode-video" or "video-transcode"
            && node.Configuration.TryGetValue("encodingProfile", out var profile) && profile.ValueKind == JsonValueKind.Object).ToList();
        if (ffmpeg is null || videoNodes.Count == 0) return false;
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var node in videoNodes)
        {
            var profile = node.Configuration["encodingProfile"].Deserialize<EncodingProfileSnapshot>(web);
            if (profile is null) return false;
            var codec = node.Configuration.TryGetValue("videoCodec", out var codecValue) ? codecValue.GetString()! : "hevc";
            var retryWithCpu = node.Configuration.TryGetValue("retryWithCpu", out var retry) && retry.ValueKind == JsonValueKind.True;
            var encoders = ffmpeg.Encoders ?? [];
            var usable = ResolveVideoEncoder(codec, "automatic", null, profile, encoders, ffmpeg.AccelerationTypes, ffmpeg.EncoderChecks) is not null
                || retryWithCpu && ResolveCpuFallbackVideoEncoder(codec, profile, encoders, ffmpeg.AccelerationTypes, ffmpeg.EncoderChecks) is not null;
            if (!usable) return false;
        }
        return true;
    }

    public static string? VideoEncoder(string codec,string hardware,IReadOnlyList<string> available)
    {
        var candidates = VideoEncoderCatalog.All.Where(encoder => encoder.Codec == codec && encoder.Acceleration == hardware);
        return candidates.Select(encoder => encoder.Id).FirstOrDefault(available.Contains);
    }

    public static (VideoEncoderDescriptor Encoder, EncodingProfileTarget Target)? ResolveVideoEncoder(
        string codec,
        string mode,
        string? selectedEncoderId,
        EncodingProfileSnapshot profile,
        IReadOnlyList<string> availableEncoders,
        IReadOnlyList<string> availableAccelerations,
        IReadOnlyList<Encoding.Contracts.Nodes.NodeEncoderCapability>? encoderChecks = null)
    {
        var targets = profile.Targets
            .Where(target => VideoEncoderCatalog.Find(target.EncoderId)?.Codec == codec)
            .Where(target => mode != "specific" || target.EncoderId == selectedEncoderId)
            .OrderBy(target => target.Priority);
        foreach (var target in targets)
        {
            var descriptor = VideoEncoderCatalog.Find(target.EncoderId)!;
            var verified = encoderChecks is null
                || encoderChecks.Any(check => check.EncoderId == descriptor.Id && check.Status == "usable");
            if (availableEncoders.Contains(descriptor.Id, StringComparer.Ordinal)
                && availableAccelerations.Contains(descriptor.Acceleration, StringComparer.Ordinal)
                && verified)
                return (descriptor, target);
        }
        return null;
    }

    public static (VideoEncoderDescriptor Encoder, EncodingProfileTarget Target)? ResolveCpuFallbackVideoEncoder(
        string codec,
        EncodingProfileSnapshot profile,
        IReadOnlyList<string> availableEncoders,
        IReadOnlyList<string> availableAccelerations,
        IReadOnlyList<Encoding.Contracts.Nodes.NodeEncoderCapability>? encoderChecks = null)
    {
        var cpuTargets=profile.Targets
            .Where(target=>VideoEncoderCatalog.Find(target.EncoderId) is { } descriptor&&descriptor.Codec==codec&&descriptor.Acceleration=="cpu")
            .OrderBy(target=>target.Priority)
            .ToList();
        var nextPriority=cpuTargets.Select(target=>target.Priority).DefaultIfEmpty(-1).Max()+1;
        foreach(var encoder in VideoEncoderCatalog.All.Where(encoder=>encoder.Codec==codec&&encoder.Acceleration=="cpu"&&!cpuTargets.Any(target=>target.EncoderId==encoder.Id)))
            cpuTargets.Add(new(encoder.Id,encoder.DefaultQuality,encoder.Presets.FirstOrDefault(preset=>preset.Id=="medium")?.Id??string.Empty,nextPriority++));
        return ResolveVideoEncoder(codec,"automatic",null,profile with { Targets=cpuTargets },availableEncoders,availableAccelerations,encoderChecks);
    }

    public static decimal ResolveQuality(VideoEncoderDescriptor encoder, string tier, int width, int height, decimal fallbackQuality)
    {
        if (string.IsNullOrWhiteSpace(tier) || tier == "custom") return fallbackQuality;

        var is4k = width >= 3200 || height >= 1800;
        var is720pOrLess = (width > 0 && width < 1600) || (height > 0 && height < 900);

        int baseline;
        int delta4k = 3;
        int deltaLow = -2;

        if (encoder.Id == "libsvtav1")
        {
            baseline = tier switch
            {
                "transparent" => 24,
                "high" => 28,
                "balanced" => 30,
                "compact" => 34,
                _ => 30
            };
            delta4k = 4;
            deltaLow = -3;
        }
        else if (encoder.Acceleration is "nvenc" or "qsv")
        {
            baseline = tier switch
            {
                "transparent" => 20,
                "high" => 23,
                "balanced" => 25,
                "compact" => 28,
                _ => 25
            };
        }
        else // cpu (x264, x265) or vaapi
        {
            baseline = tier switch
            {
                "transparent" => 18,
                "high" => 21,
                "balanced" => 23,
                "compact" => 26,
                _ => 23
            };
        }

        var resolved = baseline;
        if (is4k) resolved += delta4k;
        else if (is720pOrLess) resolved += deltaLow;

        return Math.Clamp(resolved, encoder.QualityMinimum, encoder.QualityMaximum);
    }

    public static string? ResolvePixelFormat(VideoEncoderDescriptor encoder, string? colorDepth, string? sourcePixelFormat)
    {
        var depth = colorDepth?.Trim().ToLowerInvariant() ?? "auto";
        // « auto » et « source » alignent la sortie sur l'entrée : 8 bits en 8 bits, 10 bits (ou plus) en 10 bits.
        var is10Bit = depth switch
        {
            "10-bit" => true,
            "8-bit" => false,
            _ => IsHighBitDepth(sourcePixelFormat)
        };

        if (encoder.Acceleration == "vaapi")
            return null;

        if (is10Bit)
        {
            if (encoder.Acceleration is "nvenc" or "qsv")
                return "p010le";
            return "yuv420p10le";
        }

        return "yuv420p";
    }

    private static bool IsHighBitDepth(string? pixelFormat) =>
        !string.IsNullOrEmpty(pixelFormat) && System.Text.RegularExpressions.Regex.IsMatch(pixelFormat, @"p01[026]|(?<!\d)(10|12|14|16)(?!\d)");

    public static void ApplyHdrMetadata(List<string> args, JsonElement? videoStream, string encoderId)
    {
        if (videoStream is not { ValueKind: JsonValueKind.Object } stream) return;

        string? primaries = null, transfer = null, space = null;
        if (stream.TryGetProperty("color_primaries", out var cp) && cp.ValueKind == JsonValueKind.String) primaries = cp.GetString();
        if (stream.TryGetProperty("color_transfer", out var ct) && ct.ValueKind == JsonValueKind.String) transfer = ct.GetString();
        if (stream.TryGetProperty("color_space", out var cs) && cs.ValueKind == JsonValueKind.String) space = cs.GetString();

        var isHdr = transfer is "smpte2084" or "arib-std-b67" || primaries == "bt2020";
        if (!isHdr) return;

        if (!string.IsNullOrEmpty(primaries)) args.AddRange(["-color_primaries", primaries]);
        if (!string.IsNullOrEmpty(transfer)) args.AddRange(["-color_trc", transfer]);
        if (!string.IsNullOrEmpty(space)) args.AddRange(["-colorspace", space]);

        if (encoderId == "libx265" && transfer == "smpte2084")
            args.AddRange(["-x265-params", "hdr10=1:hdr10-opt=1:repeat-headers=1"]);
    }

    public static string? AudioEncoder(string codec,IReadOnlyList<string> available)=>
        (codec=="opus"?new[]{"libopus","opus"}:new[]{codec}).FirstOrDefault(available.Contains);
}
