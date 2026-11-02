using System.Text;
using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Nodes;

namespace Encoding.Runtime.Tests;

/// <summary>Sous-titres et polices (architecture 0057) : profil ASS proportionnel, conversion, mise à niveau, doublons, forcés, titres, piste par défaut.</summary>
internal static class SubtitleScenarios
{
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private const string Foreign = "[Script Info]\nScriptType: v4.00+\nPlayResX: 384\nPlayResY: 288\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize\nStyle: Default,Arial,16\n\n";

    /// <summary>ASS tel que FFmpeg le produit depuis un SRT : un seul style « Default », mêmes événements.</summary>
    private static string Ass(IEnumerable<string> lines, string head = Foreign, IEnumerable<(double Start, double End)>? times = null)
    {
        var builder = new StringBuilder(head).Append("[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
        var slots = times?.ToArray();
        var index = 0;
        foreach (var line in lines)
        {
            var start = slots is null ? index * 4.0 : slots[index].Start; var end = slots is null ? index * 4.0 + 2 : slots[index].End;
            builder.Append($"Dialogue: 0,{Stamp(start)},{Stamp(end)},Default,,0,0,0,,{line}\n"); index++;
        }
        return builder.ToString();
    }
    private static string Stamp(double seconds) => $"{(int)(seconds / 3600)}:{(int)(seconds % 3600 / 60):00}:{seconds % 60:00.00}".Replace(',', '.');
    private static IEnumerable<string> Lines(string prefix, int count) => Enumerable.Range(0, count).Select(i => $"{prefix} réplique numéro {i}");

    public static void Run(FlowComponentRegistry registry, FlowDefinitionValidator validator, RuntimeEligibility eligibility)
    {
        var canvas = AssProfile.Canvas(J(new { width = 1920, height = 1080, display_aspect_ratio = "16:9" }));
        Check(canvas == (1920, 1080), "A 16:9 video gives a 1920x1080 canvas.");
        Check(AssProfile.Canvas(J(new { width = 720, height = 576, sample_aspect_ratio = "64:45" })).Width == 1024, "The anamorphic ratio is used when the display ratio is absent.");
        Check(AssProfile.Canvas(J(new { width = 1920, height = 1080, display_aspect_ratio = "16:9", side_data_list = new[] { new { rotation = -90 } } })) == (1080, 1920), "A rotated video swaps the canvas.");
        try { AssProfile.Canvas(J(new { width = 0, height = 0 })); Check(false, "Invalid dimensions must be refused."); } catch (SubtitleException) { }

        var styles = AssProfile.Styles(1920, 1080);
        Check(styles[0] == "Style: Default,Trebuchet MS,69,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,-1,0,0,0,100,100,0,0,1,7.5,0,2,60,60,60,1", "The default style is scaled by height / 360.");
        var header = AssProfile.Header(1920, 1080);
        Check(AssProfile.HasCurrent(header + "[Events]\n", 1920, 1080) && !AssProfile.HasCurrent(header + "[Events]\n", 1280, 720), "The profile check depends on the canvas.");
        var upgrade = AssProfile.Upgrade(Foreign + "[Events]\nFormat: x\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Texte\n", 1920, 1080);
        Check(upgrade.Changed && !upgrade.Complex && AssProfile.HasCurrent(upgrade.Text, 1920, 1080) && upgrade.Text.Contains("Dialogue: 0,0:00:01.00", StringComparison.Ordinal), "A simple ASS is upgraded and keeps its events.");
        Check(!AssProfile.Upgrade(upgrade.Text, 1920, 1080).Changed, "Upgrading twice changes nothing.");
        Check(AssProfile.IsCustom(Foreign.Replace("Style: Default", "Style: Karaoke")) && AssProfile.IsCustom("[Events]\nDialogue: 0,0,0,Default,,0,0,0,,{\\pos(10,10)}x") && !AssProfile.IsCustom(Foreign), "Extra styles and placement tags mark a custom ASS.");
        Check(AssProfile.Upgrade("[Script Info]\nPlayResX: 1\n[Events]\n", 1920, 1080).Complex, "An ASS without styles section is left intact.");

        var options = new SubtitleOptions();
        SubtitleTrack Text(int ordinal, string codec, string language, string title, string ass, bool forced = false, bool def = false) =>
            new(ordinal, codec, language, title, def, forced, false, ass);

        // SRT converti, ASS déjà au profil, ASS personnalisé et piste bitmap.
        var current = AssProfile.Header(1920, 1080) + "[Events]\nFormat: x\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Bonjour\n";
        var custom = Ass(["{\\pos(100,100)}Panneau"], Foreign.Replace("Style: Default", "Style: Karaoke"));
        var plan = SubtitlePlanner.Plan([
            Text(0, "subrip", "eng", "", Ass(Lines("Hello", 5))), Text(1, "ass", "fre", "", current), Text(2, "ass", "jpn", "", custom),
            new(3, "hdmv_pgs_subtitle", "ger", "", false, false, false, null)], null, canvas, options);
        Check(plan.Changed && plan.Decisions[0].ReplacementAss is { } converted && AssProfile.HasCurrent(converted, 1920, 1080) && converted.Contains("Hello réplique numéro 4", StringComparison.Ordinal), "An SRT is converted to the profile with its events.");
        Check(plan.Decisions[1].ReplacementAss is null && plan.Decisions[2].ReplacementAss is null && plan.Decisions[3].ReplacementAss is null, "A conform ASS, a custom ASS and a bitmap track are copied from the source.");
        Check(plan.Decisions[0].Title == "Anglais / ASS / Piste 1" && plan.Decisions[1].Title == "Français / ASS / Piste 2" && plan.Decisions[3].Title == "Allemand / PGS / Piste 4", "Titles follow « Langue / Format / Piste N ».");
        var untouched = SubtitlePlanner.Plan([Text(0, "ass", "fre", "Français / ASS / Piste 1", current)], null, canvas, options);
        Check(!untouched.Changed, "A conform file needs no write.");
        var noProfile = SubtitlePlanner.Plan([Text(0, "subrip", "eng", "", Ass(Lines("Hello", 5)))], null, canvas, options with { ApplyProfile = false });
        Check(noProfile.Decisions[0].ReplacementAss is null && noProfile.Decisions[0].Title == "Anglais / SRT / Piste 1", "Without a profile the SRT stays and is named SRT.");

        // « frf » est du français (FileFlows le réécrit en fr-FR) ; le drapeau malentendant suit le titre et survit à la réécriture.
        var frf = SubtitlePlanner.Plan([Text(0, "subrip", "frf", "", Ass(Lines("Salut", 5))), Text(1, "subrip", "eng", "SDH", Ass(Lines("Hello", 5))), Text(2, "ass", "eng", "", current)], null, canvas, options);
        Check(frf.Decisions[0].Title == "Français / ASS / Piste 1" && frf.Decisions[0].Language == "fr-FR", "A frf track is French and normalised to fr-FR.");
        Check(frf.Decisions[1].HearingImpaired && frf.Decisions[1].ReplacementAss is not null && !frf.Decisions[0].HearingImpaired, "A converted SDH track keeps the hearing-impaired flag.");
        Check(SubtitlePlanner.Plan([Text(0, "ass", "eng", "", current), Text(1, "ass", "fre", "", current)], false, canvas, options).Decisions[1].Default
            && !SubtitlePlanner.Plan([Text(0, "ass", "eng", "", current), Text(1, "ass", "fre", "", current)], SubtitlePlanner.IsFrench("frf"), canvas, options).Decisions[1].Default, "A frf reference audio counts as French.");

        // Doublons : texte et horaires concordants, même langue et mêmes marqueurs ; jamais d'ASS personnalisé.
        var big = Ass(Lines("Même", 40));
        var twins = SubtitlePlanner.Plan([Text(0, "subrip", "fre", "", big), Text(1, "ass", "fre", "", Ass(Lines("Même", 40)))], null, canvas, options);
        Check(twins.Decisions.Count(item => item.Drop) == 1, "An identical track of the same language is removed.");
        var different = SubtitlePlanner.Plan([Text(0, "subrip", "fre", "", big), Text(1, "subrip", "fre", "", Ass(Lines("Autre", 40)))], null, canvas, options);
        Check(!different.Decisions.Any(item => item.Drop), "Distinct translations are never collapsed.");
        var customTwins = SubtitlePlanner.Plan([Text(0, "ass", "fre", "", custom), Text(1, "ass", "fre", "", custom)], null, canvas, options);
        Check(!customTwins.Decisions.Any(item => item.Drop), "Independently positioned ASS are kept.");
        var forcedTwins = SubtitlePlanner.Plan([Text(0, "subrip", "fre", "", big), Text(1, "subrip", "fre", "Forced", Ass(Lines("Même", 40)), forced: true)], null, canvas, options);
        Check(!forcedTwins.Decisions.Any(item => item.Drop), "A forced track is never a duplicate of a regular one.");
        Check(!SubtitlePlanner.Plan([Text(0, "subrip", "fre", "", big), Text(1, "subrip", "fre", "", Ass(Lines("Même", 40)))], null, canvas, options with { RemoveDuplicates = false }).Decisions.Any(item => item.Drop), "The option disables the removal.");

        // Forcé déduit : piste courte (≤ 10 %) dont le texte est à 90 % dans la plus complète.
        var full = Lines("Dialogue", 250).ToArray();
        var inferred = SubtitlePlanner.Plan([Text(0, "subrip", "fre", "", Ass(full)), Text(1, "subrip", "fre", "", Ass(full.Take(12), times: Enumerable.Range(0, 12).Select(i => (i * 40.0 + 1, i * 40.0 + 3))))], null, canvas, options);
        Check(inferred.Decisions[1].Forced && inferred.Decisions[1].Title == "Français / ASS / Forcé" && !inferred.Decisions[0].Forced && inferred.Decisions[0].Title == "Français / ASS / Complet", "A short track contained in the full one is deduced forced.");
        var foreignShort = SubtitlePlanner.Plan([Text(0, "subrip", "fre", "", Ass(full)), Text(1, "subrip", "fre", "", Ass(Lines("Autre", 12)))], null, canvas, options);
        Check(!foreignShort.Decisions[1].Forced, "A short track with different text stays as it is.");
        Check(!SubtitlePlanner.Plan([Text(0, "subrip", "fre", "", Ass(full)), Text(1, "subrip", "fre", "", Ass(full.Take(12)))], null, canvas, options with { InferForced = false }).Decisions[1].Forced, "The option disables the deduction.");
        var marked = SubtitlePlanner.Plan([Text(0, "subrip", "eng", "SDH", Ass(Lines("A", 3))), Text(1, "subrip", "eng", "Signs & Songs", Ass(Lines("B", 3))), Text(2, "subrip", "eng", "Forced", Ass(Lines("C", 3)))], null, canvas, options);
        Check(marked.Decisions[0].Title.EndsWith("Malentendant", StringComparison.Ordinal) && marked.Decisions[1].Title.EndsWith("Signs & Songs", StringComparison.Ordinal) && marked.Decisions[2].Forced, "Markers come from the original titles and flags.");

        // Sous-titre français par défaut quand l'audio de référence ne l'est pas.
        var tracks = new[] { Text(0, "subrip", "eng", "", Ass(Lines("A", 3)), def: true), Text(1, "subrip", "fre", "", Ass(Lines("B", 3))), Text(2, "subrip", "fre", "Forced", Ass(Lines("C", 3)), forced: true) };
        var french = SubtitlePlanner.Plan(tracks, false, canvas, options);
        Check(!french.Decisions[0].Default && french.Decisions[1].Default && !french.Decisions[2].Default, "A French subtitle becomes the default when the reference audio is not French.");
        Check(SubtitlePlanner.Plan(tracks, true, canvas, options).Decisions[0].Default && SubtitlePlanner.Plan(tracks, null, canvas, options).Decisions[0].Default, "The default is left alone when the reference audio is French or unknown.");
        var audio = J(new { streams = new object[] { new { codec_type = "audio", disposition = new { @default = 1 }, tags = new { language = "jpn" } }, new { codec_type = "audio", disposition = new { @default = 0 }, tags = new { language = "fre" } } } });
        Check(SubtitlePlanner.ReferenceAudioIsFrench(audio) == false, "The reference audio is the single default track.");
        Check(SubtitlePlanner.ReferenceAudioIsFrench(J(new { streams = new object[] { new { codec_type = "audio", disposition = new { @default = 0 }, tags = new { language = "fre" } }, new { codec_type = "audio", disposition = new { @default = 0 } } } })) is null, "Without an explicit default the reference is unknown.");

        // Pistes vides.
        var emptyAss = SubtitlePlanner.Plan([Text(0, "ass", "fre", "", Ass([])), Text(1, "subrip", "eng", "", Ass(Lines("A", 3)))], null, canvas, options);
        Check(emptyAss.Decisions[0].Drop && !emptyAss.Decisions[1].Drop, "An empty ASS track is removed.");
        try { SubtitlePlanner.Plan([Text(0, "webvtt", "eng", "", Ass([]))], null, canvas, options); Check(false, "An unconfirmed empty track must stop the block."); } catch (SubtitleException) { }
        Check(SubtitlePlanner.Plan([new(0, "subrip", "eng", "", false, false, false, Ass([]), true)], null, canvas, options).Decisions[0].Drop, "A confirmed empty SRT is removed.");
        Check(SubtitlePlanner.SrtHasNoVisibleText("1\n00:00:01,000 --> 00:00:02,000\n<i> </i>\n\n2\n00:00:03,000 --> 00:00:04,000\n&nbsp;\n") && !SubtitlePlanner.SrtHasNoVisibleText("1\n00:00:01,000 --> 00:00:02,000\nBonjour\n"), "Visible text is detected in raw SRT.");

        // Catalogue et éligibilité.
        var subsManifest = registry.Plugins.Single(item => item.Id == "subtitles");
        var manage = subsManifest.Components.Single(item => item.Id == "subtitles-manage");
        var fonts = subsManifest.Components.Single(item => item.Id == "subtitles-add-fonts");
        var baseManifest = registry.Plugins.Single(item => item.Id == "base");
        var input = baseManifest.Components.Single(item => item.Id == "base-input-file");
        Check(subsManifest.Version == "1.3.0" && manage.Version == "1.0.0" && fonts.Version == "1.0.0"
            && manage.Outputs.Select(item => item.Id).SequenceEqual(["success", "unchanged", "error"]) && fonts.Outputs.Select(item => item.Id).SequenceEqual(["success", "unchanged", "error"]), "Both blocks are declared in Subtitles 1.3.0 with three outputs.");
        FlowDefinition Flow(params object[] _) => new(1, Guid.NewGuid(), 1, "Subtitles", "video", [
            new("input", null, new("base", "1.4.0", input.Id, input.Version, 1), new Dictionary<string, JsonElement>()),
            new("subs", null, new("base", "1.4.0", "base-subtitle-manage", "1.4.0", 1), new Dictionary<string, JsonElement>()),
            new("fonts", null, new("base", "1.4.0", "base-add-fonts", "1.4.0", 1), new Dictionary<string, JsonElement>())],
            [new("a", "input", "success", "subs"), new("b", "subs", "success", "fonts")]);
        var worker = Guid.NewGuid();
        NodeToolCapability[] Caps(string baseVersion, params NodeToolCapability[] more) => [new("plugin/base", baseVersion, ["general"], []), new("ffprobe", "7.0", ["video"], []), .. more];
        var ffmpeg = new NodeToolCapability("ffmpeg", "7.0", ["video"], []); var mkv = new NodeToolCapability("mkvtoolnix", "81.0", ["video"], []);
        Check(validator.Validate(Flow()).IsValid, "A flow with both blocks is valid.");
        Check(eligibility.CanRun(Flow(), Caps("1.4.0", ffmpeg, mkv), worker, inventory: new Encoding.Contracts.Nodes.NodeInventory("test", "x64", 1, 1, FontSets: ["Crunchyroll"])), "A worker with FFmpeg, MKVToolNix and Base 1.4.0 can run it.");
        Check(!eligibility.CanRun(Flow(), Caps("1.4.0", mkv), worker, inventory: new Encoding.Contracts.Nodes.NodeInventory("test", "x64", 1, 1, FontSets: ["Crunchyroll"])) && !eligibility.CanRun(Flow(), Caps("1.4.0", ffmpeg), worker, inventory: new Encoding.Contracts.Nodes.NodeInventory("test", "x64", 1, 1, FontSets: ["Crunchyroll"])), "Both FFmpeg and MKVToolNix are required.");
        Check(!eligibility.CanRun(Flow(), Caps("1.3.0", ffmpeg, mkv), worker, inventory: new Encoding.Contracts.Nodes.NodeInventory("test", "x64", 1, 1, FontSets: ["Crunchyroll"])), "A worker that does not announce Base 1.4.0 never receives it.");
        var lockBlock = baseManifest.Components.Single(item => item.Id == "base-disk-lock"); var unlockBlock = baseManifest.Components.Single(item => item.Id == "base-disk-unlock");
        Check(lockBlock.Outputs.Select(item => item.Id).SequenceEqual(["acquired", "busy", "error"]) && unlockBlock.Outputs.Select(item => item.Id).SequenceEqual(["success", "error"]) && lockBlock.RequiredTools.Count == 0, "The disk blocks are declared in Base 1.5.0 and need no tool, so any worker can run them.");
        Check(SubtitleOptions.From(new Dictionary<string, JsonElement> { ["styleProfile"] = J("none"), ["setTitles"] = J(false) }) is { ApplyProfile: false, SetTitles: false, ConvertSrt: true }, "Options are read from the block configuration.");
    }
}
