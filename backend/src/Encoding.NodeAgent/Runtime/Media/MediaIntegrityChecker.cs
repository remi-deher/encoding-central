using System.Globalization;
using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Nodes;

namespace Encoding.NodeAgent.Runtime.Media;

public sealed record MediaIntegrityReport(string Sha256, string Mode, string Acceleration,
    string Output, double DurationSeconds, string? Reason = null);

/// <summary>Checks only the exact file digest requested by the flow; no publication side effects.</summary>
public sealed class MediaIntegrityChecker(ProcessToolRunner tools, MediaScanner scanner)
{
    public async Task<MediaIntegrityReport> CheckAsync(string path, string sha256, string mediaType,
        string mode, string acceleration, IReadOnlyList<NodeToolCapability> capabilities, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        MediaIntegrityReport Report(string output, string used, string? reason = null) =>
            new(sha256, mode, used, output, started.Elapsed.TotalSeconds, reason);
        if (mode is not ("quick" or "deep") || acceleration is not ("auto" or "cpu" or "gpu"))
            return Report("error", "none", "invalid_configuration");
        try
        {
            if (await SafeMediaPaths.DigestAsync(path, ct) != sha256) return Report("error", "none", "artifact_changed");
            var metadata = await scanner.ValidateAsync(path, mediaType, null, ct);
            var ffmpeg = ProcessToolRunner.Find("ffmpeg");
            if (ffmpeg is null) return Report("error", "none", "ffmpeg_unavailable");
            var used = "cpu";
            var hardware = Array.Empty<string>();
            // Encoder advertisement alone does not prove decoding support. Probe this exact codec/file
            // using an explicitly requested hardware frame format before selecting acceleration.
            if (mode == "deep" && acceleration != "cpu" && mediaType == "video")
            {
                var announced = capabilities.FirstOrDefault(tool => tool.ToolId == "ffmpeg")?.AccelerationTypes ?? [];
                foreach (var (name, api) in new[] { ("nvenc", "cuda"), ("qsv", "qsv"), ("vaapi", "vaapi") })
                {
                    if (!announced.Contains(name, StringComparer.Ordinal)) continue;
                    string[] candidate = api switch {
                        "vaapi" => ["-vaapi_device", Encoding.NodeAgent.Services.GpuDeviceSelector.RenderDevice(), "-hwaccel", "vaapi", "-hwaccel_output_format", "vaapi"],
                        "qsv" => ["-init_hw_device", $"qsv=hw,child_device={Encoding.NodeAgent.Services.GpuDeviceSelector.RenderDevice()}", "-filter_hw_device",
                            "hw", "-hwaccel", "qsv", "-hwaccel_output_format", "qsv"],
                        _ => ["-hwaccel", api, "-hwaccel_output_format", api]
                    };
                    var probe = await DecodeAsync(ffmpeg, path, candidate, 0, 2, ct);
                    if (probe.ExitCode != 0 || !string.IsNullOrWhiteSpace(probe.Error)) continue;
                    hardware = candidate;
                    used = api;
                    break;
                }
                if (used == "cpu" && acceleration == "gpu") return Report("error", "none", "gpu_decoder_unavailable");
            }
            else if (mode == "deep" && acceleration == "gpu")
                return Report("error", "none", "gpu_video_required");
            if (mode == "quick")
            {
                var duration = Duration(metadata);
                if (duration is null || duration <= 0) return Report("error", "cpu", "duration_unavailable");
                foreach (var at in new[] { 0d, Math.Max(0, duration.Value / 2 - 2), Math.Max(0, duration.Value - 5) }.Distinct())
                {
                    var result = await DecodeAsync(ffmpeg, path, [], at, Math.Min(5, duration.Value - at), ct);
                    if (result.ExitCode != 0 || !string.IsNullOrWhiteSpace(result.Error))
                        return Report("invalid", "cpu", "decode_errors");
                }
            }
            else
            {
                var result = await DecodeAsync(ffmpeg, path, hardware, null, null, ct, Duration(metadata));
                if (result.ExitCode != 0 || !string.IsNullOrWhiteSpace(result.Error))
                    return Report("invalid", used, "decode_errors");
            }
            if (await SafeMediaPaths.DigestAsync(path, ct) != sha256) return Report("error", used, "artifact_changed");
            return Report("valid", used);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Report("error", "none", "inspection_timeout"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or MediaToolException or JsonException)
        {
            return Report("error", "none", exception is UnauthorizedAccessException ? "permission_denied" : "inspection_unavailable");
        }
    }

    private Task<ToolResult> DecodeAsync(string executable, string path, string[] hardware,
        double? offset, double? length, CancellationToken ct, double? duration = null)
    {
        var args = new List<string> { "-nostdin", "-v", "error", "-xerror", "-progress", "pipe:1", "-stats_period", "10" };
        if (offset is { } start) args.AddRange(["-ss", start.ToString("0.###", CultureInfo.InvariantCulture)]);
        args.AddRange(hardware);
        args.AddRange(["-i", path]);
        if (length is { } seconds) args.AddRange(["-t", seconds.ToString("0.###", CultureInfo.InvariantCulture)]);
        args.AddRange(["-map", "0:v?", "-map", "0:a?", "-fps_mode", "vfr", "-f", "null", "-"]);
        return tools.RunAsync(executable, args, Path.GetDirectoryName(path)!, TimeSpan.FromHours(6), ct, trackProgress: true, progressDurationSeconds: length ?? duration);
    }

    private static double? Duration(JsonElement metadata) => metadata.TryGetProperty("format", out var format)
        && format.TryGetProperty("duration", out var duration)
        && double.TryParse(duration.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && double.IsFinite(value) ? value : null;
}
