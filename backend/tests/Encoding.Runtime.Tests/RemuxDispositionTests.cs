using System.Text.Json;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Workers;
using Encoding.WorkerAgent.Runtime;
using Encoding.WorkerAgent.Runtime.Media;
using Encoding.WorkerAgent.Services;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>
/// #24 (banc NAS #18) : un remux FFmpeg doit conserver à l'identique les dispositions des pistes (default, forced…). Sans option
/// <c>-disposition</c>, la CLI FFmpeg marquait d'office « default » la première piste de chaque type présent en plusieurs exemplaires.
/// </summary>
public sealed class RemuxDispositionTests
{
    private static Dictionary<string, JsonElement> Config(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    private static JsonElement Stream(string type, object? disposition) =>
        JsonSerializer.SerializeToElement(new { codec_type = type, disposition });

    [Fact]
    public void DispositionValueCopiesEveryActiveFlagAndOnlyThose()
    {
        Assert.Equal("0", MediaActions.DispositionValue(Stream("audio", new { @default = 0, forced = 0, hearing_impaired = 0 })));
        Assert.Equal("default", MediaActions.DispositionValue(Stream("audio", new { @default = 1, forced = 0 })));
        Assert.Equal("forced+hearing_impaired", MediaActions.DispositionValue(Stream("subtitle", new { @default = 0, forced = 1, hearing_impaired = 1 })));
        Assert.Equal("attached_pic", MediaActions.DispositionValue(Stream("video", new { attached_pic = 1 })));
        Assert.Equal("0", MediaActions.DispositionValue(Stream("audio", null)));                         // pas d'objet disposition
        Assert.Equal("0", MediaActions.DispositionValue(Stream("audio", new { Default = 1, x_y = "1" }))); // nom non conforme, valeur non numérique
    }

    [Fact]
    public void EveryOutputStreamGetsItsSourceDispositionInOutputOrder()
    {
        // Cas n1 : toutes les pistes à default=0, un sous-titre forcé. La sortie doit porter explicitement « 0 » partout sauf « forced ».
        var sources = new[]
        {
            Stream("video", new { @default = 0 }),
            Stream("audio", new { @default = 0 }), Stream("audio", new { @default = 0 }), Stream("audio", new { @default = 0 }),
            Stream("subtitle", new { @default = 0, forced = 0 }), Stream("subtitle", new { @default = 0, forced = 1 }),
        };
        Assert.Equal(
            ["-disposition:0", "0", "-disposition:1", "0", "-disposition:2", "0", "-disposition:3", "0", "-disposition:4", "0", "-disposition:5", "forced"],
            MediaActions.PreservedDispositionArgs(sources));
        Assert.Empty(MediaActions.PreservedDispositionArgs([]));
    }

    [Fact]
    public void PlannedOutputSourcesFollowTheStreamMapping()
    {
        var metadata = JsonSerializer.SerializeToElement(new
        {
            streams = new object[]
            {
                new { index = 0, codec_type = "video", disposition = new { @default = 0 } },
                new { index = 1, codec_type = "audio", disposition = new { @default = 0 } },
                new { index = 2, codec_type = "audio", disposition = new { @default = 1 } },
                new { index = 3, codec_type = "subtitle", disposition = new { @default = 0, forced = 1 } },
            }
        });
        int[] Indexes(IReadOnlyList<JsonElement>? sources) => sources!.Select(stream => stream.GetProperty("index").GetInt32()).ToArray();

        // Remux : -map 0, toutes les pistes dans l'ordre source.
        Assert.Equal([0, 1, 2, 3], Indexes(MediaActions.PlannedOutputSources([new("video-remux-media", Config(new { container = "mkv" }))], metadata, preserve: true)));
        // Transcodage sans conservation : vidéo puis la piste audio traitée (ici la 2e).
        Assert.Equal([0, 2], Indexes(MediaActions.PlannedOutputSources(
            [new("video-transcode", Config(new { videoCodec = "hevc" })), new("audio-transcode", Config(new { trackId = 1, audioCodec = "aac" }))], metadata, preserve: false)));
        // Piste audio inexistante : pas de dispositions ajoutées, FFmpeg échouera de lui-même sur -map.
        Assert.Null(MediaActions.PlannedOutputSources([new("audio-transcode", Config(new { trackId = 5, audioCodec = "aac" }))], metadata, preserve: false));
        Assert.Null(MediaActions.PlannedOutputSources([new("video-remux-media", Config(new { container = "mkv" }))], JsonSerializer.SerializeToElement(new { }), preserve: true));
    }

    [MediaToolsFact]
    public async Task MatroskaRemuxKeepsDefaultAndForcedExactlyAsInTheSource()
    {
        var directory = Path.Combine(Path.GetTempPath(), "encoding-dispositions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var tools = new ProcessToolRunner();
            var scanner = new MediaScanner(tools);
            var actions = new MediaActions(tools, scanner, new EmptyCapabilities());
            var french = Path.Combine(directory, "fr.srt");
            var forced = Path.Combine(directory, "forced.srt");
            await File.WriteAllTextAsync(french, "1\n00:00:00,000 --> 00:00:01,000\nBonjour\n");
            await File.WriteAllTextAsync(forced, "1\n00:00:00,000 --> 00:00:01,000\nPanneau\n");

            async Task<string> SourceAsync(string name, string audio0, string audio1, string subtitle0, string subtitle1)
            {
                var path = Path.Combine(directory, name);
                // Dispositions posées explicitement à la création : aucune inférence de FFmpeg dans le fichier source.
                await tools.CheckedAsync("ffmpeg", ["-v", "error", "-y",
                    "-f", "lavfi", "-i", "testsrc=duration=1:size=64x36:rate=8",
                    "-f", "lavfi", "-i", "sine=duration=1:frequency=440:sample_rate=48000",
                    "-f", "lavfi", "-i", "sine=duration=1:frequency=880:sample_rate=48000",
                    "-i", french, "-i", forced,
                    "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:s", "-map", "4:s",
                    "-c:v", "libx264", "-c:a", "aac", "-c:s", "srt",
                    "-metadata:s:a:0", "language=fre", "-metadata:s:a:0", "title=VFQ",
                    "-metadata:s:a:1", "language=fre", "-metadata:s:a:1", "title=VFF",
                    "-metadata:s:s:0", "language=fre", "-metadata:s:s:0", "title=Français complet",
                    "-metadata:s:s:1", "language=fre", "-metadata:s:s:1", "title=Forced",
                    "-disposition:v:0", "0", "-disposition:a:0", audio0, "-disposition:a:1", audio1,
                    "-disposition:s:0", subtitle0, "-disposition:s:1", subtitle1, path], directory, CancellationToken.None);
                return path;
            }

            static string[] Flags(JsonElement scan) => scan.GetProperty("streams").EnumerateArray()
                .Select(stream => $"{stream.GetProperty("codec_type").GetString()} default={Flag(stream, "default")} forced={Flag(stream, "forced")}").ToArray();
            static int Flag(JsonElement stream, string name) =>
                stream.TryGetProperty("disposition", out var disposition) && disposition.TryGetProperty(name, out var value) ? value.GetInt32() : 0;

            // n1 / s3b / s4 : aucune piste « default », un sous-titre forcé ; puis une source qui a un « default » ailleurs qu'en première position.
            foreach (var (name, a0, a1, s0, s1) in new[] { ("none-default.mkv", "0", "0", "0", "forced"), ("second-default.mkv", "0", "default", "0", "default+forced") })
            {
                var source = await SourceAsync(name, a0, a1, s0, s1);
                var before = Flags(await scanner.ScanAsync(source, CancellationToken.None));
                var remuxed = await actions.FfmpegPlanAsync([new("video-remux-media", Config(new { container = "mkv" }))], source, directory, "remux-" + Path.GetFileNameWithoutExtension(name), Guid.NewGuid(), CancellationToken.None);
                Assert.NotNull(remuxed.Primary);
                var after = Flags(await scanner.ScanAsync(remuxed.Primary!, CancellationToken.None));
                Assert.Equal(before, after);
            }
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
