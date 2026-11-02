using System.Text.Json;
using Encoding.Contracts.Flows;

namespace Encoding.NodeAgent.Runtime.Media;

public sealed partial class MediaActions
{
    // Only an output created by this assembly may be edited without making another copy.
    private static string MutableAssemblyFile(string current,string directory)
    {
        var root=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(!Path.GetFullPath(current).StartsWith(root,StringComparison.Ordinal)
            || !Path.GetFileName(current).StartsWith("out-",StringComparison.Ordinal)
            || new FileInfo(current).LinkTarget is not null)
            throw new IOException("Une modification d’assemblage exige une sortie temporaire privée.");
        return current;
    }

    /// <summary>One accepted media outcome/checkpoint; property edits reuse the private assembled file.</summary>
    public async Task<MediaActionOutcome> AssembleAsync(IReadOnlyDictionary<string,JsonElement> configuration,
        JsonElement metadata,JsonElement? origin,string current,string directory,string node,CancellationToken ct,string? resourceFolder=null)
    {
        var candidate=current;
        var notes=new List<string>();
        void Adopt(MediaActionOutcome outcome)
        {
            if(outcome.Primary is { } path) candidate=path;
            if(outcome.Value is { ValueKind:JsonValueKind.Array } values)
                notes.AddRange(values.EnumerateArray().Where(value=>value.ValueKind==JsonValueKind.String).Select(value=>value.GetString()!));
        }
        if(IsMatroska(candidate))
        {
            if(Flag(configuration,"manageSubtitles",false))
                Adopt(await SubtitleManageAsync(configuration,metadata,candidate,directory,node+"-subtitles",ct));
            if(Flag(configuration,"addFonts"))
                Adopt(await AddFontsAsync(configuration,candidate,directory,node+"-fonts",ct,resourceFolder,candidate!=current));
            if(Flag(configuration,"nameTracks",true))
            {
                // Subtitle replacement can change track count/order; never name using stale source streams.
                var observed=candidate==current?metadata:await scanner.ScanAsync(candidate,ct);
                Adopt(await TrackNamesAsync(configuration,observed,origin,candidate,directory,node+"-names",ct,candidate!=current));
            }
        }
        else if(IsMp4(candidate))
        {
            // Le conteneur de la source reste le conteneur du résultat. Le profil ASS et les polices jointes n'existent que dans un Matroska.
            if(Flag(configuration,"manageSubtitles",false))
                Adopt(await Mp4SubtitlesAsync(configuration,metadata,candidate,directory,node+"-subtitles",ct));
            if(Flag(configuration,"addFonts"))
                notes.Add("Le MP4 conserve son conteneur : les polices jointes sont propres au Matroska.");
            if(Flag(configuration,"nameTracks",true))
            {
                var observed=candidate==current?metadata:await scanner.ScanAsync(candidate,ct);
                Adopt(await Mp4TrackNamesAsync(configuration,observed,origin,candidate,directory,node+"-names",ct));
            }
        }
        else if(Flag(configuration,"manageSubtitles",false) || Flag(configuration,"addFonts") || Flag(configuration,"nameTracks",true))
            notes.Add("Le conteneur est conservé. L’assemblage détaillé modifie un Matroska ou un MP4.");
        return new(candidate==current?null:candidate,[],JsonSerializer.SerializeToElement(notes));
    }

    private static bool IsMatroska(string path) => Path.GetExtension(path).Equals(".mkv",StringComparison.OrdinalIgnoreCase);
    private static bool IsMp4(string path)
    {
        var extension=Path.GetExtension(path);
        return extension.Equals(".mp4",StringComparison.OrdinalIgnoreCase) || extension.Equals(".m4v",StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mov",StringComparison.OrdinalIgnoreCase);
    }

    private async Task<MediaActionOutcome> Mp4SubtitlesAsync(IReadOnlyDictionary<string,JsonElement> configuration,JsonElement probe,string current,
        string directory,string node,CancellationToken ct,bool planOnly=false)
    {
        var streams=probe.ValueKind==JsonValueKind.Object && probe.TryGetProperty("streams",out var list) && list.ValueKind==JsonValueKind.Array
            ? list.EnumerateArray().ToArray() : [];
        string Kind(JsonElement stream)=>stream.TryGetProperty("codec_type",out var kind)?kind.GetString()??"":"";
        string Tag(JsonElement stream,string name)=>stream.TryGetProperty("tags",out var tags)&&tags.ValueKind==JsonValueKind.Object&&tags.TryGetProperty(name,out var value)?value.GetString()??"":"";
        bool Mark(JsonElement stream,string name)=>stream.TryGetProperty("disposition",out var disposition)&&disposition.TryGetProperty(name,out var value)
            && value.ValueKind==JsonValueKind.Number&&value.GetInt32()==1;
        var subtitles=streams.Where(stream=>Kind(stream)=="subtitle").ToArray();
        if(subtitles.Length==0) return new(null,[]);
        var options=SubtitleOptions.From(configuration);
        var work=Path.Combine(directory,"subtitles-mp4-"+Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(node)))[..16]);
        Directory.CreateDirectory(work);
        try
        {
            var texts=new Dictionary<int,string>();
            for(var ordinal=0;ordinal<subtitles.Length;ordinal++)
            {
                var codec=(subtitles[ordinal].TryGetProperty("codec_name",out var name)?name.GetString()??"":"").ToLowerInvariant();
                if(!SubtitlePlanner.IsText(codec)) continue;
                var file=Path.Combine(work,ordinal+".ass");
                try
                {
                    await tools.CheckedAsync("ffmpeg",["-nostdin","-hide_banner","-loglevel","error","-y","-i",current,"-map","0:"+subtitles[ordinal].GetProperty("index").GetInt32(),"-c:s","ass",file],directory,ct);
                }
                catch(MediaToolException exception)
                {
                    throw new SubtitleException($"Extraction de la piste {ordinal} impossible : fichier laissé intact. {exception.Message}");
                }
                texts[ordinal]=await File.ReadAllTextAsync(file,ct);
            }
            var tracks=new List<SubtitleTrack>();
            for(var ordinal=0;ordinal<subtitles.Length;ordinal++)
            {
                var stream=subtitles[ordinal];
                tracks.Add(new(ordinal,stream.TryGetProperty("codec_name",out var codec)?codec.GetString()??"":"",Tag(stream,"language"),Tag(stream,"title"),
                    Mark(stream,"default"),Mark(stream,"forced"),Mark(stream,"hearing_impaired"),texts.TryGetValue(ordinal,out var text)?text:null));
            }
            (int Width,int Height) canvas=(0,0);
            if(options.ApplyProfile)
            {
                var video=streams.FirstOrDefault(stream=>Kind(stream)=="video"&&!Mark(stream,"attached_pic"));
                if(video.ValueKind!=JsonValueKind.Object) throw new SubtitleException("Aucune piste vidéo : profil de sous-titres adaptatif impossible.");
                canvas=AssProfile.Canvas(video);
            }
            var plan=SubtitlePlanner.Plan(tracks,SubtitlePlanner.ReferenceAudioIsFrench(probe),canvas,options);
            var notes=plan.Notes.ToList();
            var structural=plan.Decisions.Where(decision=>
            {
                var track=tracks[decision.Ordinal];
                return decision.Drop || decision.Title!=track.Title || decision.Language!=track.Language || decision.Default!=track.Default
                    || decision.Forced!=track.Forced || decision.HearingImpaired!=track.HearingImpaired;
            }).ToArray();
            if(plan.Decisions.Any(decision=>decision.ReplacementAss is not null))
                notes.Add("Le profil de sous-titres ASS n’est pas écrit dans le MP4 : le conteneur et le texte d’origine sont conservés.");
            if(structural.Length==0) return new(null,[],JsonSerializer.SerializeToElement(notes));
            if(planOnly) return new(null,[],null,null,true);
            var output=Output(directory,node,Path.GetExtension(current));
            var args=new List<string>{"-nostdin","-y","-v","error","-i",current,"-map_metadata","0","-map_chapters","0"};
            foreach(var stream in streams.Where(stream=>Kind(stream)!="subtitle"))
                args.AddRange(["-map","0:"+stream.GetProperty("index").GetInt32()]);
            var outputOrdinal=0;
            foreach(var decision in plan.Decisions)
            {
                if(decision.Drop) continue;
                args.AddRange(["-map","0:"+subtitles[decision.Ordinal].GetProperty("index").GetInt32()]);
                var specifier="s:s:"+outputOrdinal;
                if(decision.Language.Length>0) args.AddRange(["-metadata:"+specifier,"language="+decision.Language]);
                if(decision.Title.Length>0) args.AddRange(["-metadata:"+specifier,"title="+decision.Title]);
                var marks=new List<string>();
                if(decision.Default) marks.Add("default");
                if(decision.Forced) marks.Add("forced");
                if(decision.HearingImpaired) marks.Add("hearing_impaired");
                args.AddRange(["-disposition:s:"+outputOrdinal,marks.Count==0?"0":string.Join('+',marks)]);
                outputOrdinal++;
            }
            args.AddRange(["-c","copy",output]);
            await tools.CheckedAsync("ffmpeg",args,directory,ct);
            notes.AddRange(plan.Decisions.Select(decision=>$"Sous-titre {decision.Ordinal} : {(decision.Drop?"retiré":"conservé dans le MP4")}, « {decision.Title} »"));
            return new(output,[],JsonSerializer.SerializeToElement(notes));
        }
        finally { Directory.Delete(work,true); }
    }

    private async Task<MediaActionOutcome> Mp4TrackNamesAsync(IReadOnlyDictionary<string,JsonElement> configuration,JsonElement probe,JsonElement? origin,
        string current,string directory,string node,CancellationToken ct,bool planOnly=false)
    {
        if(probe.ValueKind!=JsonValueKind.Object || !probe.TryGetProperty("streams",out var streams) || streams.ValueKind!=JsonValueKind.Array) return new(null,[]);
        var identified=new List<object>();
        foreach(var stream in streams.EnumerateArray())
        {
            var type=stream.TryGetProperty("codec_type",out var kind)?kind.GetString():null;
            var trackType=type=="subtitle"?"subtitles":type;
            if(trackType is not ("audio" or "subtitles")) continue;
            string Tag(string name)=>stream.TryGetProperty("tags",out var tags)&&tags.ValueKind==JsonValueKind.Object&&tags.TryGetProperty(name,out var value)?value.GetString()??"":"";
            bool Mark(string name)=>stream.TryGetProperty("disposition",out var disposition)&&disposition.TryGetProperty(name,out var value)
                && value.ValueKind==JsonValueKind.Number&&value.GetInt32()==1;
            identified.Add(new {
                id=stream.GetProperty("index").GetInt32(), type=trackType,
                properties=new {
                    uid=(ulong)stream.GetProperty("index").GetInt32(), language=Tag("language"), track_name=Tag("title"),
                    forced_track=Mark("forced"), flag_hearing_impaired=Mark("hearing_impaired"), flag_commentary=Mark("comment"),
                    default_track=Mark("default"),
                    audio_channels=type=="audio" && stream.TryGetProperty("channels",out var channels)&&channels.TryGetInt32(out var count)?count:0
                }
            });
        }
        var identification=JsonSerializer.SerializeToElement(new { tracks=identified });
        if(Text(configuration,"mode","apply")=="snapshot") return new(null,[],TrackNaming.Snapshot(identification).Clone());
        var edits=TrackNaming.Plan(identification,probe,origin,TrackNameOptions.From(configuration));
        if(edits.Count==0) return new(null,[]);
        if(planOnly) return new(null,[],null,null,true);
        var output=Output(directory,node,Path.GetExtension(current));
        var args=new List<string>{"-nostdin","-y","-v","error","-i",current,"-map","0","-map_metadata","0","-c","copy"};
        foreach(var edit in edits)
        {
            var kind=edit.Type=="audio"?"a":"s";
            args.AddRange(["-metadata:s:"+kind+":"+edit.Ordinal,"title="+edit.Name]);
            if(edit.DefaultFlag is { } flag)
            {
                JsonElement? match=null;
                var seen=0;
                foreach(var stream in streams.EnumerateArray())
                {
                    var streamType=stream.TryGetProperty("codec_type",out var streamKind)?streamKind.GetString():null;
                    if((edit.Type=="audio" && streamType=="audio") || (edit.Type=="subtitles" && streamType=="subtitle"))
                    {
                        if(seen==edit.Ordinal) { match=stream; break; }
                        seen++;
                    }
                }
                bool On(string name)=>match is { } found && found.TryGetProperty("disposition",out var disposition)&&disposition.TryGetProperty(name,out var value)
                    && value.ValueKind==JsonValueKind.Number&&value.GetInt32()==1;
                var marks=new List<string>();
                if(flag) marks.Add("default");
                if(On("forced")) marks.Add("forced");
                if(On("hearing_impaired")) marks.Add("hearing_impaired");
                if(On("comment")) marks.Add("comment");
                args.AddRange(["-disposition:"+kind+":"+edit.Ordinal,marks.Count==0?"0":string.Join('+',marks)]);
            }
        }
        args.Add(output);
        await tools.CheckedAsync("ffmpeg",args,directory,ct);
        return new(output,[],JsonSerializer.SerializeToElement(edits.Select(edit=>edit.Note).ToArray()));
    }
}
