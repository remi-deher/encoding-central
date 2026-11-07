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
    public static IReadOnlyList<string> PreservedDispositionArgs(IReadOnlyList<JsonElement> outputSources) =>
        PreservedDispositionArgs(outputSources, new Dictionary<int, bool>(), 0);

    /// <summary>
    /// Comme <see cref="PreservedDispositionArgs(IReadOnlyList{JsonElement})"/>, avec pour certaines pistes de sortie un drapeau « default »
    /// imposé par le bloc (les autres drapeaux de la source sont gardés) et <paramref name="addedStreams"/> pistes ajoutées à la fin
    /// (pistes d'un autre fichier, sans drapeau). Une seule option <c>-disposition</c> par piste : aucune ambiguïté d'ordre.
    /// </summary>
    public static IReadOnlyList<string> PreservedDispositionArgs(IReadOnlyList<JsonElement> outputSources, IReadOnlyDictionary<int, bool> defaultOverrides, int addedStreams)
    {
        var args = new List<string>((outputSources.Count + addedStreams) * 2);
        for (var i = 0; i < outputSources.Count; i++)
            args.AddRange(["-disposition:" + i.ToString(CultureInfo.InvariantCulture),
                DispositionValue(outputSources[i], defaultOverrides.TryGetValue(i, out var isDefault) ? isDefault : null)]);
        for (var i = 0; i < addedStreams; i++)
            args.AddRange(["-disposition:" + (outputSources.Count + i).ToString(CultureInfo.InvariantCulture), "0"]);
        return args;
    }

    /// <summary>Drapeaux actifs d'une piste ffprobe au format de l'option FFmpeg (<c>default+forced</c>), ou <c>0</c> si aucun.</summary>
    public static string DispositionValue(JsonElement stream) => DispositionValue(stream, null);

    /// <summary>Drapeaux de la source, avec « default » forcé à <paramref name="defaultFlag"/> quand il est donné.</summary>
    public static string DispositionValue(JsonElement stream, bool? defaultFlag)
    {
        var flags = new List<string>();
        if (stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("disposition", out var disposition) && disposition.ValueKind == JsonValueKind.Object)
            foreach (var property in disposition.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var value) && value != 0
                    && property.Name.Length > 0 && property.Name.All(character => character is >= 'a' and <= 'z' or '_'))
                    flags.Add(property.Name);
        if (defaultFlag is { } forced)
        {
            flags.Remove("default");
            if (forced) flags.Insert(0, "default");
        }
        return flags.Count == 0 ? "0" : string.Join('+', flags);
    }

    /// <summary>
    /// Le média est-il un conteneur ISO BMFF (MP4, MOV, M4A…) d'après ffprobe ? Le muxer FFmpeg « mov » active d'office la première piste
    /// de chaque type qui n'a aucune piste « default », même avec <c>-disposition 0</c> explicite (drapeau tkhd « enabled », relu comme
    /// « default ») : aucune option ne l'évite (constaté avec FFmpeg 6.1.1, #28).
    /// </summary>
    public static bool IsIsoMediaContainer(JsonElement metadata) =>
        metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.Object
        && format.TryGetProperty("format_name", out var name) && name.ValueKind == JsonValueKind.String
        && name.GetString()!.Split(',').Any(part => part is "mov" or "mp4" or "m4a" or "3gp" or "3g2" or "mj2");

    /// <summary>
    /// Drapeau « default » attendu en sortie pour la piste <paramref name="ordinal"/> d'un type, sachant les pistes source de ce type :
    /// celui de la source, sauf en sortie ISO BMFF où la première piste d'un type sans aucune piste « default » est activée par le muxer.
    /// </summary>
    public static bool ExpectedOutputDefault(IReadOnlyList<JsonElement> sourceOfType, int ordinal, bool isoMediaOutput)
    {
        var source = ordinal < sourceOfType.Count && DispositionValue(sourceOfType[ordinal]).Split('+').Contains("default");
        return source || isoMediaOutput && ordinal == 0 && !sourceOfType.Any(stream => DispositionValue(stream).Split('+').Contains("default"));
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
