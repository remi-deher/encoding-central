using System.Text.Json;
using Encoding.WorkerAgent.Runtime;
using Encoding.WorkerAgent.Runtime.Media;
using Encoding.WorkerAgent.Services;
using static Encoding.Plugins.Tests.Support;

namespace Encoding.Plugins.Tests;

/// <summary>Noms de pistes sur un vrai MKV synthétique : mkvpropedit sur la copie de travail, source intacte (architecture 0056).</summary>
internal static class TrackNamesMediaTests
{
    private sealed class NoProbe : IToolCapabilityProbe
    {
        public Task<IReadOnlyList<Encoding.Contracts.Workers.WorkerToolCapability>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Encoding.Contracts.Workers.WorkerToolCapability>>([]);
    }

    public static async Task RunAsync()
    {
        var tools = new ProcessToolRunner();
        if (ProcessToolRunner.Find("ffmpeg") is null || ProcessToolRunner.Find("mkvmerge") is null || ProcessToolRunner.Find("mkvpropedit") is null)
        {
            Console.WriteLine("SKIP: track names media test requires ffmpeg and MKVToolNix.");
            return;
        }
        var directory = Path.Combine(Path.GetTempPath(), "encoding-track-names-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.mkv");
            await tools.CheckedAsync("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=duration=1:size=64x64:rate=5",
                "-f", "lavfi", "-i", "sine=duration=1:sample_rate=48000", "-f", "lavfi", "-i", "sine=duration=1:sample_rate=48000",
                "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "libx264", "-c:a:0", "aac", "-c:a:1", "ac3",
                "-metadata:s:a:0", "language=jpn", "-metadata:s:a:1", "language=fre", source], directory, CancellationToken.None);
            var scanner = new MediaScanner(tools);
            var probe = await scanner.ScanAsync(source, CancellationToken.None);
            var actions = new MediaActions(tools, scanner, new NoProbe());
            var before = File.ReadAllBytes(source);

            var outcome = await actions.TrackNamesAsync(new Dictionary<string, JsonElement>(), probe, null, source, directory, "names", CancellationToken.None);
            Check(outcome.Primary is not null && outcome.Primary != source, "A working copy is produced.");
            Check(File.ReadAllBytes(source).SequenceEqual(before), "The source file is not modified.");
            using var identification = JsonDocument.Parse(await tools.CheckedAsync("mkvmerge", ["-J", outcome.Primary!], directory, CancellationToken.None));
            var names = identification.RootElement.GetProperty("tracks").EnumerateArray()
                .Where(item => item.GetProperty("type").GetString() == "audio")
                .Select(item => item.GetProperty("properties").TryGetProperty("track_name", out var name) ? name.GetString() : null).ToArray();
            Check(names.Length == 2 && names.All(name => !string.IsNullOrWhiteSpace(name)), "Each audio track receives a name.");
            Check(names[0]!.Contains("Japonais", StringComparison.Ordinal) && names[1]!.Contains("Français", StringComparison.Ordinal), "Names follow the declared languages.");

            var snapshot = await actions.TrackNamesAsync(new Dictionary<string, JsonElement> { ["mode"] = JsonSerializer.SerializeToElement("snapshot") }, probe, null, source, directory, "snap", CancellationToken.None);
            Check(snapshot.Primary is null && snapshot.Value is not null, "Snapshot mode only returns the original tracks.");
            Console.WriteLine("PASS: track names on a synthetic MKV (names written on the copy, source untouched, snapshot mode).");

            // #28 : en MP4 (FFmpeg -map 0 -c copy), nommer les pistes garde les dispositions de la source, piste par piste.
            var mp4 = Path.Combine(directory, "source.mp4");
            await tools.CheckedAsync("ffmpeg", ["-v", "error", "-y", "-i", source, "-map", "0:v", "-map", "0:a", "-c", "copy",
                "-disposition:v:0", "default", "-disposition:a:0", "0", "-disposition:a:1", "default", mp4], directory, CancellationToken.None);
            static string[] AudioDefaults(JsonElement scan) => scan.GetProperty("streams").EnumerateArray()
                .Where(stream => stream.GetProperty("codec_type").GetString() == "audio")
                .Select(stream => stream.GetProperty("disposition").GetProperty("default").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var mp4Probe = await scanner.ScanAsync(mp4, CancellationToken.None);
            Check(AudioDefaults(mp4Probe).SequenceEqual(["0", "1"]), "The MP4 fixture marks only the second audio track as default.");
            var named = await actions.TrackNamesAsync(new Dictionary<string, JsonElement>(), mp4Probe, null, mp4, directory, "names-mp4", CancellationToken.None);
            Check(named.Primary is not null && named.Primary != mp4, "An MP4 working copy is produced.");
            Check(AudioDefaults(await scanner.ScanAsync(named.Primary!, CancellationToken.None)).SequenceEqual(["0", "1"]),
                "Naming MP4 tracks keeps the source default flags (the second audio stays default, the first is not promoted).");
            Console.WriteLine("PASS: MP4 track names keep the source dispositions.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
