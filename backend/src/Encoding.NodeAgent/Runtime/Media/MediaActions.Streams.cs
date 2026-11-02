using System.Globalization;
using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Executions;
using Encoding.NodeAgent.Services;
using Microsoft.Extensions.Logging;

namespace Encoding.NodeAgent.Runtime.Media;

public sealed partial class MediaActions
{
    public async Task<CropCoordinates?> DetectCropAsync(string current,string directory,int samplePoints,int sampleDurationSeconds,int threshold,
        int roundMultiple,double durationSeconds,CancellationToken ct)
    {
        var points=new List<double>();
        if(durationSeconds>10) points.AddRange([durationSeconds*0.1,durationSeconds*0.25,durationSeconds*0.5,durationSeconds*0.75,durationSeconds*0.9]);
        else points.Add(0);

        var lines=new List<string>();
        foreach(var p in points.Take(Math.Max(1,samplePoints)))
        {
            var args=new List<string> { "-nostdin","-v","error","-ss",p.ToString("F2",CultureInfo.InvariantCulture),"-i",current,"-frames:v","25","-vf",
                $"cropdetect={threshold}:16:0","-f","null","-" };
            var res=await tools.CheckedWithResultAsync("ffmpeg",args,directory,ct);
            if(!string.IsNullOrWhiteSpace(res.Error))
                lines.AddRange(res.Error.Split('\n',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries));
        }

        return CropDetector.CalculateConsensus(lines,roundMultiple);
    }

    public async Task<bool> DetectInterlacedAsync(string current,string directory,int frames,double thresholdPercent,CancellationToken ct)
    {
        var args=new List<string> { "-nostdin","-v","error","-i",current,"-frames:v",frames.ToString(CultureInfo.InvariantCulture),"-vf","idet","-f","null","-" };
        var res=await tools.CheckedWithResultAsync("ffmpeg",args,directory,ct);
        return DeinterlaceDetector.IsInterlaced(res.Error,thresholdPercent);
    }

    public async Task<MediaActionOutcome> FilterLanguagesAsync(IReadOnlyDictionary<string,JsonElement> c,JsonElement probe,string current,string directory,string node,CancellationToken ct)
    {
        var audioLangs=Text(c,"allowedAudioLanguages").Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        var subLangs=Text(c,"allowedSubtitleLanguages").Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        var keepUnd=Flag(c,"keepUndefinedAudio",true);
        var fallbackAudio=Flag(c,"keepFirstAudioIfNoneMatch",true);
        var keepForced=Flag(c,"keepForcedSubtitles",true);

        var streams=probe.GetProperty("streams");
        var removeAudio=LanguageFilterPlanner.IdentifyTracksToRemove(streams,"audio",audioLangs,keepUnd,fallbackAudio,keepForced);
        var removeSub=LanguageFilterPlanner.IdentifyTracksToRemove(streams,"subtitles",subLangs,keepUnd,fallbackAudio,keepForced);

        var toRemove=new HashSet<int>(removeAudio.Concat(removeSub));
        if(toRemove.Count==0) return new(null,[],JsonSerializer.SerializeToElement(new { filtered=false }));

        var file=Output(directory,node,Path.GetExtension(current));
        if(Path.GetExtension(current)==".mkv")
        {
            using var identification=JsonDocument.Parse(await tools.CheckedAsync("mkvmerge",["-J",current],directory,ct));
            var tracks=identification.RootElement.GetProperty("tracks").EnumerateArray().ToArray();
            var keptAudio=tracks.Where(t=>t.GetProperty("type").GetString()=="audio"&&!toRemove.Contains(t.GetProperty("id").GetInt32()))
                .Select(t=>t.GetProperty("id").GetInt32()).ToArray();
            var keptSub=tracks.Where(t=>t.GetProperty("type").GetString()=="subtitles"&&!toRemove.Contains(t.GetProperty("id").GetInt32()))
                .Select(t=>t.GetProperty("id").GetInt32()).ToArray();

            var mergeArgs=new List<string> { "-o",file };
            if(keptAudio.Length>0) mergeArgs.AddRange(["--audio-tracks",string.Join(',',keptAudio)]);
            else mergeArgs.Add("--no-audio");
            if(keptSub.Length>0) mergeArgs.AddRange(["--subtitle-tracks",string.Join(',',keptSub)]);
            else mergeArgs.Add("--no-subtitles");
            mergeArgs.Add(current);
            await tools.CheckedAsync("mkvmerge",mergeArgs,directory,ct);
        }
        else
        {
            var args=new List<string> { "-nostdin","-y","-v","error","-i",current,"-map","0:v" };
            foreach(var s in streams.EnumerateArray())
            {
                var idx=s.GetProperty("index").GetInt32();
                if(!toRemove.Contains(idx)) args.AddRange(["-map","0:"+idx]);
            }
            args.AddRange(["-c","copy",file]);
            await tools.CheckedAsync("ffmpeg",args,directory,ct);
        }

        return new(file,[],JsonSerializer.SerializeToElement(new { filtered=true,removedTracks=toRemove.ToArray() }));
    }

    public async Task<MediaActionOutcome> RemoveCommentaryAsync(IReadOnlyDictionary<string,JsonElement> c,JsonElement probe,string current,string directory,string node,CancellationToken ct)
    {
        var checkAudio=Flag(c,"removeAudioCommentary",true);
        var checkSub=Flag(c,"removeSubtitleCommentary",true);
        var custom=Text(c,"customKeywords").Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);

        var streams=probe.GetProperty("streams");
        var toRemove=CommentaryFilterPlanner.IdentifyCommentaryTracks(streams,checkAudio,checkSub,custom);
        if(toRemove.Count==0) return new(null,[],JsonSerializer.SerializeToElement(new { removed=false }));

        var file=Output(directory,node,Path.GetExtension(current));
        if(Path.GetExtension(current)==".mkv")
        {
            using var identification=JsonDocument.Parse(await tools.CheckedAsync("mkvmerge",["-J",current],directory,ct));
            var tracks=identification.RootElement.GetProperty("tracks").EnumerateArray().ToArray();
            var keptAudio=tracks.Where(t=>t.GetProperty("type").GetString()=="audio"&&!toRemove.Contains(t.GetProperty("id").GetInt32()))
                .Select(t=>t.GetProperty("id").GetInt32()).ToArray();
            var keptSub=tracks.Where(t=>t.GetProperty("type").GetString()=="subtitles"&&!toRemove.Contains(t.GetProperty("id").GetInt32()))
                .Select(t=>t.GetProperty("id").GetInt32()).ToArray();

            var mergeArgs=new List<string> { "-o",file };
            if(keptAudio.Length>0) mergeArgs.AddRange(["--audio-tracks",string.Join(',',keptAudio)]);
            if(keptSub.Length>0) mergeArgs.AddRange(["--subtitle-tracks",string.Join(',',keptSub)]);
            mergeArgs.Add(current);
            await tools.CheckedAsync("mkvmerge",mergeArgs,directory,ct);
        }
        else
        {
            var args=new List<string> { "-nostdin","-y","-v","error","-i",current,"-map","0:v" };
            foreach(var s in streams.EnumerateArray())
            {
                var idx=s.GetProperty("index").GetInt32();
                if(!toRemove.Contains(idx)) args.AddRange(["-map","0:"+idx]);
            }
            args.AddRange(["-c","copy",file]);
            await tools.CheckedAsync("ffmpeg",args,directory,ct);
        }

        return new(file,[],JsonSerializer.SerializeToElement(new { removed=true,removedTracks=toRemove.ToArray() }));
    }

    public async Task<JsonElement> ValidateOutputAsync(string current, string original, JsonElement before, JsonElement after,
        IReadOnlyDictionary<string, JsonElement> config, string directory, CancellationToken ct)
    {
        var issues = new List<string>();
        var sourceStreams = Streams(before).Where(stream => !IsAttachedPicture(stream)).ToArray();
        var outputStreams = Streams(after).Where(stream => !IsAttachedPicture(stream)).ToArray();
        var sourceVideo = sourceStreams.Where(stream => CodecType(stream) == "video").ToArray();
        var outputVideo = outputStreams.Where(stream => CodecType(stream) == "video").ToArray();
        var sourceAudio = sourceStreams.Where(stream => CodecType(stream) == "audio").ToArray();
        var outputAudio = outputStreams.Where(stream => CodecType(stream) == "audio").ToArray();
        var sourceSubtitles = sourceStreams.Where(stream => CodecType(stream) == "subtitle").ToArray();
        var outputSubtitles = outputStreams.Where(stream => CodecType(stream) == "subtitle").ToArray();

        ValidateVideo(sourceVideo, outputVideo, config, issues);
        ValidateDuration(before, after, config, issues);
        ValidateTrackCounts(sourceStreams, outputStreams, sourceSubtitles, outputSubtitles, config, issues);
        ValidateAudio(sourceAudio, outputAudio, config, issues);
        ValidateSubtitles(sourceSubtitles, outputSubtitles, config, issues);
        ValidateChapters(before, after, config, issues);
        ValidateSizeGrowth(before, after, config, issues);
        await ValidateAttachmentsAsync(original, current, directory, config, issues, ct);
        await ValidateAssEventsAsync(original, current, directory, sourceSubtitles, outputSubtitles, config, issues, ct);

        return JsonSerializer.SerializeToElement(new { valid = issues.Count == 0, issues });
    }

    private static void ValidateVideo(JsonElement[] sourceVideo, JsonElement[] outputVideo,
        IReadOnlyDictionary<string, JsonElement> config, List<string> issues)
    {
        var requiredCodec = Text(config, "requiredVideoCodec", "any");
        if (requiredCodec == "hevc-or-source")
        {
            for (var i = 0; i < outputVideo.Length; i++)
                if (CodecName(outputVideo[i]) != "hevc" && (i >= sourceVideo.Length || CodecName(outputVideo[i]) != CodecName(sourceVideo[i])))
                    issues.Add("Une piste vidéo n’est ni en HEVC ni conservée dans son codec source.");
        }
        else if (requiredCodec != "any" && outputVideo.Any(stream => !CodecName(stream).Equals(requiredCodec, StringComparison.OrdinalIgnoreCase)))
            issues.Add("Le codec vidéo ne correspond pas au codec demandé (" + requiredCodec + ").");
        if (outputVideo.Length == 0) issues.Add("Aucune piste vidéo exploitable n’est présente dans la sortie.");
    }

    private static void ValidateDuration(JsonElement before, JsonElement after,
        IReadOnlyDictionary<string, JsonElement> config, List<string> issues)
    {
        var sourceDuration = MetadataNumber(before, "format", "duration");
        var outputDuration = MetadataNumber(after, "format", "duration");
        var durationTolerance = (double)decimal.Parse(Number(config, "durationToleranceSeconds", 2), CultureInfo.InvariantCulture);
        if (sourceDuration is { } sourceLength && outputDuration is { } outputLength && Math.Abs(sourceLength - outputLength) > durationTolerance)
            issues.Add($"La durée diffère de {Math.Abs(sourceLength - outputLength):0.###} s (tolérance : {durationTolerance:0.###} s).");
        else if (sourceDuration is null || outputDuration is null) issues.Add("La durée source ou la durée de sortie est absente.");
    }

    private static void ValidateTrackCounts(JsonElement[] sourceStreams, JsonElement[] outputStreams,
        JsonElement[] sourceSubtitles, JsonElement[] outputSubtitles,
        IReadOnlyDictionary<string, JsonElement> config, List<string> issues)
    {
        if (Flag(config, "preserveTrackCounts"))
        {
            foreach (var type in new[] { "video", "audio" })
            {
                var expected = sourceStreams.Count(stream => CodecType(stream) == type);
                var actual = outputStreams.Count(stream => CodecType(stream) == type);
                if (expected != actual) issues.Add($"Le nombre de pistes {type} a changé ({expected} → {actual}).");
            }
        }
        if (Flag(config, "preserveSubtitleCount") && sourceSubtitles.Length != outputSubtitles.Length)
            issues.Add($"Le nombre de pistes de sous-titres a changé ({sourceSubtitles.Length} → {outputSubtitles.Length}).");
    }

    private static void ValidateAudio(JsonElement[] sourceAudio, JsonElement[] outputAudio,
        IReadOnlyDictionary<string, JsonElement> config, List<string> issues)
    {
        var allowedAudio = Csv(config, "allowedAudioCodecs");
        if (allowedAudio.Length > 0)
            foreach (var stream in outputAudio.Where(stream => !allowedAudio.Contains(CodecName(stream), StringComparer.OrdinalIgnoreCase)))
                issues.Add("Codec audio non autorisé : " + CodecName(stream) + ".");
        var forbiddenAudio = Csv(config, "forbiddenAudioCodecs");
        foreach (var stream in outputAudio.Where(stream => forbiddenAudio.Contains(CodecName(stream), StringComparer.OrdinalIgnoreCase)))
            issues.Add("Codec audio interdit dans la sortie : " + CodecName(stream) + ".");

        var maxChannels = int.Parse(Number(config, "maxAudioChannels", 0), CultureInfo.InvariantCulture);
        if (maxChannels > 0 && outputAudio.Any(stream => MetadataInt(stream, "channels") is { } channels && channels > maxChannels))
            issues.Add("Une piste audio dépasse la limite de " + maxChannels + " canaux.");
        if (Flag(config, "preserveAudioChannels") || Flag(config, "preserveAudioLayout"))
        {
            if (sourceAudio.Length != outputAudio.Length) issues.Add("Le nombre de pistes audio a changé.");
            for (var i = 0; i < Math.Min(sourceAudio.Length, outputAudio.Length); i++)
            {
                // Au-delà de la limite (6), la règle audio ramène 7.1 à 5.1 (5.0 sans LFE) : c'est le changement attendu, pas une dérive.
                var reduced = maxChannels >= 6 && MetadataInt(sourceAudio[i], "channels") is { } sourceChannels && sourceChannels > maxChannels;
                var expectedChannels = reduced ? (HasLfe(AudioLayout(sourceAudio[i])) ? 6 : 5) : MetadataInt(sourceAudio[i], "channels");
                if (Flag(config, "preserveAudioChannels") && expectedChannels != MetadataInt(outputAudio[i], "channels"))
                    issues.Add($"Le nombre de canaux de la piste audio {i + 1} a changé.");
                if (Flag(config, "preserveAudioLayout"))
                {
                    var sourceLayout = AudioLayout(sourceAudio[i]);
                    var outputLayout = AudioLayout(outputAudio[i]);
                    if (sourceLayout.Length > 0 && outputLayout.Length > 0)
                    {
                        if (!reduced && NormalizeLayout(sourceLayout) != NormalizeLayout(outputLayout))
                            issues.Add($"La disposition audio de la piste {i + 1} a changé ({sourceLayout} → {outputLayout}).");
                        if (HasLfe(sourceLayout) != HasLfe(outputLayout))
                            issues.Add($"Le canal LFE de la piste audio {i + 1} a changé.");
                    }
                }
            }
        }
    }

    private static void ValidateSubtitles(JsonElement[] sourceSubtitles, JsonElement[] outputSubtitles,
        IReadOnlyDictionary<string, JsonElement> config, List<string> issues)
    {
        var allowedSubtitles = Csv(config, "allowedSubtitleCodecs");
        if (allowedSubtitles.Length > 0)
            foreach (var stream in outputSubtitles.Where(stream => !allowedSubtitles.Contains(CodecName(stream), StringComparer.OrdinalIgnoreCase)))
                issues.Add("Codec de sous-titres non autorisé : " + CodecName(stream) + ".");

        if (Flag(config, "preserveSubtitleMetadata"))
        {
            if (sourceSubtitles.Length != outputSubtitles.Length) issues.Add("Le nombre de pistes de sous-titres a changé.");
            for (var i = 0; i < Math.Min(sourceSubtitles.Length, outputSubtitles.Length); i++)
            {
                var source = sourceSubtitles[i];
                var result = outputSubtitles[i];
                foreach (var (tag, label) in new[] { ("language", "langue"), ("title", "titre") })
                    if (Tag(source, tag) != Tag(result, tag)) issues.Add($"La {label} de la piste de sous-titres {i + 1} a changé.");
                foreach (var (flag, label) in new[] { ("default", "par défaut"), ("forced", "forcé"), ("hearing_impaired", "malentendant") })
                    if (Disposition(source, flag) != Disposition(result, flag)) issues.Add($"Le marqueur {label} de la piste de sous-titres {i + 1} a changé.");
            }
        }
    }

    private static void ValidateChapters(JsonElement before, JsonElement after,
        IReadOnlyDictionary<string, JsonElement> config, List<string> issues)
    {
        if (Flag(config, "preserveChapterCount"))
        {
            var sourceChapters = ChapterCount(before);
            var resultChapters = ChapterCount(after);
            if (sourceChapters is null || resultChapters is null) issues.Add("Les chapitres n’ont pas pu être comparés.");
            else if (sourceChapters != resultChapters) issues.Add($"Le nombre de chapitres a changé ({sourceChapters} → {resultChapters}).");
        }
    }

    private static void ValidateSizeGrowth(JsonElement before, JsonElement after,
        IReadOnlyDictionary<string, JsonElement> config, List<string> issues)
    {
        var maxGrowth = (double)decimal.Parse(Number(config, "maxOutputSizeGrowthPercent", 0), CultureInfo.InvariantCulture);
        var sourceSize = MetadataNumber(before, "format", "size");
        var outputSize = MetadataNumber(after, "format", "size");
        if (maxGrowth > 0)
        {
            if (sourceSize is > 0 && outputSize is { } size && ((size - sourceSize.Value) / sourceSize.Value * 100) > maxGrowth)
                issues.Add($"La sortie dépasse la taille source de plus de {maxGrowth:0.###} %.");
            else if (sourceSize is null || outputSize is null) issues.Add("La taille source ou la taille de sortie est absente.");
        }
    }

    private async Task ValidateAttachmentsAsync(string original, string current, string directory,
        IReadOnlyDictionary<string, JsonElement> config, List<string> issues, CancellationToken ct)
    {
        var requiredAttachments = Csv(config, "requiredAttachmentNames");
        if (Flag(config, "preserveOriginalAttachments") || requiredAttachments.Length > 0)
        {
            using var sourceIdentify = JsonDocument.Parse(await tools.CheckedAsync("mkvmerge", ["-J", original], directory, ct));
            using var outputIdentify = JsonDocument.Parse(await tools.CheckedAsync("mkvmerge", ["-J", current], directory, ct));
            var sourceAttachments = Attachments(sourceIdentify.RootElement);
            var outputAttachments = Attachments(outputIdentify.RootElement);
            if (Flag(config, "preserveOriginalAttachments"))
            {
                var remaining = outputAttachments.GroupBy(item => item, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
                foreach (var attachment in sourceAttachments)
                {
                    if (!remaining.TryGetValue(attachment, out var count) || count == 0)
                    {
                        issues.Add("Une pièce jointe d’origine est absente de la sortie : " + attachment + ".");
                        break;
                    }
                    remaining[attachment] = count - 1;
                }
            }
            foreach (var required in requiredAttachments)
                if (!outputAttachments.Any(item => item.Split('|')[0].Equals(required, StringComparison.OrdinalIgnoreCase)))
                    issues.Add("Pièce jointe requise absente : " + required + ".");
        }
    }

    private async Task ValidateAssEventsAsync(string original, string current, string directory,
        JsonElement[] sourceSubtitles, JsonElement[] outputSubtitles,
        IReadOnlyDictionary<string, JsonElement> config, List<string> issues, CancellationToken ct)
    {
        if (Flag(config, "compareAssEvents"))
        {
            var sourceText = sourceSubtitles.Where(IsTextSubtitle).ToArray();
            var outputText = outputSubtitles.Where(IsTextSubtitle).ToArray();
            if (sourceText.Length != outputText.Length) issues.Add("Le nombre de pistes de sous-titres texte a changé ; comparaison ASS impossible.");
            else for (var i = 0; i < sourceText.Length; i++)
            {
                var sourceEvents = await ExtractAssEventsAsync(tools, original, sourceText[i], directory, ct);
                var outputEvents = await ExtractAssEventsAsync(tools, current, outputText[i], directory, ct);
                if (!sourceEvents.SequenceEqual(outputEvents, StringComparer.Ordinal))
                {
                    issues.Add($"Les événements ASS de la piste de sous-titres {i + 1} ont changé.");
                    break;
                }
            }
        }
    }

    private static IEnumerable<JsonElement> Streams(JsonElement metadata)
    {
        if(metadata.TryGetProperty("streams",out var streams)&&streams.ValueKind==JsonValueKind.Array)
            return streams.EnumerateArray().ToArray();
        return [];
    }
    private static string CodecType(JsonElement stream)=>stream.TryGetProperty("codec_type",out var type)?type.GetString()??"":"";
    private static string CodecName(JsonElement stream)=>stream.TryGetProperty("codec_name",out var codec)?codec.GetString()??"":"";
    private static bool IsAttachedPicture(JsonElement stream)=>stream.TryGetProperty("disposition",
        out var disposition)&&disposition.TryGetProperty("attached_pic",out var cover)&&cover.ValueKind==JsonValueKind.Number&&cover.GetInt32()!=0;
    private static int? MetadataInt(JsonElement source,string name)=>source.TryGetProperty(name,out var value)&&value.TryGetInt32(out var number)?number:null;
    private static double? MetadataNumber(JsonElement source,string parent,string name)
    {
        if(!source.TryGetProperty(parent,out var group)||!group.TryGetProperty(name,out var value)) return null;
        if(value.ValueKind==JsonValueKind.Number&&value.TryGetDouble(out var number)) return number;
        if(value.ValueKind==JsonValueKind.String&&double.TryParse(value.GetString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var parsed)) return parsed;
        return null;
    }
    private static int? ChapterCount(JsonElement metadata)=>metadata.TryGetProperty("chapters",out var chapters)&&chapters.ValueKind==JsonValueKind.Array?chapters.GetArrayLength():null;
    private static string Tag(JsonElement stream,string name)=>stream.TryGetProperty("tags",out var tags)&&tags.TryGetProperty(name,out var value)?value.GetString()??"":"";
    private static bool Disposition(JsonElement stream,string name)=>stream.TryGetProperty("disposition",out var disposition)&&disposition.TryGetProperty(name,
        out var value)&&value.ValueKind==JsonValueKind.Number&&value.GetInt32()!=0;
    private static string AudioLayout(JsonElement stream)=>stream.TryGetProperty("channel_layout",out var layout)?layout.GetString()??"":"";
    private static string NormalizeLayout(string value)=>value.ToLowerInvariant().Replace("(side)","").Replace("sl","bl",StringComparison.Ordinal).Replace("sr","br",StringComparison.Ordinal);
    private static bool HasLfe(string value)=>value.Contains("lfe",StringComparison.OrdinalIgnoreCase)||System.Text.RegularExpressions.Regex.IsMatch(value,
        @"\.1(?:\(|$)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static string[] Csv(IReadOnlyDictionary<string,JsonElement> config,string key)=>Text(config,key).Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
    private static bool IsTextSubtitle(JsonElement stream)=>new[]{"ass","ssa","subrip","srt","webvtt","mov_text","text"}.Contains(CodecName(stream),StringComparer.OrdinalIgnoreCase);
    private static string[] Attachments(JsonElement identify)=>identify.TryGetProperty("attachments",out var attachments)&&attachments.ValueKind==JsonValueKind.Array
        ?attachments.EnumerateArray().Select(item=>string.Join('|',AttachmentField(item,"file_name","name"),AttachmentField(item,"content_type","mime_type"),
            AttachmentField(item,"size"))).ToArray():[];
    private static string AttachmentField(JsonElement item,string primary,string? alternate=null)=>item.TryGetProperty(primary,
        out var value)?value.ToString():alternate is not null&&item.TryGetProperty(alternate,out value)?value.ToString():"";
    private static async Task<string[]> ExtractAssEventsAsync(IProcessToolRunner tools,string media,JsonElement subtitle,string directory,CancellationToken ct)
    {
        var index=MetadataInt(subtitle,"index")??throw new MediaToolException("index de piste de sous-titres",1);
        var file=Path.Combine(directory,"validate-"+Guid.NewGuid().ToString("N")+".ass");
        try
        {
            await tools.CheckedAsync("ffmpeg",["-nostdin","-y","-v","error","-i",media,"-map","0:"+index,"-c:s","ass","-f","ass",file],directory,ct);
            var events=new List<string>();
            foreach(var line in await File.ReadAllLinesAsync(file,ct))
            {
                if(!line.StartsWith("Dialogue:",StringComparison.OrdinalIgnoreCase)) continue;
                var fields=line.Split(',',10);
                if(fields.Length<10) throw new IOException("Événement ASS mal formé pendant la validation.");
                events.Add(fields[1]+"|"+fields[2]+"|"+fields[9]);
            }
            return events.ToArray();
        }
        finally { if(File.Exists(file)) File.Delete(file); }
    }
}
