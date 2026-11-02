using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Nodes;

namespace Encoding.Runtime.Tests;

/// <summary>Nomenclature des pistes (architecture 0056) : modèle, variantes, marqueurs, titres d'origine, piste audio par défaut, idempotence.</summary>
internal static class TrackNameScenarios
{
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed record Spec(string Type, string Language, string Name = "", bool Default = false, bool Forced = false, bool Hearing = false, bool Commentary = false, int Channels = 2,
        string Codec = "", string Layout = "stereo");

    private static (JsonElement Identification, JsonElement Probe) Media(params Spec[] specs)
    {
        var uid = 100ul; var id = 1;
        var tracks = new List<object> { new { id = 0, type = "video", properties = new Dictionary<string, object> { ["uid"] = 1ul } } };
        var streams = new List<object> { new { codec_type = "video", codec_name = "h264" } };
        foreach (var spec in specs)
        {
            var properties = new Dictionary<string, object> { ["uid"] = uid++, ["language"] = spec.Language, ["track_name"] = spec.Name, ["default_track"] = spec.Default,
                ["forced_track"] = spec.Forced, ["flag_hearing_impaired"] = spec.Hearing, ["flag_commentary"] = spec.Commentary };
            if (spec.Type == "audio") properties["audio_channels"] = spec.Channels;
            tracks.Add(new { id = id++, type = spec.Type, properties });
            streams.Add(spec.Type == "audio"
                ? new { codec_type = "audio", codec_name = spec.Codec, channels = spec.Channels, channel_layout = spec.Layout }
                : (object)new { codec_type = "subtitle", codec_name = spec.Codec, channels = 0, channel_layout = "" });
        }
        return (J(new { tracks }), J(new { streams }));
    }

    private static string Name(IReadOnlyList<TrackNameEdit> edits, string type, int ordinal) => edits.Single(item => item.Type == type && item.Ordinal == ordinal).Name;

    public static void Run(FlowComponentRegistry registry, FlowDefinitionValidator validator, RuntimeEligibility eligibility)
    {
        var options = TrackNameOptions.From(new Dictionary<string, JsonElement>());
        var (identification, probe) = Media(
            new("audio", "fre", "VFQ", Default: true, Channels: 6, Codec: "ac3", Layout: "5.1(side)"),
            new("audio", "eng", "", Default: true, Channels: 2, Codec: "eac3"),
            new("audio", "und", "", Channels: 6, Codec: "dts", Layout: "5.1"),
            new("subtitles", "eng", "", Hearing: true, Codec: "hdmv_pgs_subtitle"),
            new("subtitles", "fre", "Forced", Codec: "ass"),
            new("subtitles", "fre", "Sous-titres complets", Codec: "subrip"));
        var edits = TrackNaming.Plan(identification, probe, null, options);
        Check(Name(edits, "audio", 0) == "Français (VFQ) / AC3 / 5.1", "A French audio track keeps its variant, codec and layout.");
        Check(Name(edits, "audio", 1) == "Anglais / EAC3 / 2.0", "A plain English track gets language, codec and layout.");
        Check(Name(edits, "audio", 2) == "DTS / 5.1", "An undetermined language leaves no orphan separator.");
        Check(Name(edits, "subtitles", 0) == "Anglais / PGS / Malentendant", "A hearing-impaired subtitle carries its marker.");
        Check(Name(edits, "subtitles", 1) == "Français / ASS / Forcé", "A forced marker in the title becomes the Forcé marker.");
        Check(Name(edits, "subtitles", 2) == "Français / SRT", "A subtitle without marker has no flags segment.");
        Check(edits.Single(item => item.Type == "audio" && item.Ordinal == 0).DefaultFlag is null && edits.Single(item => item.Type == "audio" && item.Ordinal == 1).DefaultFlag == false,
            "Only one audio track stays default: the first one that was.");

        // Idempotence : appliquer les noms puis relancer ne change plus rien.
        var renamed = Media(
            new("audio", "fre", Name(edits, "audio", 0), Default: true, Channels: 6, Codec: "ac3", Layout: "5.1(side)"),
            new("audio", "eng", Name(edits, "audio", 1), Channels: 2, Codec: "eac3"),
            new("audio", "und", Name(edits, "audio", 2), Channels: 6, Codec: "dts", Layout: "5.1"),
            new("subtitles", "eng", Name(edits, "subtitles", 0), Hearing: true, Codec: "hdmv_pgs_subtitle"),
            new("subtitles", "fre", Name(edits, "subtitles", 1), Forced: true, Codec: "ass"),
            new("subtitles", "fre", Name(edits, "subtitles", 2), Codec: "subrip"));
        Check(TrackNaming.Plan(renamed.Identification, renamed.Probe, null, options).Count == 0, "Applying the names twice changes nothing.");

        // Titres d'origine : un bloc intermédiaire a renommé les pistes ; la variante et les marqueurs viennent de l'instantané.
        var before = Media(new("audio", "fre", "VFF", Default: true, Channels: 2, Codec: "aac"), new("subtitles", "fre", "SDH", Codec: "ass"));
        var snapshot = TrackNaming.Snapshot(before.Identification);
        var converted = Media(new("audio", "fre", "Français / AAC", Default: true, Channels: 2, Codec: "aac"), new("subtitles", "fre", "Français / ASS", Codec: "ass"));
        var fromOrigin = TrackNaming.Plan(converted.Identification, converted.Probe, snapshot, options);
        Check(Name(fromOrigin, "audio", 0) == "Français (VFF) / AAC / 2.0" && Name(fromOrigin, "subtitles", 0) == "Français / ASS / Malentendant", "The origin snapshot restores the variant and the markers.");
        var otherLanguage = Media(new("audio", "eng", "Anglais / AAC", Default: true, Channels: 2, Codec: "aac"), new("subtitles", "fre", "Français / ASS", Codec: "ass"));
        Check(TrackNaming.Plan(otherLanguage.Identification, otherLanguage.Probe, snapshot, options).All(item => !item.Name.Contains("VFF", StringComparison.Ordinal)), "An origin entry of another language is ignored.");

        // Piste audio par défaut préférée, hors commentaire.
        var languages = Media(new("audio", "eng", "", Default: true, Codec: "aac"), new("audio", "fre", "Commentaire", Codec: "aac", Commentary: true), new("audio", "fre", "", Codec: "aac"));
        var preferred = TrackNaming.Plan(languages.Identification, languages.Probe, null, options with { DefaultAudioLanguage = "fr" });
        Check(preferred.Single(item => item.Ordinal == 2).DefaultFlag == true && preferred.Single(item => item.Ordinal == 0).DefaultFlag == false && preferred.Single(item => item.Ordinal == 1).DefaultFlag is null,
            "The preferred language becomes the default audio track, a commentary track never does.");
        Check(TrackNaming.Plan(languages.Identification, languages.Probe, null, options with { SingleDefaultAudio = false }).All(item => item.DefaultFlag is null), "The default flag is left alone when the option is off.");

        Check(TrackNaming.Render("{language} {variant} / {codec} / {layout}", new Dictionary<string, string> { ["language"] = "Anglais", ["variant"] = "", ["codec"] = "AC3", ["layout"] = "" }) == "Anglais / AC3", "Empty segments are dropped.");
        Check(TrackNameOptions.From(new Dictionary<string, JsonElement> { ["audioTemplate"] = J("{codec}") }) is { AudioTemplate: "{codec}", SingleDefaultAudio: true }, "Templates are read from the block configuration.");
        Check(TrackNaming.Snapshot(before.Identification).GetProperty("tracks").GetArrayLength() == 2, "The snapshot keeps audio and subtitle tracks only.");

        var subsManifest = registry.Plugins.Single(item => item.Id == "subtitles");
        var names = subsManifest.Components.Single(item => item.Id == "subtitles-track-names");
        var baseManifest = registry.Plugins.Single(item => item.Id == "base");
        var input = baseManifest.Components.Single(item => item.Id == "base-input-file");
        Check(subsManifest.Version == "1.3.0" && names.Version == "1.0.0" && names.Outputs.Select(item => item.Id).SequenceEqual(["success", "unchanged", "error"]), "The block is declared in Subtitles 1.3.0 with its three outputs.");
        FlowDefinition Flow() => new(1, Guid.NewGuid(), 1, "Track names", "video", [
            new("input", null, new("base", "1.3.0", input.Id, input.Version, 1), new Dictionary<string, JsonElement>()),
            new("names", null, new("base", "1.3.0", "base-track-names", "1.3.0", 1), new Dictionary<string, JsonElement>())], [new("edge", "input", "success", "names")]);
        var worker = Guid.NewGuid();
        NodeToolCapability[] Caps(string baseVersion, params NodeToolCapability[] more) => [new("plugin/base", baseVersion, ["general"], []), new("ffprobe", "7.0", ["video"], []), .. more];
        Check(validator.Validate(Flow()).IsValid, "A flow with the track names block is valid.");
        Check(eligibility.CanRun(Flow(), Caps("1.3.0", new NodeToolCapability("mkvtoolnix", "81.0", ["video"], [])), worker), "A worker with MKVToolNix and Base 1.3.0 can run it.");
        Check(!eligibility.CanRun(Flow(), Caps("1.3.0"), worker), "A worker without MKVToolNix never receives it.");
        Check(!eligibility.CanRun(Flow(), Caps("1.2.0", new NodeToolCapability("mkvtoolnix", "81.0", ["video"], [])), worker), "A worker that does not announce Base 1.3.0 never receives it.");
    }
}
