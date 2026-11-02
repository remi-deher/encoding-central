using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.NodeAgent.Runtime;
using Encoding.NodeAgent.Runtime.Media;
using Encoding.NodeAgent.Services;
using static Encoding.Plugins.Tests.Support;

namespace Encoding.Plugins.Tests;

/// <summary>Règles audio sur un vrai fichier synthétique : conversions FFmpeg, LFE conservé, pistes conformes copiées (architecture 0055).</summary>
internal static class AudioRulesMediaTests
{
    private sealed class NoProbe : IToolCapabilityProbe
    {
        public Task<IReadOnlyList<Encoding.Contracts.Nodes.NodeToolCapability>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Encoding.Contracts.Nodes.NodeToolCapability>>([]);
    }

    public static async Task RunAsync()
    {
        var tools = new ProcessToolRunner();
        if (ProcessToolRunner.Find("ffmpeg") is null || ProcessToolRunner.Find("ffprobe") is null)
        {
            Console.WriteLine("SKIP: audio rules media test requires ffmpeg and ffprobe.");
            return;
        }
        var directory = Path.Combine(Path.GetTempPath(), "encoding-audio-rules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // 0 : 7.1 FLAC (à ramener à 5.1), 1 : stéréo AAC (déjà conforme), 2 : 5.1 E-AC-3 (à passer en AC-3).
            var source = Path.Combine(directory, "source.mkv");
            await tools.CheckedAsync("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=duration=2:size=64x64:rate=5",
                "-f", "lavfi", "-i", "sine=duration=2:sample_rate=48000", "-f", "lavfi", "-i", "sine=duration=2:sample_rate=48000", "-f", "lavfi", "-i", "sine=duration=2:sample_rate=48000",
                "-filter_complex", "[1:a]pan=7.1|FL=c0|FR=c0|FC=c0|LFE=c0|BL=c0|BR=c0|SL=c0|SR=c0[a0];[2:a]pan=stereo|FL=c0|FR=c0[a1];[3:a]pan=5.1|FL=c0|FR=c0|FC=c0|LFE=c0|BL=c0|BR=c0[a2]",
                "-map", "0:v", "-map", "[a0]", "-map", "[a1]", "-map", "[a2]", "-c:v", "libx264", "-c:a:0", "flac", "-c:a:1", "aac", "-c:a:2", "eac3", "-b:a:2", "448k", source], directory, CancellationToken.None);

            var scanner = new MediaScanner(tools);
            var metadata = await scanner.ScanAsync(source, CancellationToken.None);
            var options = new AudioRuleOptions();
            var plan = AudioRulePlanner.Plan(metadata, options);
            Check(plan.Conversions.Select(item => item.AudioIndex).SequenceEqual([0, 2]), "The scan of a real file selects the 7.1 and the E-AC-3 tracks.");

            var actions = new MediaActions(tools, scanner, new NoProbe());
            var outcome = await actions.FfmpegPlanAsync(plan.Steps("rules", options), source, directory, "rules", Guid.NewGuid(), CancellationToken.None);
            var result = await scanner.ScanAsync(outcome.Primary!, CancellationToken.None);
            var audio = result.GetProperty("streams").EnumerateArray().Where(item => item.GetProperty("codec_type").GetString() == "audio").ToArray();
            Check(audio.Length == 3, "No audio track is lost.");
            Check(audio[0].GetProperty("codec_name").GetString() == "ac3" && audio[0].GetProperty("channels").GetInt32() == 6 && audio[0].GetProperty("channel_layout").GetString()!.StartsWith("5.1", StringComparison.Ordinal),
                "7.1 becomes 5.1 AC-3 with its LFE.");
            Check(audio[1].GetProperty("codec_name").GetString() == "aac" && audio[1].GetProperty("channels").GetInt32() == 2, "The conform stereo track is copied.");
            Check(audio[2].GetProperty("codec_name").GetString() == "ac3" && audio[2].GetProperty("channels").GetInt32() == 6, "E-AC-3 becomes AC-3.");
            Check(result.GetProperty("streams").EnumerateArray().Count(item => item.GetProperty("codec_type").GetString() == "video") == 1
                && result.GetProperty("streams").EnumerateArray().First(item => item.GetProperty("codec_type").GetString() == "video").GetProperty("codec_name").GetString() == "h264", "The video is copied untouched.");
            Check(File.Exists(source) && (await scanner.ScanAsync(source, CancellationToken.None)).GetProperty("streams").EnumerateArray().Count() == 4, "The source file is not modified.");
            Check(AudioRulePlanner.Plan(result, options).Conversions.Count == 0, "The converted file now respects every rule.");
            Console.WriteLine("PASS: audio rules on a synthetic media file (7.1 to 5.1, E-AC-3 to AC-3, conform tracks copied).");
        }
        finally { Directory.Delete(directory, true); }
    }
}
