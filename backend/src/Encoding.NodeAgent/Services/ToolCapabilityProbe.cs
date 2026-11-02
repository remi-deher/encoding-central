using Encoding.Contracts.Plugins;
using Encoding.Core.Runtime;
using System.Text.RegularExpressions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Nodes;
using Encoding.NodeAgent.Runtime;

namespace Encoding.NodeAgent.Services;

public sealed class ToolCapabilityProbe(
    IProcessToolRunner processes,
    PluginProcessHost plugins,
    NodeAgentOptions options,
    ILogger<ToolCapabilityProbe> logger) : IToolCapabilityProbe
{
    private static readonly Regex EncoderLine = new(
        @"^\s*(?<flags>[A-Z\.]{6})\s+(?<name>[A-Za-z0-9_-]+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly SemaphoreSlim gate = new(1, 1);
    private IReadOnlyList<NodeToolCapability>? cached;
    private DateTimeOffset refreshed;
    private string? lastHardwareSummary;

    public async Task InvalidateCacheAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { cached = null; refreshed = default; }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<NodeToolCapability>> DiscoverAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (cached is not null && refreshed > DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5))
                return cached;

            Directory.CreateDirectory(options.DataDirectory);
            var list = new List<NodeToolCapability>
            {
                new("plugin/base", "1.9.0", ["general"], ["cpu"]),
                new(Encoding.Contracts.Executions.SourceWriteProtocol.Capability, "1.0.0", ["general"], []),
                new(FlowArtifactReference.Capability, "1.0.0", ["general"], []),
                new(Encoding.Contracts.Executions.RuntimeSourceObservation.Capability, "1.0.0", ["general"], []),
                new(MediaIntegrityPolicy.Capability, "1.0.0", ["general"], []),
                new("ai-runtime", "1.0.0", ["subtitles", "video", "music"], []),
                new("plugin/nodes", "1.8.0", ["general"], []),
                new("plugin/conditions", "1.2.0", ["general"], [])
            };
            // L'aide tools des scripts (0054) exige le même bac à sable que les scripts eux-mêmes.
            if (ProcessSandbox.IsAvailable) list.Add(new(ScriptTools.CapabilityId, ScriptTools.CapabilityVersion, ["general"], []));

            foreach (var (id, names, args) in new (string, string[], string[])[]
            {
                ("ffmpeg", ["ffmpeg"], ["-version"]), ("ffprobe", ["ffprobe"], ["-version"]),
                ("mkvtoolnix", ["mkvmerge"], ["--version"]), ("mediainfo", ["mediainfo"], ["--Version"]),
                ("imagemagick", ["magick", "convert"], ["-version"]), ("python", ["python3", "python"], ["--version"]),
                ("javascript", ["node"], ["--version"]),
                ("codex-cli", ["codex", "codex-cli"], ["--version"]), ("claude-cli", ["claude", "claude-cli"], ["--version"])
            })
            {
                if (id is "python" or "javascript" && !ProcessSandbox.IsAvailable) continue;
                var executable = names.Select(processes.Find).FirstOrDefault(path => path is not null);
                if (executable is null) continue;

                try
                {
                    var result = await processes.RunAsync(executable, args, options.DataDirectory, TimeSpan.FromSeconds(10), ct);
                    if (result.ExitCode != 0) continue;
                    var version = Regex.Match(result.Output, @"\d+\.\d+(?:\.\d+)?").Value;
                    if (version.Length == 0) version = "detected";

                    var encoders = new List<string>();
                    var checks = new List<NodeEncoderCapability>();
                    var acceleration = new List<string>();
                    if (id == "ffmpeg")
                    {
                        var encoderOutput = await processes.RunAsync(executable, ["-hide_banner", "-encoders"],
                            options.DataDirectory, TimeSpan.FromSeconds(10), ct);
                        if (encoderOutput.ExitCode != 0) continue;

                        encoders.AddRange(encoderOutput.Output.Split('\n')
                            .Select(line => EncoderLine.Match(line))
                            .Where(match => match.Success)
                            .Select(match => match.Groups["name"].Value)
                            .Distinct(StringComparer.Ordinal));

                        foreach (var encoder in VideoEncoderCatalog.All.Where(item => encoders.Contains(item.Id, StringComparer.Ordinal)))
                        {
                            var usable = await CheckEncoderAsync(executable, encoder, ct);
                            checks.Add(new(encoder.Id, usable ? "usable" : "detected"));
                            if (usable && !acceleration.Contains(encoder.Acceleration, StringComparer.Ordinal))
                                acceleration.Add(encoder.Acceleration);
                        }
                    }

                    if (id == "ffmpeg") LogHardwareEncoding(encoders, checks);
                    list.Add(new(id, version, ["video", "audio", "image", "subtitles", "general"],
                        acceleration, encoders, id == "ffmpeg" ? checks : null, ToolPackageSha256: ManagedToolCatalog.PackageSha256ForCapability(id)));
                }
                catch (Exception) when (!ct.IsCancellationRequested) { }
            }

            foreach (var plugin in plugins.Installed)
            {
                try
                {
                    var manifest = await plugins.DescribeAsync(plugin, ct);
                    if ((manifest.RequiredToolPackages ?? []).Any(dependency => !ManagedToolCatalog.Satisfies(dependency))) continue;
                    if (plugin.IsNative) list.Add(new("plugin/" + manifest.Id, manifest.Version, ["general"], ["engine"], ManifestFingerprint: PluginManifestFingerprint.Compute(manifest)));
                    else list.Add(new("plugin/" + manifest.Id, manifest.Version, ["general"], ["process"], ManifestFingerprint: PluginManifestFingerprint.Compute(manifest)));
                }
                catch (Exception) when (!ct.IsCancellationRequested) { }
            }

            cached = list;
            refreshed = DateTimeOffset.UtcNow;
            return list;
        }
        finally { gate.Release(); }
    }

    /// <summary>Journalise au démarrage (et à chaque rafraîchissement du cache) le GPU retenu ou la raison du repli CPU.</summary>
    private void LogHardwareEncoding(IReadOnlyCollection<string> encoders, IReadOnlyList<NodeEncoderCapability> checks)
    {
        var usable = checks.Where(check => check.Status == "usable").Select(check => check.EncoderId).ToList();
        var hardware = usable.Where(id => !id.StartsWith("lib", StringComparison.Ordinal)).ToList();
        var device = GpuDeviceSelector.RenderDevice();
        var devices = GpuDeviceSelector.RenderDevices();
        var nvidia = GpuDeviceSelector.NvidiaPresent();
        var summary = string.Join(",", hardware) + "|" + device + "|" + nvidia;
        if (summary == lastHardwareSummary) return; // le cache se rafraîchit toutes les 5 minutes : ne journaliser que les changements
        lastHardwareSummary = summary;
        if (hardware.Count > 0)
        {
            logger.LogInformation("Encodage matériel actif : {Encoders} (périphérique de rendu {Device}, NVIDIA {Nvidia}, LIBVA_DRIVER_NAME={Driver}).",
                string.Join(", ", hardware), device, nvidia ? "présent" : "absent",
                Environment.GetEnvironmentVariable("LIBVA_DRIVER_NAME") ?? "automatique");
            return;
        }

        var reasons = new List<string>();
        if (!nvidia) reasons.Add("NVIDIA : ni /dev/nvidiactl ni nvidia-smi (NVIDIA Container Toolkit absent ou GPU non réservé)");
        else if (!encoders.Any(id => id.EndsWith("_nvenc", StringComparison.Ordinal))) reasons.Add("NVIDIA : FFmpeg sans encodeur NVENC");
        else reasons.Add("NVIDIA : présent mais NVENC inutilisable (pilote hôte sans capability video ?)");
        if (devices.Count == 0) reasons.Add("Intel/AMD : aucun /dev/dri/renderD* (périphérique non monté dans le conteneur)");
        else reasons.Add($"Intel/AMD : {string.Join(", ", devices)} présent(s), mais VAAPI/QSV inutilisable sur {device} "
            + "(pilote VA-API manquant, droits sur le périphérique ou mauvais GPU ; voir `vainfo`, ENCODING_GPU_DEVICE, LIBVA_DRIVER_NAME)");
        logger.LogWarning("Aucun encodage matériel utilisable, repli sur le CPU. {Reasons}", string.Join(" ; ", reasons));
    }

    private async Task<bool> CheckEncoderAsync(string executable, VideoEncoderDescriptor encoder, CancellationToken ct)
    {
        var args = new List<string> { "-nostdin", "-hide_banner", "-v", "error" };
        if (encoder.Acceleration == "vaapi")
            args.AddRange(["-vaapi_device", GpuDeviceSelector.RenderDevice()]);
        else if (encoder.Acceleration == "qsv")
            args.AddRange(["-init_hw_device", $"qsv=hw,child_device={GpuDeviceSelector.RenderDevice()}", "-filter_hw_device", "hw"]);

        args.AddRange(["-f", "lavfi", "-i", "color=c=black:s=64x64:r=1", "-frames:v", "1", "-an"]);
        if (encoder.Acceleration == "vaapi") args.AddRange(["-vf", "format=nv12,hwupload"]);
        if (encoder.Acceleration == "qsv") args.AddRange(["-vf", "format=nv12,hwupload=extra_hw_frames=64,format=qsv"]);
        args.AddRange(["-c:v", encoder.Id, "-f", "null", "-"]);

        try
        {
            var result = await processes.RunAsync(executable, args, options.DataDirectory, TimeSpan.FromSeconds(4), ct,
                outputLimit: 64 * 1024);
            return result.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (IOException) when (!ct.IsCancellationRequested) { return false; }
    }
}
