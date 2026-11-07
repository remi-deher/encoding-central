using System.Globalization;
using System.Text.Json;
using Encoding.Contracts.Executions;

namespace Encoding.WorkerAgent.Runtime.Media;

/// <summary>
/// Conservation des dispositions des pistes (default, forced, hearing_impaired…) dans les commandes FFmpeg qui copient des pistes.
/// La CLI FFmpeg copie la disposition de chaque piste source, puis, si AUCUNE option <c>-disposition</c> n'est donnée pour le fichier de
/// sortie, marque d'office « default » la première piste de chaque type présent en plusieurs exemplaires quand aucune ne l'est
/// (fftools, <c>set_dispositions</c>). Une source dont toutes les pistes sont à default=0 ressortait donc avec le premier audio et le
/// premier sous-titre à default=1 (banc NAS #18, défaut #24). Écrire explicitement la disposition source de chaque piste de sortie
/// désactive cette inférence et rend la sortie identique à la source.
/// </summary>
public sealed partial class MediaActions
{
    /// <summary>
    /// Arguments <c>-disposition:&lt;i&gt; &lt;drapeaux&gt;</c> pour chaque piste de sortie, dans l'ordre de sortie ; <paramref name="outputSources"/>
    /// donne, pour la piste de sortie i, la piste source ffprobe dont elle est la copie.
    /// </summary>
    public static IReadOnlyList<string> PreservedDispositionArgs(IReadOnlyList<JsonElement> outputSources)
    {
        var args = new List<string>(outputSources.Count * 2);
        for (var i = 0; i < outputSources.Count; i++)
            args.AddRange(["-disposition:" + i.ToString(CultureInfo.InvariantCulture), DispositionValue(outputSources[i])]);
        return args;
    }

    /// <summary>Drapeaux actifs d'une piste ffprobe au format de l'option FFmpeg (<c>default+forced</c>), ou <c>0</c> si aucun.</summary>
    public static string DispositionValue(JsonElement stream)
    {
        var flags = new List<string>();
        if (stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("disposition", out var disposition) && disposition.ValueKind == JsonValueKind.Object)
            foreach (var property in disposition.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var value) && value != 0
                    && property.Name.Length > 0 && property.Name.All(character => character is >= 'a' and <= 'z' or '_'))
                    flags.Add(property.Name);
        return flags.Count == 0 ? "0" : string.Join('+', flags);
    }

    /// <summary>Le plan FFmpeg copie-t-il toutes les pistes de l'entrée (<c>-map 0</c>) ? Sinon, seules la vidéo et les pistes audio traitées.</summary>
    private static bool MapsWholeInput(IReadOnlyList<RuntimePlanStep> plan, bool preserve) => preserve || !plan.Any(p => p.ComponentId is
        "base-transcode-video" or "video-transcode" or
        "base-transcode-audio" or "audio-transcode" or
        "base-normalize-audio" or "audio-normalize" or
        "base-resize-video" or "video-resize" or
        "base-auto-crop" or "video-auto-crop" or
        "base-deinterlace-video" or "video-deinterlace");

    private static bool MapsVideo(IReadOnlyList<RuntimePlanStep> plan) => plan.Any(p => p.ComponentId is
        "base-transcode-video" or "video-transcode" or
        "base-resize-video" or "video-resize" or
        "base-auto-crop" or "video-auto-crop" or
        "base-deinterlace-video" or "video-deinterlace");

    private static IEnumerable<string> MappedAudioTracks(IReadOnlyList<RuntimePlanStep> plan) => plan
        .Where(p => p.ComponentId is "base-transcode-audio" or "audio-transcode" or "base-normalize-audio" or "audio-normalize")
        .Select(p => Number(p.Configuration, "trackId")).Distinct();

    /// <summary>
    /// Pistes source de chaque piste de sortie du plan FFmpeg, dans l'ordre de <see cref="ConfigureStreamMapping"/> ; <c>null</c> si
    /// l'entrée ne les décrit pas ou si une piste demandée n'existe pas (FFmpeg échouera alors de lui-même).
    /// </summary>
    internal static IReadOnlyList<JsonElement>? PlannedOutputSources(IReadOnlyList<RuntimePlanStep> plan, JsonElement metadata, bool preserve)
    {
        if (metadata.ValueKind != JsonValueKind.Object || !metadata.TryGetProperty("streams", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var streams = list.EnumerateArray().ToArray();
        if (MapsWholeInput(plan, preserve)) return streams;
        var sources = new List<JsonElement>();
        if (MapsVideo(plan)) sources.AddRange(streams.Where(stream => CodecType(stream) == "video"));
        var audio = streams.Where(stream => CodecType(stream) == "audio").ToArray();
        foreach (var track in MappedAudioTracks(plan))
        {
            if (!int.TryParse(track, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal) || ordinal < 0 || ordinal >= audio.Length) return null;
            sources.Add(audio[ordinal]);
        }
        return sources;
    }
}
