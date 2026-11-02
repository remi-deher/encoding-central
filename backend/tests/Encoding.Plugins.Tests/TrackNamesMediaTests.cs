using System.Text.Json;
using Encoding.NodeAgent.Runtime;
using Encoding.NodeAgent.Runtime.Media;
using Encoding.NodeAgent.Services;
using static Encoding.Plugins.Tests.Support;

namespace Encoding.Plugins.Tests;

/// <summary>Noms de pistes sur un vrai MKV synthétique : mkvpropedit sur la copie de travail, source intacte (architecture 0056).</summary>
internal static class TrackNamesMediaTests
{
    private sealed class NoProbe : IToolCapabilityProbe
    {
        public Task<IReadOnlyList<Encoding.Contracts.Nodes.NodeToolCapability>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Encoding.Contracts.Nodes.NodeToolCapability>>([]);
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
        }
        finally { Directory.Delete(directory, true); }
    }
}
