using System.Text;
using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.NodeAgent.Runtime;
using Encoding.NodeAgent.Runtime.Media;
using Encoding.NodeAgent.Services;
using static Encoding.Plugins.Tests.Support;

namespace Encoding.Plugins.Tests;

/// <summary>Sous-titres et polices sur un vrai MKV synthétique : conversion SRT vers ASS, forcé déduit, doublon retiré, ASS personnalisé et source intacts (architecture 0057).</summary>
internal static class SubtitlesMediaTests
{
    private sealed class NoProbe : IToolCapabilityProbe
    {
        public Task<IReadOnlyList<Encoding.Contracts.Nodes.NodeToolCapability>> DiscoverAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Encoding.Contracts.Nodes.NodeToolCapability>>([]);
    }

    private static string Srt(IEnumerable<string> lines)
    {
        var builder = new StringBuilder();
        var index = 1;
        foreach (var line in lines)
        {
            var start = TimeSpan.FromSeconds((index - 1) * 4.0); var end = start + TimeSpan.FromSeconds(2);
            builder.Append($"{index}\n{start:hh\\:mm\\:ss}.000 --> {end:hh\\:mm\\:ss}.000\n{line}\n\n".Replace('.', ','));
            index++;
        }
        return builder.ToString();
    }

    public static async Task RunAsync()
    {
        var tools = new ProcessToolRunner();
        if (ProcessToolRunner.Find("ffmpeg") is null || ProcessToolRunner.Find("mkvmerge") is null || ProcessToolRunner.Find("mkvextract") is null)
        {
            Console.WriteLine("SKIP: subtitles media test requires ffmpeg and MKVToolNix.");
            return;
        }
        var directory = Path.Combine(Path.GetTempPath(), "encoding-subtitles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string P(string name) => Path.Combine(directory, name);
            var full = Enumerable.Range(0, 250).Select(i => $"Réplique numéro {i}").ToArray();
            await File.WriteAllTextAsync(P("fre-full.srt"), Srt(full));
            await File.WriteAllTextAsync(P("fre-short.srt"), Srt(full.Take(12)));
            await File.WriteAllTextAsync(P("eng.srt"), Srt(["Hello", "World", "Again"]));
            await File.WriteAllTextAsync(P("eng-copy.srt"), Srt(["Hello", "World", "Again"]));
            await File.WriteAllTextAsync(P("jpn.ass"), "[Script Info]\nScriptType: v4.00+\nPlayResX: 384\nPlayResY: 288\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Karaoke,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,0,2,10,10,10,1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Karaoke,,0,0,0,,{\\pos(100,100)}Panneau\n");
            await tools.CheckedAsync("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=duration=3:size=128x72:rate=5", "-f", "lavfi", "-i", "sine=duration=3:sample_rate=48000",
                "-map", "0:v", "-map", "1:a", "-c:v", "libx264", "-c:a", "aac", "-metadata:s:a:0", "language=jpn", P("base.mkv")], directory, CancellationToken.None);
            var source = P("source.mkv");
            await tools.CheckedAsync("mkvmerge", ["-o", source, P("base.mkv"),
                "--language", "0:fre", P("fre-full.srt"), "--language", "0:fre", P("fre-short.srt"), "--language", "0:eng", P("eng.srt"),
                "--language", "0:eng", P("eng-copy.srt"), "--language", "0:jpn", P("jpn.ass")], directory, CancellationToken.None);

            var scanner = new MediaScanner(tools);
            var actions = new MediaActions(tools, scanner, new NoProbe());
            var probe = await scanner.ScanAsync(source, CancellationToken.None);
            var before = File.ReadAllBytes(source);
            var outcome = await actions.SubtitleManageAsync(new Dictionary<string, JsonElement>(), probe, source, directory, "subs", CancellationToken.None);
            Check(outcome.Primary is not null && outcome.Primary != source, "A new MKV is written.");
            Check(File.ReadAllBytes(source).SequenceEqual(before), "The source file is not modified.");

            using var result = JsonDocument.Parse(await tools.CheckedAsync("mkvmerge", ["-J", outcome.Primary!], directory, CancellationToken.None));
            var tracks = result.RootElement.GetProperty("tracks").EnumerateArray().ToArray();
            var subtitles = tracks.Where(t => t.GetProperty("type").GetString() == "subtitles").ToArray();
            string Name(JsonElement t) => t.GetProperty("properties").TryGetProperty("track_name", out var n) ? n.GetString()! : "";
            bool Flag(JsonElement t, string key) => t.GetProperty("properties").TryGetProperty(key, out var v) && v.GetBoolean();
            Check(tracks.Count(t => t.GetProperty("type").GetString() == "video") == 1 && tracks.Count(t => t.GetProperty("type").GetString() == "audio") == 1, "Video and audio are untouched.");
            Check(subtitles.Length == 4, "The duplicate English track is removed (4 subtitle tracks remain).");
            Check(subtitles.Select(Name).SequenceEqual(["Français / ASS / Complet", "Français / ASS / Forcé", "Anglais / ASS / Piste 3", "Japonais / ASS / Piste 4"]), "Titles follow the template: " + string.Join(" | ", subtitles.Select(Name)));
            Check(!Flag(subtitles[0], "forced_track") && Flag(subtitles[1], "forced_track"), "The short French track is deduced forced.");
            Check(Flag(subtitles[0], "default_track") && !Flag(subtitles[1], "default_track") && !Flag(subtitles[2], "default_track"), "The full French track is the default (Japanese reference audio).");
            Check(subtitles[0].GetProperty("properties").GetProperty("language").GetString() == "fre" && subtitles[3].GetProperty("properties").GetProperty("language").GetString() == "jpn", "Languages are kept.");

            await tools.CheckedAsync("mkvextract", ["tracks", outcome.Primary!, subtitles[0].GetProperty("id").GetInt32() + ":" + P("out-fre.ass"), subtitles[3].GetProperty("id").GetInt32() + ":" + P("out-jpn.ass")], directory, CancellationToken.None);
            var converted = await File.ReadAllTextAsync(P("out-fre.ass"));
            Check(converted.Contains("SubtitleProfileVersion: 2", StringComparison.Ordinal) && converted.Contains("PlayResX: 128", StringComparison.Ordinal) && converted.Contains("Réplique numéro 249", StringComparison.Ordinal), "The converted track carries the proportional profile and every event.");
            Check((await File.ReadAllTextAsync(P("out-jpn.ass"))).Contains("{\\pos(100,100)}Panneau", StringComparison.Ordinal) && !(await File.ReadAllTextAsync(P("out-jpn.ass"))).Contains("SubtitleProfileVersion", StringComparison.Ordinal), "The custom ASS is copied as it was.");

            var second = await actions.SubtitleManageAsync(new Dictionary<string, JsonElement>(), await scanner.ScanAsync(outcome.Primary!, CancellationToken.None), outcome.Primary!, directory, "subs2", CancellationToken.None);
            Check(second.Primary is null, "A second pass finds nothing to change.");
            Console.WriteLine("PASS: subtitles on a synthetic MKV (SRT to profile ASS, forced deduced, duplicate removed, custom ASS and source intact, idempotent).");

            // Polices : jeu déclaré par le nœud, seulement celles qui manquent.
            var fontRoot = P("fonts"); Directory.CreateDirectory(Path.Combine(fontRoot, "Crunchyroll"));
            await File.WriteAllBytesAsync(Path.Combine(fontRoot, "Crunchyroll", "A.ttf"), new byte[2048]);
            await File.WriteAllBytesAsync(Path.Combine(fontRoot, "Crunchyroll", "B.ttf"), new byte[2048]);
            var previous = Environment.GetEnvironmentVariable("ENCODING_FONT_DIR");
            Environment.SetEnvironmentVariable("ENCODING_FONT_DIR", fontRoot);
            try
            {
                var config = new Dictionary<string, JsonElement> { ["fontSet"] = JsonSerializer.SerializeToElement("Crunchyroll"), ["fonts"] = JsonSerializer.SerializeToElement("A.ttf, B.ttf") };
                var withFonts = await actions.AddFontsAsync(config, outcome.Primary!, directory, "fonts", CancellationToken.None);
                Check(withFonts.Primary is not null, "Missing fonts are attached.");
                using var attached = JsonDocument.Parse(await tools.CheckedAsync("mkvmerge", ["-J", withFonts.Primary!], directory, CancellationToken.None));
                Check(attached.RootElement.GetProperty("attachments").EnumerateArray().Select(a => a.GetProperty("file_name").GetString()).Order().SequenceEqual(["A.ttf", "B.ttf"]), "Both fonts are attached.");
                Check((await actions.AddFontsAsync(config, withFonts.Primary!, directory, "fonts2", CancellationToken.None)).Primary is null, "Fonts already attached are not added twice.");
                var allFonts = await actions.AddFontsAsync(new Dictionary<string, JsonElement> { ["fontSet"] = JsonSerializer.SerializeToElement("Crunchyroll") }, outcome.Primary!, directory, "fonts3", CancellationToken.None);
                Check(allFonts.Primary is not null, "An empty list attaches every font of the set.");
                foreach (var bad in new[] { "../x", "Absent" })
                    try { await actions.AddFontsAsync(new Dictionary<string, JsonElement> { ["fontSet"] = JsonSerializer.SerializeToElement(bad) }, outcome.Primary!, directory, "fonts4", CancellationToken.None); Check(false, "A bad font set is refused: " + bad); }
                    catch (SubtitleException) { }
                try { await actions.AddFontsAsync(new Dictionary<string, JsonElement> { ["fontSet"] = JsonSerializer.SerializeToElement("Crunchyroll"), ["fonts"] = JsonSerializer.SerializeToElement("../A.ttf") }, outcome.Primary!, directory, "fonts5", CancellationToken.None); Check(false, "A font path is refused."); }
                catch (SubtitleException) { }
                // Ressource du catalogue : dossier déjà téléchargé par le runner, toutes ses polices sont ajoutées et le jeu local est ignoré.
                var resourceFolder = P("resource"); Directory.CreateDirectory(resourceFolder);
                await File.WriteAllBytesAsync(Path.Combine(resourceFolder, "C.ttf"), new byte[2048]);
                var fromResource = await actions.AddFontsAsync(new Dictionary<string, JsonElement> { ["fontSet"] = JsonSerializer.SerializeToElement("Absent") }, outcome.Primary!, directory, "fonts6", CancellationToken.None, resourceFolder);
                using var fromResourceInfo = JsonDocument.Parse(await tools.CheckedAsync("mkvmerge", ["-J", fromResource.Primary!], directory, CancellationToken.None));
                Check(fromResourceInfo.RootElement.GetProperty("attachments").EnumerateArray().Select(a => a.GetProperty("file_name").GetString()).SequenceEqual(["C.ttf"]), "A catalog resource attaches its own fonts.");

                // Reproduction Overgeared S01E01 et déduplication intelligente :
                var mkvWithDupes = P("mkv-with-dupes.mkv");
                var fontA = P("ArialBold.ttf");
                var fontB = P("TrebuchetMSBold.otf");
                var fontC = P("ArialBoldItalic.ttf");
                await File.WriteAllBytesAsync(fontA, FontMatcherTests.CreateSyntheticTtf("Arial", "Bold"));
                await File.WriteAllBytesAsync(fontB, FontMatcherTests.CreateSyntheticTtf("Trebuchet MS", "Bold"));
                await File.WriteAllBytesAsync(fontC, FontMatcherTests.CreateSyntheticTtf("Arial", "Bold Italic"));
                await tools.CheckedAsync("mkvmerge", ["-o", mkvWithDupes, outcome.Primary!,
                    "--attach-file", fontA, "--attach-file", fontB, "--attach-file", fontC], directory, CancellationToken.None);

                var crunchyrollResource = P("crunchyroll-resource"); Directory.CreateDirectory(crunchyrollResource);
                await File.WriteAllBytesAsync(Path.Combine(crunchyrollResource, "Arial Bold.ttf"), FontMatcherTests.CreateSyntheticTtf("Arial", "Bold"));
                await File.WriteAllBytesAsync(Path.Combine(crunchyrollResource, "Trebuchet MS Bold.ttf"), FontMatcherTests.CreateSyntheticTtf("Trebuchet MS", "Bold"));
                await File.WriteAllBytesAsync(Path.Combine(crunchyrollResource, "Times New Roman Bold.ttf"), FontMatcherTests.CreateSyntheticTtf("Times New Roman", "Bold"));

                var dedupResult = await actions.AddFontsAsync(new Dictionary<string, JsonElement>(), mkvWithDupes, directory, "fonts-dedup", CancellationToken.None, crunchyrollResource);
                Check(dedupResult.Primary is not null, "Adding fonts produces an updated MKV.");
                using var dedupInfo = JsonDocument.Parse(await tools.CheckedAsync("mkvmerge", ["-J", dedupResult.Primary!], directory, CancellationToken.None));
                var attachedNames = dedupInfo.RootElement.GetProperty("attachments").EnumerateArray().Select(a => a.GetProperty("file_name").GetString()!).Order().ToArray();

                Check(!attachedNames.Contains("Arial Bold.ttf"), "Arial Bold.ttf was deduplicated and NOT attached.");
                Check(!attachedNames.Contains("Trebuchet MS Bold.ttf"), "Trebuchet MS Bold.ttf was deduplicated and NOT attached.");
                Check(attachedNames.Contains("Times New Roman Bold.ttf"), "Times New Roman Bold.ttf had no equivalent and WAS attached.");
                Check(attachedNames.Contains("ArialBoldItalic.ttf"), "ArialBoldItalic.ttf is kept intact.");
                Check(attachedNames.Length == 4, $"Exactement 4 polices attachées (3 existantes + 1 nouvelle), trouvé : {attachedNames.Length} ({string.Join(", ", attachedNames)}).");

                Check(dedupResult.Value is { ValueKind: JsonValueKind.Array }, "Notes array is returned in outcome.Value.");
                var notes = dedupResult.Value!.Value.EnumerateArray().Select(n => n.GetString()!).ToArray();
                Check(notes.Any(n => n.Contains("Police « Arial Bold.ttf » ignorée : déjà présente (« ArialBold.ttf »)", StringComparison.Ordinal)), "Log mentions Arial Bold.ttf ignored with ArialBold.ttf.");
                Check(notes.Any(n => n.Contains("Police « Trebuchet MS Bold.ttf » ignorée : déjà présente (« TrebuchetMSBold.otf »)", StringComparison.Ordinal)), "Log mentions Trebuchet MS Bold.ttf ignored with TrebuchetMSBold.otf.");
                Check(notes.Any(n => n.Contains("Police « Times New Roman Bold.ttf » ajoutée", StringComparison.Ordinal)), "Log mentions Times New Roman Bold.ttf added with size.");

                var secondDedup = await actions.AddFontsAsync(new Dictionary<string, JsonElement>(), dedupResult.Primary!, directory, "fonts-dedup-second", CancellationToken.None, crunchyrollResource);
                Check(secondDedup.Primary is null, "Second pass adds nothing when all fonts are already present.");
                var secondNotes = secondDedup.Value!.Value.EnumerateArray().Select(n => n.GetString()!).ToArray();
                Check(secondNotes.Length == 3 && secondNotes.All(n => n.Contains("ignorée", StringComparison.Ordinal)), "Second pass reports all 3 fonts as ignored in notes.");
                Console.WriteLine("PASS: fonts attached from the node font set (only missing ones, no free path) and from a catalog resource.");
            }
            finally { Environment.SetEnvironmentVariable("ENCODING_FONT_DIR", previous); }
        }
        finally { Directory.Delete(directory, true); }
    }
}
