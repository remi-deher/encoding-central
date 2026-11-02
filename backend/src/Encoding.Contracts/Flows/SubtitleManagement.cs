using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Encoding.Contracts.Flows;

/// <summary>Sous-titres ou polices impossibles à traiter sans risque (piste vide non confirmée, police absente, dimensions invalides). Le bloc suit sa sortie « Erreur ».</summary>
public sealed class SubtitleException(string message) : Exception(message);

/// <summary>Options du bloc <c>base-subtitle-manage</c> (architecture 0057).</summary>
public sealed record SubtitleOptions(bool ApplyProfile = true, bool ConvertSrt = true, bool UpgradeAss = true, bool RemoveDuplicates = true,
    bool InferForced = true, bool SetTitles = true, bool FrenchDefault = true, string? Operation = null, AssStyleSettings? Style = null)
{
    public static SubtitleOptions From(IReadOnlyDictionary<string, JsonElement> configuration)
    {
        bool Flag(string key) => !configuration.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.False;
        var profile = configuration.TryGetValue("styleProfile", out var mode) && mode.ValueKind == JsonValueKind.String ? mode.GetString() : "crunchyroll";
        return new(profile != "none", Flag("convertSrt"), Flag("upgradeAss"), Flag("removeDuplicates"), Flag("inferForced"), Flag("setTitles"),
            Flag("frenchDefault"), configuration.TryGetValue("operation", out var operation) ? operation.GetString() : null,
            profile == "custom" ? AssStyleSettings.From(configuration) : null);
    }
}

/// <summary>Piste de sous-titres telle que lue dans le fichier. <see cref="AssText"/> est le contenu ASS (extrait ou converti) des pistes texte, nul pour les pistes bitmap.</summary>
public sealed record SubtitleTrack(int Ordinal, string Codec, string Language, string Title, bool Default, bool Forced, bool HearingImpaired,
    string? AssText, bool EmptyConfirmed = false);

/// <summary>Décision pour une piste. <see cref="ReplacementAss"/> n'est renseigné que si le contenu change (SRT converti, ASS mis à niveau) ; sinon la piste d'origine est recopiée telle quelle.</summary>
public sealed record SubtitleDecision(int Ordinal, bool Drop, string? ReplacementAss, string Title, bool Default, bool Forced, string Language, string Note, bool HearingImpaired = false);

public sealed record SubtitlePlan(IReadOnlyList<SubtitleDecision> Decisions, IReadOnlyList<string> Notes, bool Changed);

/// <summary>Profil de style ASS proportionnel (référence 16:9 sur 360 lignes), intégré et en lecture seule.</summary>
public static class AssProfile
{
    public const string Version = "2";
    private const string StyleFormat = "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding";
    public static readonly string[] StyleNames = ["Default", "Italique", "TiretsDefault", "Sign"];
    private static readonly Regex Positioned = new(@"\{[^}]*\\(?:pos|move|org|clip|iclip|p[1-9]|fs|fscx|fscy|bord|shad|fn)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Dimensions affichées de la vidéo (rapport d'affichage, anamorphose et rotation pris en compte).</summary>
    public static (int Width, int Height) Canvas(JsonElement video)
    {
        double Number(string name) => video.TryGetProperty(name, out var value)
            && (value.ValueKind == JsonValueKind.Number ? value.TryGetDouble(out var n) : double.TryParse(value.GetString(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out n)) ? n : 0;
        double Ratio(string name)
        {
            if (!video.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return 0;
            var parts = value.GetString()!.Split(':', '/');
            return parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.TryParse(parts[1],
                NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && a > 0 && b > 0 ? a / b : 0;
        }
        double width = Number("width"), height = Number("height");
        if (width <= 0 || height <= 0) throw new SubtitleException("Dimensions vidéo invalides : profil adaptatif impossible. Original non remplacé.");
        var display = Ratio("display_aspect_ratio");
        if (display <= 0) display = width / height * (Ratio("sample_aspect_ratio") is var sar and > 0 ? sar : 1);
        double rotation = 0;
        if (video.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty("rotate", out var rotate)
            && double.TryParse(rotate.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var tagged)) rotation = tagged;
        if (video.TryGetProperty("side_data_list", out var sides) && sides.ValueKind == JsonValueKind.Array)
            foreach (var side in sides.EnumerateArray()) if (side.TryGetProperty("rotation", out var value) && value.TryGetDouble(out var angle)) rotation = angle;
        var turned = Math.Abs(rotation % 180) == 90;
        if (turned) display = 1 / display;
        var canvasHeight = turned ? width : height;
        var canvasWidth = Math.Round(canvasHeight * display, MidpointRounding.AwayFromZero);
        return ((int)canvasWidth, (int)Math.Round(canvasHeight, MidpointRounding.AwayFromZero));
    }

    private static string Num(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero).ToString("0.###", CultureInfo.InvariantCulture);

    private static string Scaled(string line, int width, int height, AssStyleSettings? settings = null)
    {
        var factor = height / 360.0;
        var fields = line.Split(',');
        if (settings is not null) {
            fields[1] = settings.FontFamily;
            fields[2] = Num(settings.FontSize);
            fields[16] = Num(settings.Outline);
            fields[21] = Num(settings.Margin);
        }
        foreach (var index in new[] { 2, 16, 17 }) fields[index] = Num(double.Parse(fields[index], CultureInfo.InvariantCulture) * factor);
        fields[19] = fields[20] = Num(Math.Round(width * 20 / 640.0, MidpointRounding.AwayFromZero));
        fields[21] = Num(Math.Round(height * (settings?.Margin ?? 20) / 360.0, MidpointRounding.AwayFromZero));
        return string.Join(',', fields);
    }

    public static string[] Styles(int width, int height, AssStyleSettings? settings = null) =>
    [
        Scaled("Style: Default,Trebuchet MS,23,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,-1,0,0,0,100,100,0,0,1,2.5,0,2,20,20,20,1", width, height, settings),
        Scaled("Style: Italique,Trebuchet MS,23,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,-1,-1,0,0,100,100,0,0,1,2.5,0,2,20,20,20,1", width, height, settings),
        Scaled("Style: TiretsDefault,Trebuchet MS,23,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,-1,0,0,0,100,100,0,0,1,2.5,0,1,20,20,20,1", width, height, settings),
        Scaled("Style: Sign,Arial,21,&H00FFFFFF,&H000000FF,&H00292929,&H00000000,-1,0,0,0,100,100,0,0,1,1,0,8,20,20,20,1", width, height, settings)
    ];

    public static string Header(int width, int height, AssStyleSettings? settings = null) =>
        "[Script Info]\nTitle: New subtitles\nSubtitleProfileVersion: " + Version + "\nScriptType: v4.00+\nWrapStyle: 0\nPlayResX: " + width + "\nPlayResY: " + height
        + "\nScaledBorderAndShadow: yes\nYCbCr Matrix: TV.601\nLayoutResX: " + width + "\nLayoutResY: " + height + "\n\n[V4+ Styles]\n" + StyleFormat + "\n"
        + string.Join('\n', Styles(width, height, settings)) + "\n\n";

    public static bool HasCurrent(string text, int width, int height, AssStyleSettings? settings = null)
    {
        var lines = text.Replace("\r", "").Split('\n').Select(line => line.Trim()).ToHashSet(StringComparer.Ordinal);
        var styles = Styles(width, height, settings);
        string[] required = ["SubtitleProfileVersion: " + Version, "PlayResX: " + width, "PlayResY: " + height, "LayoutResX: " + width, "LayoutResY: " + height,
            "ScaledBorderAndShadow: yes", "YCbCr Matrix: TV.601", styles[0], styles[1], styles[2], styles[3]];
        return required.All(lines.Contains) && text.Contains(StyleFormat, StringComparison.Ordinal) && text.IndexOf(StyleFormat,
            StringComparison.Ordinal) < text.IndexOf(styles[0], StringComparison.Ordinal);
    }

    /// <summary>Styles propres au typesetting ou balises de placement : un tel ASS n'est jamais recalibré.</summary>
    public static bool IsCustom(string text) =>
        Positioned.IsMatch(text) || text.Replace("\r", "").Split('\n').Any(line => Regex.Match(line, @"^Style:\s*([^,]+),",
            RegexOptions.IgnoreCase) is { Success: true } match && !StyleNames.Contains(match.Groups[1].Value.Trim()));

    private static string SetScriptInfo(string text, string key, string value)
    {
        var pattern = new Regex("^" + Regex.Escape(key) + @":\s*.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (pattern.IsMatch(text)) return pattern.Replace(text, key + ": " + value, 1);
        var styles = Regex.Match(text, @"^\[V4\+ Styles\]", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return styles.Success ? text[..styles.Index] + key + ": " + value + "\n" + text[styles.Index..] : text;
    }

    /// <summary>Normalise l'en-tête et les quatre styles communs d'un ASS simple ; les événements sont conservés. <c>Complex</c> : pas de section de styles exploitable, le fichier reste intact.</summary>
    public static (string Text, bool Changed, bool Complex) Upgrade(string text, int width, int height, AssStyleSettings? settings = null)
    {
        text = text.TrimStart('\uFEFF');
        var original = text;
        foreach (var (key, value) in new[] { ("SubtitleProfileVersion", Version), ("PlayResX", width.ToString(CultureInfo.InvariantCulture)), ("PlayResY",
            height.ToString(CultureInfo.InvariantCulture)),
            ("ScaledBorderAndShadow", "yes"), ("YCbCr Matrix", "TV.601"), ("LayoutResX", width.ToString(CultureInfo.InvariantCulture)), ("LayoutResY", height.ToString(CultureInfo.InvariantCulture)) })
            text = SetScriptInfo(text, key, value);
        var start = Regex.Match(text, @"^\[V4\+ Styles\]\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!start.Success) return (text, false, true);
        var next = Regex.Match(text[(start.Index + 1)..], @"^\[[^\]]+\]\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        var end = next.Success ? start.Index + 1 + next.Index : text.Length;
        var result = text[..start.Index] + "[V4+ Styles]\n" + StyleFormat + "\n" + string.Join('\n', Styles(width, height)) + "\n\n" + text[end..];
        return (result, result != original, false);
    }
}

/// <summary>Classement et planification des sous-titres : conversion, mise à niveau, doublons, forcés, titres et piste par défaut (architecture 0057).</summary>
public static class SubtitlePlanner
{
    private static readonly string[] TextCodecs = ["subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text", "microdvd", "subviewer", "subviewer1", "sami",
        "jacosub", "mpl2", "vplayer", "pjs", "realtext", "stl", "ttml"];
    private static readonly Regex ForcedTitle = new(@"(^|[^a-z])(forced|forcee?s?|fcd|signs?)([^a-z]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SdhTitle = new(@"(^|[^a-z])(s[. _-]*d[. _-]*h|cc|hoh|sme|closed[ _-]*captions?|hearing[ _-]*impaired|hard[ _-]*of[ _-]*hearing|sourds?|malentendants?)([^a-z]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SignsTitle = new(@"(signs?.*songs?|songs?.*signs?|signes?.*chansons?|chansons?.*signes?|karaoke)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FullTitle = new(@"(^|[^a-z])(full|complete|complet(?:e|es|s)?|vostfr|dialogues?)([^a-z]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["fr"] = "Français", ["fra"] = "Français", ["fre"] = "Français", ["frf"] = "Français", ["french"] = "Français", ["francais"] = "Français",
        ["en"] = "Anglais", ["eng"] = "Anglais", ["english"] = "Anglais", ["ja"] = "Japonais", ["jpn"] = "Japonais", ["japanese"] = "Japonais",
        ["de"] = "Allemand", ["deu"] = "Allemand", ["ger"] = "Allemand", ["es"] = "Espagnol", ["spa"] = "Espagnol", ["it"] = "Italien", ["ita"] = "Italien",
        ["ko"] = "Coréen", ["kor"] = "Coréen", ["zh"] = "Chinois", ["zho"] = "Chinois", ["chi"] = "Chinois", ["pt"] = "Portugais", ["por"] = "Portugais"
    };
    private static readonly Dictionary<string, string> LangKeys = new() { ["fr"] = "fre", ["fra"] = "fre", ["frf"] = "fre", ["en"] = "eng", ["ja"] = "jpn", ["de"] = "deu", ["ger"] = "deu" };
    private static readonly Dictionary<string, string> BitmapLabels = new() { ["hdmv_pgs_subtitle"] = "PGS", ["dvd_subtitle"] = "VobSub", ["dvb_subtitle"] = "DVB" };

    public static bool IsText(string codec) => TextCodecs.Contains(codec.ToLowerInvariant());
    public static bool IsAss(string codec) => codec.ToLowerInvariant() is "ass" or "ssa";

    internal static string Norm(string? value)
    {
        var text = (value ?? "").Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        return new string(text.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).Normalize(NormalizationForm.FormC);
    }

    public static bool IsFrench(string? value)
    {
        var code = Norm(value).Replace('_', '-').Split('-')[0];
        return code is "fr" or "fra" or "fre" or "frf" or "french" or "francais";
    }

    private static string LangKey(string? value)
    {
        var code = Norm(value).Split('-', '_')[0];
        return LangKeys.TryGetValue(code, out var key) ? key : code;
    }

    private static bool Signs(SubtitleTrack track) => SignsTitle.IsMatch(Norm(track.Title));
    private static bool Forced(SubtitleTrack track) => track.Forced || !Signs(track) && ForcedTitle.IsMatch(Norm(track.Title));
    private static bool Sdh(SubtitleTrack track) => track.HearingImpaired || SdhTitle.IsMatch(Norm(track.Title));
    private static bool FullByTitle(SubtitleTrack track) => FullTitle.IsMatch(Norm(track.Title));

    /// <summary>Langue de la piste audio de référence : l'unique piste par défaut, sinon l'unique piste audio. Nul si le conteneur n'est pas explicite.</summary>
    public static bool? ReferenceAudioIsFrench(JsonElement probe)
    {
        if (probe.ValueKind != JsonValueKind.Object || !probe.TryGetProperty("streams", out var streams)) return null;
        var audio = streams.EnumerateArray().Where(item => item.TryGetProperty("codec_type", out var type) && type.GetString() == "audio").ToArray();
        var defaults = audio.Where(item => item.TryGetProperty("disposition", out var disposition) && disposition.TryGetProperty("default", out var flag) && flag.GetInt32() == 1).ToArray();
        var reference = defaults.Length == 1 ? defaults[0] : audio.Length == 1 ? audio[0] : (JsonElement?)null;
        if (reference is not { } track) return null;
        var language = track.TryGetProperty("tags", out var tags) && tags.TryGetProperty("language", out var value) ? value.GetString() ?? "" : "";
        return language.Length == 0 ? null : IsFrench(language);
    }

    internal sealed record Dialogue(double Start, double End, string Text);

    private static string PlainText(string value) => Regex.Replace(Norm(Regex.Replace(Regex.Replace(Regex.Replace(value, @"\{[^}]*\}", ""), @"\\[Nnh]", " "), "<[^>]+>", "")), @"\s+", " ").Trim();

    private static double Seconds(string value)
    {
        var parts = value.Trim().Split(':');
        return parts.Length == 3 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && double.TryParse(parts[1],
            NumberStyles.Float, CultureInfo.InvariantCulture, out var m)
            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? h * 3600 + m * 60 + s : double.NaN;
    }

    internal static List<Dialogue> Dialogues(string ass)
    {
        var rows = new List<Dialogue>();
        foreach (var line in ass.Replace("\r", "").Split('\n'))
        {
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
            var fields = line.Split(',');
            if (fields.Length < 10) continue;
            var text = PlainText(string.Join(',', fields.Skip(9)));
            if (text.Length > 0) rows.Add(new(Seconds(fields[1]), Seconds(fields[2]), text));
        }
        return rows;
    }

    private static bool TimedSubset(IReadOnlyList<Dialogue> shorter, IReadOnlyList<Dialogue> longer)
    {
        if (shorter.Count == 0 || longer.Count == 0 || shorter.Count > longer.Count) return false;
        if (longer.Count - shorter.Count > Math.Max(2, (int)Math.Floor(longer.Count * 0.02))) return false;
        const double tolerance = 0.100001;
        var j = 0;
        var matched = 0;
        foreach (var e in shorter)
        {
            if (double.IsNaN(e.Start) || double.IsNaN(e.End)) return false;
            while (j < longer.Count && longer[j].Start < e.Start - tolerance) j++;
            for (var k = j; k < longer.Count && longer[k].Start <= e.Start + tolerance; k++)
                if (e.Text == longer[k].Text && Math.Abs(e.End - longer[k].End) <= tolerance) { matched++; j = k + 1; break; }
        }
        return matched == shorter.Count;
    }

    private sealed class State(SubtitleTrack track)
    {
        public SubtitleTrack Track = track;
        public List<string> Events = [];
        public List<Dialogue> Rows = [];
        public bool Full, DeducedForced, Dropped, Custom;
        public bool IsForced, IsDefault;
        public string? Replacement;
        public string Kind = "";
    }

    public static SubtitlePlan Plan(IReadOnlyList<SubtitleTrack> tracks, bool? referenceAudioFrench, (int Width, int Height) canvas, SubtitleOptions options)
    {
        var notes = new List<string>();
        var states = tracks.Select(track => new State(track)).ToList();
        var byOrdinal = states.ToDictionary(item => item.Track.Ordinal);

        // Contenu des pistes texte : pistes vides, puis forcés et pistes complètes par comparaison de texte.
        foreach (var state in states.Where(item => item.Track.AssText is not null))
        {
            state.Rows = Dialogues(state.Track.AssText!);
            state.Events = state.Rows.Select(row => row.Text).ToList();
            state.Custom = AssProfile.IsCustom(state.Track.AssText!);
            if (state.Events.Count > 0) { notes.Add($"Sous-titres piste {state.Track.Ordinal} ({state.Track.Language}) : {state.Events.Count} répliques."); continue; }
            if (AssIsEmpty(state.Track)) { state.Dropped = options.Operation is null or "subtitles-remove-duplicates";
                notes.Add($"Piste {state.Track.Ordinal} vide confirmée : {(state.Dropped ? "retirée" : "conservée") }."); }
            else throw new SubtitleException($"Piste {state.Track.Ordinal} sans répliques, mais piste source non confirmée vide ({state.Track.Codec}). Source conservée ; vérifier l'extraction.");
        }
        var live = states.Where(item => !item.Dropped).ToList();
        foreach (var forced in live.Where(item => Forced(item.Track) && item.Track.AssText is not null))
        {
            var key = LangKey(forced.Track.Language);
            if (key.Length == 0 || key == "und") continue;
            foreach (var other in live.Where(item => item.Track.AssText is not null && !Forced(item.Track) && !Sdh(item.Track) && !Signs(item.Track) && LangKey(item.Track.Language) == key
                && item.Events.Count >= 200 && (double)forced.Events.Count / item.Events.Count <= 0.10)) other.Full = true;
        }
        if (options.InferForced)
            foreach (var state in live.Where(item => item.Track.AssText is not null))
            {
                var key = LangKey(state.Track.Language);
                if (key.Length == 0 || key == "und" || Forced(state.Track) || FullByTitle(state.Track) || Sdh(state.Track) || Signs(state.Track) || state.Events.Count == 0) continue;
                var candidates = live.Where(item => item != state && item.Track.AssText is not null && LangKey(item.Track.Language) == key
                    && item.Events.Count >= 200 && !Forced(item.Track) && !Signs(item.Track)).ToList();
                if (candidates.Count == 0) continue;
                var largest = candidates.OrderByDescending(item => item.Events.Count).First();
                var ratio = (double)state.Events.Count / largest.Events.Count;
                if (ratio > 0.10) continue;
                var set = largest.Events.ToHashSet(StringComparer.Ordinal);
                var overlap = (double)state.Events.Count(set.Contains) / state.Events.Count;
                if (overlap >= 0.90)
                {
                    state.DeducedForced = true; largest.Full = true;
                    notes.Add($"Piste forcée déduite : {state.Track.Ordinal} contre {largest.Track.Ordinal}, ratio {ratio.ToString("0.0000", CultureInfo.InvariantCulture)}, texte commun {overlap.ToString("0.0000", CultureInfo.InvariantCulture)}.");
                }
                else notes.Add($"Piste courte ambiguë {state.Track.Ordinal} : statut conservé, texte commun {overlap.ToString("0.0000", CultureInfo.InvariantCulture)}.");
            }
        foreach (var state in live) { state.IsForced = state.Track.Forced || Forced(state.Track) || state.DeducedForced; state.IsDefault = state.Track.Default; }

        // Doublons : un ASS identique (texte et horaires à 0,1 s) à une autre piste de même langue et mêmes marqueurs est retiré.
        if (options.RemoveDuplicates)
        {
            var ranked = live.Where(item => item.Track.AssText is not null && item.Rows.Count > 0 && LangKey(item.Track.Language) is { Length: > 0 } key && key != "und")
                .OrderByDescending(item => item.Rows.Count).ThenByDescending(item => item.Custom).ThenBy(item => item.Track.Ordinal).ToList();
            for (var i = 0; i < ranked.Count; i++)
            {
                var keep = ranked[i];
                if (keep.Dropped) continue;
                for (var j = i + 1; j < ranked.Count; j++)
                {
                    var candidate = ranked[j];
                    if (candidate.Dropped || LangKey(candidate.Track.Language) != LangKey(keep.Track.Language) || candidate.IsForced != keep.IsForced
                        || Sdh(candidate.Track) != Sdh(keep.Track) || Signs(candidate.Track) != Signs(keep.Track) || candidate.Custom || keep.Custom) continue;
                    if (!TimedSubset(candidate.Rows, keep.Rows)) continue;
                    candidate.Dropped = true;
                    notes.Add($"Doublon confirmé : piste {candidate.Track.Ordinal} retirée, piste {keep.Track.Ordinal} conservée ({candidate.Rows.Count} / {keep.Rows.Count} répliques).");
                }
            }
            live = live.Where(item => !item.Dropped).ToList();
        }

        // Piste par défaut : quand l'audio de référence n'est pas français, le sous-titre français (complet de préférence) l'est.
        if (options.FrenchDefault && referenceAudioFrench == false)
        {
            var candidates = new List<State>();
            foreach (var state in live.Where(item => IsFrench(item.Track.Language) && !item.IsForced))
                if (FullByTitle(state.Track)) candidates.Insert(0, state); else candidates.Add(state);
            if (candidates.Count > 0)
            {
                foreach (var state in live) state.IsDefault = state == candidates[0];
                notes.Add($"Sous-titre par défaut : piste {candidates[0].Track.Ordinal} (audio de référence non français).");
            }
        }

        // Conversion SRT vers ASS et mise à niveau des ASS simples. Les pistes bitmap et les ASS personnalisés restent intacts.
        if (options.ApplyProfile || options.Operation == "subtitles-convert-ass")
            foreach (var state in live.Where(item => item.Track.AssText is not null))
            {
                var text = state.Track.AssText!.TrimStart('\uFEFF');
                if (!IsAss(state.Track.Codec))
                {
                    if (!options.ConvertSrt) continue;
                    var events = text.IndexOf("[Events]", StringComparison.Ordinal);
                    if (events < 0) throw new SubtitleException($"Section [Events] absente après conversion de la piste {state.Track.Ordinal}.");
                    state.Replacement = options.ApplyProfile ? AssProfile.Header(canvas.Width, canvas.Height, options.Style) + text[events..] : text;
                    if (options.ApplyProfile && !AssProfile.HasCurrent(state.Replacement, canvas.Width, canvas.Height,
                        options.Style)) throw new SubtitleException($"Validation du profil de style échouée, piste {state.Track.Ordinal}.");
                    state.Kind = "converti";
                }
                else if (AssProfile.HasCurrent(text, canvas.Width, canvas.Height, options.Style)) state.Kind = "à jour";
                else if (state.Custom) { state.Kind = "personnalisé"; notes.Add($"ASS avec styles ou placements personnalisés conservé sans recalibrage : piste {state.Track.Ordinal}."); }
                else if (options.UpgradeAss)
                {
                    var upgrade = AssProfile.Upgrade(text, canvas.Width, canvas.Height, options.Style);
                    if (upgrade.Complex) { state.Kind = "personnalisé"; notes.Add($"ASS sans section de styles standard laissé intact : piste {state.Track.Ordinal}."); }
                    else if (upgrade.Changed)
                    {
                        if (!AssProfile.HasCurrent(upgrade.Text, canvas.Width, canvas.Height, options.Style)) throw new SubtitleException($"Mise à niveau ASS invalide, piste {state.Track.Ordinal}.");
                        state.Replacement = upgrade.Text; state.Kind = "mis à niveau";
                    }
                }
            }

        var decisions = new List<SubtitleDecision>();
        var changed = false;
        // Le numéro de piste suit la position dans le fichier de sortie : le renommage est stable après le retrait d'un doublon.
        var positions = states.Where(item => !item.Dropped).Select((item, index) => (item.Track.Ordinal, index)).ToDictionary(item => item.Ordinal, item => item.index + 1);
        foreach (var state in states)
        {
            var track = state.Track;
            var title = track.Title;
            var label = Labels.TryGetValue(Norm(track.Language).Replace('_', '-').Split('-')[0], out var known) ? known : null;
            if (options.SetTitles && label is not null && !state.Dropped)
            {
                var format = state.Replacement is not null || IsAss(track.Codec) ? "ASS"
                    : IsText(track.Codec) ? (track.Codec.ToLowerInvariant() is "subrip" or "srt" ? "SRT" : track.Codec.ToUpperInvariant())
                    : BitmapLabels.TryGetValue(track.Codec.ToLowerInvariant(), out var bitmap) ? bitmap : track.Codec.ToUpperInvariant();
                title = state.IsForced ? $"{label} / {format} / Forcé"
                    : Sdh(track) ? $"{label} / {format} / Malentendant"
                    : Signs(track) ? $"{label} / {format} / Signs & Songs"
                    : FullByTitle(track) || state.Full ? $"{label} / {format} / Complet"
                    : $"{label} / {format} / Piste {positions[track.Ordinal]}";
            }
            var language = options.Operation is null && track.Language.Equals("frf", StringComparison.OrdinalIgnoreCase) ? "fr-FR" : track.Language;
            if (options.Operation is not (null or "subtitles-detect-forced")) state.IsForced = track.Forced;
            var note = state.Dropped ? "retirée" : state.Kind;
            // Le drapeau « malentendant » suit le titre comme l'indicateur Matroska : une piste réécrite ne le perd jamais.
            var hearing = options.Operation is null ? Sdh(track) : track.HearingImpaired;
            var different = state.Dropped || state.Replacement is not null || state.IsForced != track.Forced || state.IsDefault != track.Default || title != track.Title
                || language != track.Language || hearing != track.HearingImpaired;
            changed |= different;
            decisions.Add(new(track.Ordinal, state.Dropped, state.Replacement, title, state.IsDefault, state.IsForced, language, note, hearing));
        }
        return new(decisions, notes, changed);
    }

    private static bool AssIsEmpty(SubtitleTrack track) => IsAss(track.Codec) || track.EmptyConfirmed;

    /// <summary>Vrai si un SRT brut ne contient aucun texte visible (balises et blocs vides ignorés).</summary>
    public static bool SrtHasNoVisibleText(string raw)
    {
        var text = raw.TrimStart('\uFEFF').Trim();
        if (text.Length == 0) return true;
        foreach (var block in Regex.Split(text, @"\r?\n\s*\r?\n"))
        {
            var lines = block.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
            if (lines.Count > 0 && Regex.IsMatch(lines[0].Trim(), @"^\d+$")) lines.RemoveAt(0);
            if (lines.Count == 0 || !Regex.IsMatch(lines[0].Trim(), @"^\d{1,2}:\d\d:\d\d[,.]\d+\s*-->\s*\d{1,2}:\d\d:\d\d[,.]\d+\s*$")) return false;
            lines.RemoveAt(0);
            var payload = Regex.Replace(Regex.Replace(string.Join('\n', lines), @"\{\\[^}]*\}", ""), @"</?(?:font|b|i|u|s|span)(?:\s[^>]*)?>", "", RegexOptions.IgnoreCase);
            payload = Regex.Replace(payload, "&nbsp;|&#160;|&#xA0;", " ", RegexOptions.IgnoreCase);
            if (payload.Trim().Length > 0) return false;
        }
        return true;
    }
}
