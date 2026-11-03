using System.Text.Json;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Workers;
using Encoding.WorkerAgent.Runtime;
using Encoding.WorkerAgent.Runtime.Media;
using Encoding.WorkerAgent.Runtime.Media.Handlers;
using Encoding.WorkerAgent.Services;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class ContainerPredictabilityTests
{
    private static Dictionary<string, JsonElement> Config(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    [Fact]
    public void CommandsKeepTheContainerTheirBlockDeclares()
    {
        Assert.Equal(".mp4", MediaActions.PlannedContainerExtension("/media/film.mp4", [new("video-transcode", Config(new { videoCodec = "hevc" }))]));
        Assert.Equal(".webm", MediaActions.PlannedContainerExtension("/media/film.webm", [new("audio-transcode", Config(new { audioCodec = "aac" }))]));
        Assert.Equal(".mkv", MediaActions.PlannedContainerExtension("/media/film.mp4", [new("video-remux-media", Config(new { container = "mkv" }))]));
        Assert.Null(MediaActions.AudioOnlyContainerOverride(".mp4", "flac"));
        Assert.Equal(".m4a", MediaActions.AudioOnlyContainerOverride(".flac", "aac"));
        Assert.Equal(".opus", MediaActions.AudioOnlyContainerOverride(".mp3", "opus"));
        Assert.Null(MediaActions.AudioOnlyContainerOverride(".mka", "eac3"));
        Assert.Equal("libx264", MediaActions.SameCodecVideoEncoder("h264"));
        Assert.Equal("libx265", MediaActions.SameCodecVideoEncoder("hevc"));
        Assert.Equal("libvpx-vp9", MediaActions.SameCodecVideoEncoder("vp9"));
        Assert.Throws<IOException>(() => MediaActions.SameCodecVideoEncoder("mjpeg"));
    }

    [MediaToolsFact]
    public async Task Mp4AudioResizeAndSubtitleStayMp4()
    {
        var directory = Path.Combine(Path.GetTempPath(), "encoding-container-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var tools = new ProcessToolRunner();
            var scanner = new MediaScanner(tools);
            var actions = new MediaActions(tools, scanner, new EmptyCapabilities());
            var source = Path.Combine(directory, "source.mp4");
            await tools.CheckedAsync("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=duration=1:size=64x36:rate=8",
                "-f", "lavfi", "-i", "sine=duration=1:frequency=440:sample_rate=48000",
                "-f", "lavfi", "-i", "sine=duration=1:frequency=880:sample_rate=48000",
                "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "libx264", "-c:a", "aac",
                "-metadata:s:a:0", "language=fra", "-metadata:s:a:1", "language=eng", source], directory, CancellationToken.None);
            var resized = await actions.FfmpegPlanAsync([new("video-resize", Config(new { width = 32, height = 18 }))], source, directory, "resize", Guid.NewGuid(), CancellationToken.None);
            Assert.EndsWith(".mp4", resized.Primary, StringComparison.OrdinalIgnoreCase);
            var resizedScan = await scanner.ScanAsync(resized.Primary!, CancellationToken.None);
            Assert.Equal("h264", resizedScan.GetProperty("streams").EnumerateArray().Single(stream => stream.GetProperty("codec_type").GetString() == "video").GetProperty("codec_name").GetString());
            var flac = await actions.FfmpegPlanAsync([new("audio-transcode", Config(new { trackId = 0, audioCodec = "flac" }))], source, directory, "flac", Guid.NewGuid(), CancellationToken.None);
            Assert.EndsWith(".mp4", flac.Primary, StringComparison.OrdinalIgnoreCase);
            var filtered = await actions.FilterLanguagesAsync(Config(new { allowedAudioLanguages = "fra", allowedSubtitleLanguages = "fra", keepUndefinedAudio = false }),
                await scanner.ScanAsync(source, CancellationToken.None), source, directory, "filter", CancellationToken.None);
            Assert.EndsWith(".mp4", filtered.Primary, StringComparison.OrdinalIgnoreCase);
            var filteredScan = await scanner.ScanAsync(filtered.Primary!, CancellationToken.None);
            Assert.Single(filteredScan.GetProperty("streams").EnumerateArray(), stream => stream.GetProperty("codec_type").GetString() == "video");
            Assert.Single(filteredScan.GetProperty("streams").EnumerateArray(), stream => stream.GetProperty("codec_type").GetString() == "audio");
            var flacSource = Path.Combine(directory, "source.flac");
            await tools.CheckedAsync("ffmpeg", ["-v", "error", "-y", "-i", source, "-map", "0:a:0", "-c:a", "flac", flacSource], directory, CancellationToken.None);
            var normalized = await actions.FfmpegPlanAsync([new("audio-normalize", Config(new { trackId = 0 }))], flacSource, directory, "normalize", Guid.NewGuid(), CancellationToken.None);
            Assert.Equal(".m4a", Path.GetExtension(normalized.Primary));
            var subtitle = Path.Combine(directory, "source.srt");
            await File.WriteAllTextAsync(subtitle, "1\n00:00:00,000 --> 00:00:01,000\nBonjour\n");
            var handler = new MatroskaActionHandler(tools);
            await Assert.ThrowsAsync<IOException>(() => handler.ExecuteAsync(new MediaActionContext("mkv-remux-matroska", new Dictionary<string, JsonElement>(), source, directory, "remux", _ => Task.FromResult(""), Guid.NewGuid()), CancellationToken.None));
            var added = await handler.ExecuteAsync(new MediaActionContext("subtitles-add-track", Config(new { sourceFile = subtitle, language = "fra", trackName = "Français" }),
                source, directory, "add", path => Task.FromResult(path), Guid.NewGuid()), CancellationToken.None);
            Assert.EndsWith(".mp4", added.Primary, StringComparison.OrdinalIgnoreCase);
            var addedScan = await scanner.ScanAsync(added.Primary!, CancellationToken.None);
            Assert.Contains(addedScan.GetProperty("streams").EnumerateArray(), stream => stream.GetProperty("codec_type").GetString() == "subtitle");
            Assert.DoesNotContain(Directory.GetFiles(directory, "*.mkv"), _ => true);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed class EmptyCapabilities : IToolCapabilityProbe
    {
        public Task<IReadOnlyList<WorkerToolCapability>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<WorkerToolCapability>>([]);
    }
}
