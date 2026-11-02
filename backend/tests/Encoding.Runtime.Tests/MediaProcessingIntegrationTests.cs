using System.Text.Json;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Nodes;
using Encoding.NodeAgent.Runtime;
using Encoding.NodeAgent.Runtime.Media;
using Encoding.NodeAgent.Services;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class MediaProcessingIntegrationTests
{
    private sealed class Capabilities : IToolCapabilityProbe
    {
        public Task<IReadOnlyList<NodeToolCapability>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<NodeToolCapability>>([]);
    }
    private static Dictionary<string, JsonElement> Config(object value) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    [MediaToolsFact]
    public async Task SelectionResizeAndTwoPassNormalizationPreserveTheSource()
    {
        var directory = Path.Combine(Path.GetTempPath(), "encoding-processing-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var tools = new ProcessToolRunner();
            var scanner = new MediaScanner(tools);
            var actions = new MediaActions(tools, scanner, new Capabilities());
            var source = Path.Combine(directory, "source.mkv");
            await tools.CheckedAsync("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=duration=2:size=128x72:rate=5",
                "-f", "lavfi", "-i", "sine=duration=2:frequency=440:sample_rate=48000", "-f", "lavfi", "-i", "sine=duration=2:frequency=880:sample_rate=48000",
                "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "libx264", "-c:a", "aac", "-ac", "2",
                "-metadata:s:a:0", "language=fra", "-metadata:s:a:1", "language=jpn", source], directory, CancellationToken.None);
            var original = await SafeMediaPaths.DigestAsync(source, CancellationToken.None);
            var unchanged = await actions.FfmpegPlanAsync([new("video-resize", Config(new { resolution = "1080p", onlyIfLarger = true }))], source, directory, "small", Guid.NewGuid(), CancellationToken.None);
            Assert.Null(unchanged.Primary);
            IReadOnlyList<RuntimePlanStep> plan = [
                new("audio-transcode", Config(new { trackSelection = new[] { new { languages = "fra" } }, audioCodec = "ac3", bitrateKbps = 256, channels = "source" })),
                new("video-resize", Config(new { width = 64, height = 36, onlyIfLarger = true }))
            ];
            using var compositionCapture = RuntimeCommandCapture.Begin();
            var compositionInvocation = Guid.NewGuid();
            MediaActionOutcome converted;
            using (compositionCapture.BeginStep("composed-conversion", compositionInvocation))
                converted = await actions.FfmpegPlanAsync(plan, source, directory, "convert", Guid.NewGuid(), CancellationToken.None);
            var commands = compositionCapture.ForStep("composed-conversion", compositionInvocation);
            Assert.Single(commands, command => command.Command.Contains("ffprobe"));
            Assert.Single(commands, command => command.Command.Contains("ffmpeg"));
            Assert.NotNull(converted.Primary);
            var metadata = await scanner.ScanAsync(converted.Primary, CancellationToken.None);
            var audio = metadata.GetProperty("streams").EnumerateArray().Where(stream => stream.GetProperty("codec_type").GetString() == "audio").ToArray();
            Assert.Equal("ac3", audio[0].GetProperty("codec_name").GetString());
            Assert.Equal("aac", audio[1].GetProperty("codec_name").GetString());
            var video = metadata.GetProperty("streams").EnumerateArray().Single(stream => stream.GetProperty("codec_type").GetString() == "video");
            Assert.Equal(64, video.GetProperty("width").GetInt32());
            var normalized = await actions.FfmpegPlanAsync([new("audio-normalize", Config(new { trackId = 0, normalizationProfile = "broadcast", normalizationMethod = "twoPass" }))], source, directory, "normalize", Guid.NewGuid(), CancellationToken.None);
            Assert.NotNull(normalized.Primary);
            await scanner.ScanAsync(normalized.Primary, CancellationToken.None);
            foreach (var reverse in new[] { false, true })
            {
                RuntimePlanStep convert = new("audio-transcode", Config(new { trackId = 1, audioCodec = "ac3", bitrateKbps = 256, channels = "2", sampleRate = 48000 }));
                RuntimePlanStep normalize = new("audio-normalize", Config(new { trackId = 1, normalizationProfile = "broadcast", normalizationMethod = "twoPass" }));
                var composed = await actions.FfmpegPlanAsync(reverse ? [normalize, convert] : [convert, normalize], source, directory, "composed" + reverse, Guid.NewGuid(), CancellationToken.None);
                var result = await scanner.ScanAsync(composed.Primary!, CancellationToken.None);
                var streams = result.GetProperty("streams").EnumerateArray().Where(stream => stream.GetProperty("codec_type").GetString() == "audio").ToArray();
                Assert.Equal("ac3", streams[1].GetProperty("codec_name").GetString());
                Assert.Equal("aac", streams[0].GetProperty("codec_name").GetString());
            }
            using var capture = RuntimeCommandCapture.Begin();
            var invocation = Guid.NewGuid();
            using (capture.BeginStep("multiple-normalizations", invocation))
                await actions.FfmpegPlanAsync([
                    new("audio-normalize", Config(new { trackId = 0, normalizationProfile = "broadcast" })),
                    new("audio-normalize", Config(new { trackId = 0, normalizationProfile = "night" }))
                ], source, directory, "multiple", Guid.NewGuid(), CancellationToken.None);
            var command = Assert.Single(capture.ForStep("multiple-normalizations", invocation)).Command;
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(command, "-filter:a:0"));
            Assert.Contains("loudnorm=I=-23", command);
            Assert.Contains(",loudnorm=I=-18", command);
            var selectedOnly = await actions.FfmpegPlanAsync([
                new("audio-normalize", Config(new { trackId = 1 })),
                new("audio-transcode", Config(new { trackId = 1, audioCodec = "ac3", channels = "2", sampleRate = 48000 }))
            ], source, directory, "selected-only", Guid.NewGuid(), CancellationToken.None, preserve: false);
            var selectedMetadata = await scanner.ScanAsync(selectedOnly.Primary!, CancellationToken.None);
            var selectedStream = Assert.Single(selectedMetadata.GetProperty("streams").EnumerateArray());
            Assert.Equal("ac3", selectedStream.GetProperty("codec_name").GetString());
            var flacSource = Path.Combine(directory, "source.flac");
            await tools.CheckedAsync("ffmpeg", ["-v", "error", "-y", "-i", source, "-map", "0:a:0", "-c:a", "flac", flacSource], directory, CancellationToken.None);
            var remuxed = await actions.FfmpegPlanAsync([
                new("audio-normalize", Config(new { trackId = 0 })),
                new("video-remux-media", Config(new { container = "mkv" }))
            ], flacSource, directory, "explicit-container", Guid.NewGuid(), CancellationToken.None);
            Assert.Equal(".mkv", Path.GetExtension(remuxed.Primary));
            await scanner.ScanAsync(remuxed.Primary!, CancellationToken.None);
            Assert.Equal(original, await SafeMediaPaths.DigestAsync(source, CancellationToken.None));
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(directory));
            Directory.Delete(directory, true);
        }
    }
}
