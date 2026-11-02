using System.Text.Json;
using System.Text.RegularExpressions;

namespace Encoding.Contracts.Flows;

/// <summary>Options du bloc <c>base-track-names</c> (architecture 0056).</summary>
public sealed record TrackNameOptions(string AudioTemplate, string SubtitleTemplate, bool SingleDefaultAudio, string DefaultAudioLanguage, bool RenameTracks = true)
{
    public const string DefaultAudioTemplate = "{language} {variant} / {codec} / {layout} / {flags}";
    public const string DefaultSubtitleTemplate = "{language} {variant} / {codec} / {flags}";

    public static TrackNameOptions From(IReadOnlyDictionary<string, JsonElement> configuration)
    {
        string Text(string key, string fallback) => configuration.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : fallback;
        var single = !configuration.TryGetValue("singleDefaultAudio", out var flag) || flag.ValueKind != JsonValueKind.False;
        return new(Text("audioTemplate", DefaultAudioTemplate), Text("subtitleTemplate", DefaultSubtitleTemplate), single, Text("defaultAudioLanguage", ""),
            !configuration.TryGetValue("renameTracks", out var rename) || rename.ValueKind != JsonValueKind.False);
    }
}

/// <summary>Modification d'une piste Matroska : <see cref="Name"/> toujours définie, <see cref="DefaultFlag"/> seulement si elle change.</summary>
public sealed record TrackNameEdit(ulong Uid, string Type, int Ordinal, string Name, bool? DefaultFlag, string Note);

/// <summary>
/// Nomenclature des pistes audio et sous-titres (« Français (VFQ) / AC3 / 5.1 », « Anglais / PGS / Malentendant ») et piste audio par
/// défaut unique. La variante (VFF, VFQ…) et les marqueurs viennent de l'instantané des titres d'origine quand il existe, parce qu'un
/// bloc intermédiaire peut avoir renommé les pistes.
/// </summary>
public static class TrackNaming
{
    private static readonly Regex Variant = new(@"(?<![A-Za-z0-9])(VFF|VFQ|VFI|VF|VOSTFR|VOF|VO)(?![A-Za-z0-9])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ForcedWord = new(@"(?<![a-z])(forced|forc[eé]e?s?)(?![a-z])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex HearingWord = new(@"(?<![a-z])(sdh|malentendants?|hearing[ -]impaired|cc)(?![a-z])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex CommentaryWord = new(@"(?<![a-z])(commentaires?|commentary)(?![a-z])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, (string Code, string Label)> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fr"] = ("fr", "Français"), ["fra"] = ("fr", "Français"), ["fre"] = ("fr", "Français"), ["frf"] = ("fr", "Français"), ["french"] = ("fr", "Français"),
        ["en"] = ("en", "Anglais"), ["eng"] = ("en", "Anglais"), ["english"] = ("en", "Anglais"),
        ["ja"] = ("ja", "Japonais"), ["jpn"] = ("ja", "Japonais"), ["japanese"] = ("ja", "Japonais"),
        ["de"] = ("de", "Allemand"), ["deu"] = ("de", "Allemand"), ["ger"] = ("de", "Allemand"),
        ["es"] = ("es", "Espagnol"), ["spa"] = ("es", "Espagnol"), ["it"] = ("it", "Italien"), ["ita"] = ("it", "Italien"),
        ["ko"] = ("ko", "Coréen"), ["kor"] = ("ko", "Coréen"), ["zh"] = ("zh", "Chinois"), ["zho"] = ("zh", "Chinois"), ["chi"] = ("zh", "Chinois"),
        ["pt"] = ("pt", "Portugais"), ["por"] = ("pt", "Portugais")
    };

    /// <summary>Titres, langues et marqueurs des pistes audio et sous-titres avant toute modification, à conserver dans une variable du flow.</summary>
    public static JsonElement Snapshot(JsonElement identification)
    {
        var items = new List<object>();
        foreach (var track in Tracks(identification))
            items.Add(new { type = track.Type, ordinal = track.Ordinal, language = track.Language, name = track.Name, forced = track.Forced,
                hearingImpaired = track.HearingImpaired, commentary = track.Commentary, @default = track.Default });
        return JsonSerializer.SerializeToElement(new { tracks = items });
    }

    public static IReadOnlyList<TrackNameEdit> Plan(JsonElement identification, JsonElement probe, JsonElement? origin, TrackNameOptions options)
    {
        var tracks = Tracks(identification);
        var audioProbe = Streams(probe, "audio");
        var subtitleProbe = Streams(probe, "subtitle");
        var originals = OriginTracks(origin);
        var edits = new List<TrackNameEdit>();

        // Piste audio par défaut : la préférée, sinon celle qui l'est déjà, sinon la première hors commentaire.
        var audio = tracks.Where(item => item.Type == "audio").ToArray();
        bool?[] defaults = new bool?[audio.Length];
        if (options.SingleDefaultAudio && audio.Length > 0)
        {
            var preferred = options.DefaultAudioLanguage.Length > 0 ? LanguageCode(options.DefaultAudioLanguage) : null;
            var chosen = preferred is null ? -1 : Array.FindIndex(audio, item => LanguageCode(item.Language) == preferred && !Effective(item, originals).Commentary);
            if (chosen < 0) chosen = Array.FindIndex(audio, item => item.Default);
            if (chosen < 0) chosen = Math.Max(0, Array.FindIndex(audio, item => !Effective(item, originals).Commentary));
            for (var i = 0; i < audio.Length; i++)
            {
                var wanted = i == chosen;
                if (audio[i].Default != wanted) defaults[i] = wanted;
            }
        }

        foreach (var track in tracks.Where(item => item.Type is "audio" or "subtitles"))
        {
            var source = Effective(track, originals);
            var streams = track.Type == "audio" ? audioProbe : subtitleProbe;
            var stream = track.Ordinal < streams.Count ? streams[track.Ordinal] : (JsonElement?)null;
            var language = Languages.TryGetValue(track.Language, out var known) ? known.Label : track.Language is "und" or "" ? "" : track.Language.ToUpperInvariant();
            var variant = Variant.Match(source.Name) is { Success: true } match ? "(" + match.Value.ToUpperInvariant() + ")" : "";
            var flags = new List<string>();
            if (source.Forced) flags.Add("Forcé");
            if (source.HearingImpaired) flags.Add("Malentendant");
            if (source.Commentary) flags.Add("Commentaire");
            var tokens = new Dictionary<string, string>
            {
                ["language"] = language, ["variant"] = variant, ["codec"] = stream is { } s ? CodecLabel(s, track.Type) : "",
                ["layout"] = track.Type == "audio" && stream is { } a ? LayoutLabel(a, track.AudioChannels) : "", ["flags"] = string.Join(" / ", flags)
            };
            var name = options.RenameTracks ? Render(track.Type == "audio" ? options.AudioTemplate : options.SubtitleTemplate, tokens) : track.Name;
            var defaultChange = track.Type == "audio" ? defaults[Array.IndexOf(audio, track)] : null;
            if (name != track.Name || defaultChange is not null)
                edits.Add(new(track.Uid, track.Type, track.Ordinal, name, defaultChange,
                    $"{track.Type} {track.Ordinal} : « {track.Name} » vers « {name} »" + (defaultChange is { } d ? (d ? ", piste par défaut" : ", plus par défaut") : "")));
        }
        return edits;
    }

    /// <summary>Remplace les jetons puis retire les segments vides (séparateur « / ») : un jeton absent ne laisse pas de séparateur orphelin.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> tokens)
    {
        var segments = new List<string>();
        foreach (var segment in template.Split(" / "))
        {
            var text = Regex.Replace(segment, @"\{(\w+)\}", match => tokens.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length > 0) segments.Add(text);
        }
        return string.Join(" / ", segments);
    }

    private sealed record Track(int Id, string Type, int Ordinal, ulong Uid, string Language, string Name, bool Forced, bool HearingImpaired, bool Commentary, bool Default, int AudioChannels);

    private static List<Track> Tracks(JsonElement identification)
    {
        var result = new List<Track>();
        var ordinals = new Dictionary<string, int> { ["audio"] = 0, ["subtitles"] = 0 };
        if (identification.ValueKind != JsonValueKind.Object || !identification.TryGetProperty("tracks", out var tracks)) return result;
        foreach (var track in tracks.EnumerateArray())
        {
            var type = track.GetProperty("type").GetString() ?? "";
            if (!ordinals.TryGetValue(type, out var ordinal)) continue;
            ordinals[type] = ordinal + 1;
            var properties = track.GetProperty("properties");
            bool Flag(string key) => properties.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
            string Text(string key) => properties.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            var language = Text("language_ietf") is { Length: > 0 } ietf ? ietf : Text("language");
            result.Add(new(track.GetProperty("id").GetInt32(), type, ordinal, properties.GetProperty("uid").GetUInt64(), language, Text("track_name"),
                Flag("forced_track"), Flag("flag_hearing_impaired"), Flag("flag_commentary"), Flag("default_track"),
                properties.TryGetProperty("audio_channels", out var channels) && channels.ValueKind == JsonValueKind.Number ? channels.GetInt32() : 0));
        }
        return result;
    }

    private sealed record Source(string Name, bool Forced, bool HearingImpaired, bool Commentary);

    /// <summary>Titre et marqueurs d'origine quand la piste correspond (même type, même rang, même langue) ; sinon ceux de la piste actuelle. Les mots du titre complètent les indicateurs Matroska.</summary>
    private static Source Effective(Track track, IReadOnlyDictionary<(string, int), Track> originals)
    {
        var origin = originals.TryGetValue((track.Type, track.Ordinal), out var saved) && LanguageCode(saved.Language) == LanguageCode(track.Language) ? saved : null;
        var name = origin?.Name is { Length: > 0 } originalName ? originalName : track.Name;
        return new(name,
            track.Forced || origin?.Forced == true || ForcedWord.IsMatch(name),
            track.HearingImpaired || origin?.HearingImpaired == true || HearingWord.IsMatch(name),
            track.Commentary || origin?.Commentary == true || CommentaryWord.IsMatch(name));
    }

    private static Dictionary<(string, int), Track> OriginTracks(JsonElement? origin)
    {
        var result = new Dictionary<(string, int), Track>();
        if (origin is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in tracks.EnumerateArray())
        {
            string Text(string key) => item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            bool Flag(string key) => item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
            if (!item.TryGetProperty("ordinal", out var ordinal) || ordinal.ValueKind != JsonValueKind.Number) continue;
            result[(Text("type"), ordinal.GetInt32())] = new(0, Text("type"), ordinal.GetInt32(), 0, Text("language"), Text("name"), Flag("forced"),
                Flag("hearingImpaired"), Flag("commentary"), Flag("default"), 0);
        }
        return result;
    }

    private static string? LanguageCode(string language) => Languages.TryGetValue(language.Split('-')[0], out var known) ? known.Code : language.Length == 0 ? null : language.ToLowerInvariant();

    private static List<JsonElement> Streams(JsonElement probe, string type) =>
        probe.ValueKind == JsonValueKind.Object && probe.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array
            ? streams.EnumerateArray().Where(item => item.TryGetProperty("codec_type", out var kind) && kind.GetString() == type).ToList() : [];

    private static string CodecLabel(JsonElement stream, string type)
    {
        var codec = stream.TryGetProperty("codec_name", out var name) ? name.GetString()?.ToLowerInvariant() ?? "" : "";
        var profile = stream.TryGetProperty("profile", out var p) ? p.GetString() ?? "" : "";
        return codec switch
        {
            "ac3" => "AC3", "eac3" => "EAC3", "aac" => "AAC", "truehd" => "TrueHD", "flac" => "FLAC", "opus" => "Opus", "vorbis" => "Vorbis", "mp3" => "MP3",
            "dts" => profile.StartsWith("DTS-HD", StringComparison.OrdinalIgnoreCase) ? "DTS-HD" : "DTS",
            "hdmv_pgs_subtitle" => "PGS", "dvd_subtitle" => "VobSub", "ass" => "ASS", "ssa" => "SSA", "subrip" => "SRT", "webvtt" => "WebVTT", "mov_text" => "TX3G",
            _ when codec.StartsWith("pcm_", StringComparison.Ordinal) => "PCM",
            _ => codec.ToUpperInvariant()
        };
    }

    private static string LayoutLabel(JsonElement stream, int mkvChannels)
    {
        var channels = stream.TryGetProperty("channels", out var count) && count.ValueKind == JsonValueKind.Number ? count.GetInt32() : mkvChannels;
        var layout = AudioRulePlanner.RealLayout(stream.TryGetProperty("channel_layout", out var value) ? value.GetString() : null);
        if (layout == "mono") return "1.0";
        if (layout == "stereo") return "2.0";
        if (layout == "quad" || layout.StartsWith("quad(", StringComparison.Ordinal)) return "4.0";
        var match = Regex.Match(layout, @"^(\d+\.\d+)");
        if (match.Success) return match.Groups[1].Value;
        return channels switch { 1 => "1.0", 2 => "2.0", 6 => "5.1", 8 => "7.1", 0 => "", _ => channels + " canaux" };
    }
}
