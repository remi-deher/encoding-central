using System.Text.Json;
using Encoding.Contracts.Workers;
using Encoding.WorkerAgent.Runtime;
using Encoding.WorkerAgent.Runtime.Media;
using Encoding.WorkerAgent.Services;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>
/// #25 (banc NAS #18) : une source MPEG-4 Part 2 (Xvid/DivX) avec images B n'a pas de PTS sur une partie de ses paquets vidéo ; le remux
/// Matroska <c>-c copy</c> échouait (« Can't write packet with unknown timestamp », code 234). Les commandes FFmpeg lisent la source avec
/// <c>-fflags +genpts</c>.
/// </summary>
public sealed class RemuxTimestampTests
{
    private static Dictionary<string, JsonElement> Config(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    [Fact]
    public void SourceInputGeneratesMissingTimestampsBeforeTheSource()
    {
        Assert.Equal(["-fflags", "+genpts", "-i", "/work/current.avi"], MediaActions.SourceInput("/work/current.avi"));
    }

    [MediaToolsFact]
    public async Task XvidWithBFramesRemuxesToMatroskaWithEveryFrameAndItsTimestamp()
    {
        var directory = Path.Combine(Path.GetTempPath(), "encoding-genpts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var tools = new ProcessToolRunner();
            var scanner = new MediaScanner(tools);
            var actions = new MediaActions(tools, scanner, new EmptyCapabilities());
            var source = Path.Combine(directory, "xvid.avi");
            // MPEG-4 Part 2 avec images B en AVI : une partie des paquets vidéo n'a pas de PTS.
            await tools.CheckedAsync("ffmpeg", ["-v", "error", "-y",
                "-f", "lavfi", "-i", "testsrc=duration=4:size=160x120:rate=25",
                "-f", "lavfi", "-i", "sine=duration=4:sample_rate=44100",
                "-map", "0:v", "-map", "1:a", "-c:v", "mpeg4", "-vtag", "XVID", "-bf", "2", "-q:v", "5", "-c:a", "ac3", source], directory, CancellationToken.None);

            var remuxed = await actions.FfmpegPlanAsync([new("video-remux-media", Config(new { container = "mkv" }))], source, directory, "remux", Guid.NewGuid(), CancellationToken.None);

            Assert.NotNull(remuxed.Primary);
            Assert.Equal(".mkv", Path.GetExtension(remuxed.Primary));
            // Toutes les images, dans le même ordre d'affichage et aux mêmes instants que la source.
            async Task<string[]> FramesAsync(string path) =>
                (await tools.CheckedAsync("ffmpeg", ["-v", "error", "-i", path, "-map", "0:v", "-f", "framemd5", "-"], directory, CancellationToken.None))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !line.StartsWith('#'))
                .Select(line => line.Split(','))
                .Select(fields => $"{fields[2].Trim()} {fields[5].Trim()}") // PTS et empreinte de l'image décodée
                .ToArray();
            var expected = await FramesAsync(source);
            Assert.Equal(100, expected.Length);
            Assert.Equal(expected, await FramesAsync(remuxed.Primary!));
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
