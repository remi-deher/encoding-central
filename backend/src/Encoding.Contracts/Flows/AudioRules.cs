using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Encoding.Contracts.Executions;

namespace Encoding.Contracts.Flows;

/// <summary>Règle audio impossible à appliquer sans risque (disposition inconnue, non prise en charge par AC3…). Le bloc suit sa sortie « Erreur ».</summary>
public sealed class AudioRuleException(string message) : Exception(message);

/// <summary>Options du bloc <c>base-audio-rules</c> (architecture 0055).</summary>
public sealed record AudioRuleOptions(bool KeepStereoAc3 = true, bool InferUnknownLayout = true, int SampleRate = 48000, IReadOnlyList<JsonElement>? Rules = null, bool EnforceSampleRate = false)
{
    public static AudioRuleOptions From(IReadOnlyDictionary<string, JsonElement> configuration)
    {
        bool Flag(string key, bool fallback) => configuration.TryGetValue(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
        var rate = configuration.TryGetValue("sampleRate", out var sample) && sample.ValueKind == JsonValueKind.Number && sample.TryGetInt32(out var parsed) ? parsed : 48000;
        var unknown = configuration.TryGetValue("unknownLayout", out var mode) && mode.ValueKind == JsonValueKind.String ? mode.GetString() : "infer";
        var rules = configuration.TryGetValue("rules", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(item => item.Clone()).ToArray() : null;
        if (rules is not null)
            foreach (var rule in rules)
            {
                if (rule.ValueKind != JsonValueKind.Object) throw new AudioRuleException("Règle audio invalide.");
                var action = MediaTrackSelection.Text(rule, "action");
                if (action is not ("copy" or "convert")) throw new AudioRuleException("Choisissez une action audio explicite.");
                var minimum = MediaTrackSelection.Number(rule, "minChannels"); var maximum = MediaTrackSelection.Number(rule, "maxChannels");
                if (maximum > 0 && minimum > maximum) throw new AudioRuleException("Le minimum de canaux dépasse le maximum.");
                if (action == "copy") continue;
                var codec = MediaTrackSelection.Text(rule, "codec"); var bitrate = MediaTrackSelection.Number(rule, "bitrateKbps", 256);
                if (codec is not ("aac" or "ac3" or "opus" or "flac")) throw new AudioRuleException("Codec audio cible inconnu.");
                if (MediaTrackSelection.Number(rule, "targetChannels") is not (0 or 2 or 6)) throw new AudioRuleException("Limite de canaux : 0, 2 ou 6.");
                if (bitrate is < 32 or > 1536 || codec == "ac3" && bitrate > 640) throw new AudioRuleException("Débit audio invalide (AC-3 : 640 kbit/s maximum).");
            }
        return new(Flag("keepStereoAc3", true), unknown != "fail", rate, rules, configuration.TryGetValue("sampleRate", out var suppliedRate) && suppliedRate.ValueKind == JsonValueKind.Number);
    }
}

/// <summary>Décision pour une piste audio (indice parmi les pistes audio). <see cref="Convert"/> faux : la piste est conservée telle quelle.</summary>
public sealed record AudioTrackDecision(int AudioIndex, string SourceCodec, int SourceChannels, bool Convert, string? Codec = null,
    int? Channels = null, int? BitrateKbps = null, string? OutputLayout = null, string? InputLayout = null, string Note = "");

public sealed record AudioRulePlan(IReadOnlyList<AudioTrackDecision> Decisions)
{
    public IReadOnlyList<AudioTrackDecision> Conversions => Decisions.Where(item => item.Convert).ToArray();

    /// <summary>Étapes <c>base-transcode-audio</c> du plan FFmpeg commun : le bloc n'a pas de second chemin d'exécution.</summary>
    public IReadOnlyList<RuntimePlanStep> Steps(string? flowNodeId, AudioRuleOptions options) => Conversions.Select(item =>
    {
        var configuration = new Dictionary<string, JsonElement>
        {
            ["trackId"] = JsonSerializer.SerializeToElement(item.AudioIndex),
            ["audioCodec"] = JsonSerializer.SerializeToElement(item.Codec),
            ["bitrateKbps"] = JsonSerializer.SerializeToElement(item.BitrateKbps),
            ["channels"] = JsonSerializer.SerializeToElement(item.Channels!.Value.ToString(CultureInfo.InvariantCulture)),
            ["outputLayout"] = JsonSerializer.SerializeToElement(item.OutputLayout)
        };
        if (item.InputLayout is not null) configuration["inputLayout"] = JsonSerializer.SerializeToElement(item.InputLayout);
        if (options.SampleRate > 0) configuration["sampleRate"] = JsonSerializer.SerializeToElement(options.SampleRate);
        return new RuntimePlanStep("base-transcode-audio", configuration, flowNodeId);
    }).ToArray();
}

/// <summary>
/// Règles d'uniformisation audio : stéréo en AAC (une stéréo AC3 est conservée), 2.1 et plus en AC3 limité à 6 canaux,
/// mono conservé en AAC sinon converti en AC3. Une disposition de canaux inconnue n'est déduite que de pistes
/// déclarées concordantes ; sans référence unique, la conversion est refusée avant tout remplacement.
/// </summary>
public static class AudioRulePlanner
{
    private static readonly string[] Ac3Layouts = ["mono", "stereo", "3.0(back)", "3.0", "quad(side)", "quad", "4.0", "5.0(side)", "5.0",
        "2 channels (fc+lfe)", "2.1", "4 channels (fl+fr+lfe+bc)", "3.1", "4.1", "5.1(side)", "5.1"];
    private static readonly Regex StereoTitle = new(@"(^|[^a-z0-9])(stereo|stéréo|2[.]0)([^a-z0-9]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed class Track
    {
        public JsonElement Metadata; public int Index; public string Codec = ""; public int Channels; public string Layout = ""; public string Title = ""; public bool Inferred;
    }

    public static AudioRulePlan Plan(JsonElement metadata, AudioRuleOptions options)
    {
        var tracks = new List<Track>();
        if (metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            foreach (var stream in streams.EnumerateArray().Where(item => item.TryGetProperty("codec_type", out var type) && type.GetString() == "audio"))
            {
                var channels = stream.TryGetProperty("channels", out var count) && count.ValueKind == JsonValueKind.Number ? count.GetInt32() : 0;
                tracks.Add(new Track
                {
                    Metadata = stream.Clone(), Index = tracks.Count, Channels = channels,
                    Codec = (stream.TryGetProperty("codec_name", out var codec) ? codec.GetString() ?? "" : "").ToLowerInvariant(),
                    Layout = RealLayout(stream.TryGetProperty("channel_layout", out var layout) ? layout.GetString() : null),
                    Title = stream.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty("title", out var title) ? title.GetString() ?? "" : ""
                });
            }
        var decisions = new List<AudioTrackDecision>();
        foreach (var track in tracks)
        {
            JsonElement? rule = options.Rules?.Cast<JsonElement?>().FirstOrDefault(item => item is {} selected && MediaTrackSelection.Matches(track.Metadata, selected));
            if (options.Rules is not null && (rule is null || MediaTrackSelection.Text(rule.Value, "action") == "copy"))
            {
                decisions.Add(new(track.Index, track.Codec, track.Channels, false, Note: rule is null ? "aucune règle : piste conservée" : "copie demandée"));
                continue;
            }
            if (track.Channels < 1) throw new AudioRuleException($"Nombre de canaux audio inconnu : piste {track.Index}. Original non remplacé.");
            var target = rule is {} chosen ? MediaTrackSelection.Text(chosen, "codec") : track.Channels switch
            {
                2 => options.KeepStereoAc3 && track.Codec == "ac3" ? "ac3" : "aac",
                >= 3 => "ac3",
                _ => track.Codec == "aac" ? "aac" : "ac3"
            };
            var limit = rule is {} selectedRule ? MediaTrackSelection.Number(selectedRule, "targetChannels") : 0;
            var outputChannels = Math.Min(track.Channels, limit > 0 ? limit : track.Channels);
            if (target == "ac3") outputChannels = Math.Min(outputChannels, 6);
            var sampleRateMatches = options.SampleRate == 0 || rule is null && !options.EnforceSampleRate
                || track.Metadata.TryGetProperty("sample_rate", out var sampling) && int.TryParse(sampling.ToString(), out var sourceSampling) && sourceSampling == options.SampleRate;
            if (sampleRateMatches && track.Codec == target && track.Channels == outputChannels && (rule is null || target == "flac"
                || track.Metadata.TryGetProperty("bit_rate", out var sourceBitrate) && int.TryParse(sourceBitrate.ToString(), out var sourceRate)
                    && sourceRate == MediaTrackSelection.Number(rule.Value, "bitrateKbps") * 1000))
            {
                decisions.Add(new(track.Index, track.Codec, track.Channels, false, Note: "conforme"));
                continue;
            }
            if (!Infer(track, tracks, options)) throw new AudioRuleException($"Audio piste {track.Index} ({track.Codec}, {track.Channels} canaux) : disposition inconnue. Original non remplacé.");
            var (layout, channels) = outputChannels == 2 && track.Channels > 2 ? ("stereo", 2)
                : outputChannels == 6 && track.Channels > 6 ? (HasLfe(track.Layout) ? ("5.1", 6) : ("5.0", 5)) : TargetLayout(track, target);
            var bitrate = target == "aac" ? (track.Channels == 1 ? 128 : 256) : channels switch { 1 => 128, 2 => 256, 3 => 384, 4 => 448, _ => 640 };
            if (rule is {} bitrateRule) bitrate = MediaTrackSelection.Number(bitrateRule, "bitrateKbps", bitrate);
            decisions.Add(new(track.Index, track.Codec, track.Channels, true, target, channels, bitrate, layout, track.Inferred ? track.Layout : null,
                $"{track.Codec} {track.Channels} canaux vers {target.ToUpperInvariant()} {channels} canaux, {bitrate} kb/s" + (track.Inferred ? " (disposition déduite)" : "")));
        }
        return new(decisions);
    }

    internal static string RealLayout(string? value)
    {
        var layout = (value ?? "").Trim().ToLowerInvariant();
        return layout.Length == 0 || layout == "unknown" || Regex.IsMatch(layout, @"^\d+ channels?$") ? "" : layout;
    }

    internal static string LayoutKey(string layout)
    {
        var key = Regex.Replace(layout.ToLowerInvariant(), @"^(5\.[01])\(side\)$", "$1");
        key = Regex.Replace(key, @"^\d+ channels? \((.*)\)$", "$1");
        return Regex.Replace(Regex.Replace(key, @"\bsl\b", "bl"), @"\bsr\b", "br");
    }

    internal static bool HasLfe(string layout) => Regex.IsMatch(layout, @"lfe|\.1(?:\(|$)", RegexOptions.IgnoreCase);

    /// <summary>Déduit une disposition inconnue uniquement depuis des pistes déclarées (jamais depuis une piste déjà déduite).</summary>
    private static bool Infer(Track track, IReadOnlyList<Track> all, AudioRuleOptions options)
    {
        if (track.Layout.Length > 0) return true;
        if (!options.InferUnknownLayout) return false;
        if (track.Codec == "eac3" && track.Channels == 2) { Declare(track, "stereo"); return true; }
        if (track.Channels == 2 && StereoTitle.IsMatch(track.Title))
        {
            if (all.Any(other => other != track && !other.Inferred && other.Channels == 2 && other.Layout.Length > 0 && LayoutKey(other.Layout) != "stereo"))
                throw new AudioRuleException("Titre stéréo contradictoire avec la référence audio : conversion refusée.");
            Declare(track, "stereo"); return true;
        }
        var references = all.Where(other => other != track && !other.Inferred && other.Channels == track.Channels && other.Layout.Length > 0).ToArray();
        var keys = references.Select(other => LayoutKey(other.Layout)).Distinct().ToArray();
        if (keys.Length != 1)
            throw new AudioRuleException($"Audio piste {track.Index} ({track.Codec}, {track.Channels} canaux) : disposition inconnue, "
                + (references.Length > 0 ? "références contradictoires" : "aucune référence déclarée concordante") + ". Unification impossible : original non remplacé.");
        Declare(track, references[0].Layout);
        return true;
    }

    private static void Declare(Track track, string layout) { track.Layout = layout; track.Inferred = true; }

    private static (string Layout, int Channels) TargetLayout(Track track, string codec)
    {
        var layout = track.Layout;
        if (codec == "ac3" && track.Channels > 6) return HasLfe(layout) ? ("5.1", 6) : ("5.0", 5);
        if (codec == "ac3" && !Ac3Layouts.Contains(layout)) throw new AudioRuleException($"Disposition non prise en charge par AC3 : {layout}. Original conservé.");
        return (Regex.Replace(layout, @"^\d+ channels? \((.*)\)$", match => match.Groups[1].Value.ToUpperInvariant()), track.Channels);
    }
}
