using System.Globalization;
using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Executions;
using Encoding.NodeAgent.Services;
using Microsoft.Extensions.Logging;

namespace Encoding.NodeAgent.Runtime.Media;

public sealed partial class MediaActions
{
    /// <summary>Nomenclature des pistes (0056). Mode « snapshot » : retourne les titres d'origine sans toucher au fichier. Mode « apply » : mkvpropedit sur une copie de travail ; aucun fichier n'est produit quand rien ne change ou que le conteneur n'est pas Matroska.</summary>
    public async Task<MediaActionOutcome> TrackNamesAsync(IReadOnlyDictionary<string,JsonElement> c,JsonElement probe,JsonElement? origin,string current,
        string directory,string node,CancellationToken ct,bool editWorkingCopy=false,bool planOnly=false)
    {
        if(Path.GetExtension(current)!=".mkv") return new(null,[]);
        using var identification=JsonDocument.Parse(await tools.CheckedAsync("mkvmerge",["-J",current],directory,ct));
        if(Text(c,"mode","apply")=="snapshot") return new(null,[],TrackNaming.Snapshot(identification.RootElement).Clone());
        var edits=TrackNaming.Plan(identification.RootElement,probe,origin,TrackNameOptions.From(c));
        if(edits.Count==0) return new(null,[]);
        if(planOnly) return new(null,[],null,null,true);
        var canEditDirectly = editWorkingCopy || (Path.GetDirectoryName(current) == Path.GetFullPath(directory) && Path.GetFileName(current).StartsWith("out-",
            StringComparison.Ordinal) && new FileInfo(current).LinkTarget is null);
        var file = canEditDirectly ? (editWorkingCopy ? MutableAssemblyFile(current, directory) : current) : Output(directory, node, ".mkv");
        if (!canEditDirectly) File.Copy(current, file, true);
        var args = new List<string> { file };
        foreach(var edit in edits)
        {
            args.AddRange(["--edit","track:="+edit.Uid,"--set","name="+edit.Name]);
            if(edit.DefaultFlag is { } flag) args.AddRange(["--set","flag-default="+(flag?"1":"0")]);
        }
        await tools.CheckedAsync("mkvpropedit",args,directory,ct); return new(file,[],JsonSerializer.SerializeToElement(edits.Select(edit=>edit.Note).ToArray()));
    }

    /// <summary>Gestion des sous-titres (0057) : extrait les pistes texte, planifie (conversion SRT vers ASS, mise à niveau, doublons, forcés, titres, défaut) puis écrit un seul MKV avec mkvmerge. Les pistes non modifiées sont recopiées de la source ; rien n'est produit quand tout est déjà conforme.</summary>
    public async Task<MediaActionOutcome> SubtitleManageAsync(IReadOnlyDictionary<string,JsonElement> c,JsonElement probe,string current,string directory,
        string node,CancellationToken ct,bool planOnly=false)
    {
        if(Path.GetExtension(current)!=".mkv") return new(null,[]);
        var options=SubtitleOptions.From(c);
        var streams=probe.GetProperty("streams").EnumerateArray().ToArray();
        string Kind(JsonElement s)=>s.TryGetProperty("codec_type",out var t)?t.GetString()??"":"";
        string Tag(JsonElement s,string name)=>s.TryGetProperty("tags",out var tags)&&tags.ValueKind==JsonValueKind.Object&&tags.TryGetProperty(name,out var v)?v.GetString()??"":"";
        bool Disposition(JsonElement s,string name)=>s.TryGetProperty("disposition",out var d)&&d.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.Number&&v.GetInt32()==1;
        var subtitleStreams=streams.Where(s=>Kind(s)=="subtitle").ToArray();
        if(subtitleStreams.Length==0) return new(null,[]);
        using var identification=JsonDocument.Parse(await tools.CheckedAsync("mkvmerge",["-J",current],directory,ct));
        var allTracks=identification.RootElement.GetProperty("tracks").EnumerateArray().ToArray();
        var subtitleIds=allTracks.Where(t=>t.GetProperty("type").GetString()=="subtitles").Select(t=>t.GetProperty("id").GetInt32()).ToArray();
        if(subtitleIds.Length!=subtitleStreams.Length) throw new SubtitleException("Les pistes de sous-titres de FFprobe et de MKVToolNix ne correspondent pas : fichier laissé intact.");
        (int Width,int Height) canvas=(0,0);
        if(options.ApplyProfile)
        {
            var video=streams.FirstOrDefault(s=>Kind(s)=="video"&&!Disposition(s,"attached_pic"));
            if(video.ValueKind!=JsonValueKind.Object) throw new SubtitleException("Aucune piste vidéo : profil de sous-titres adaptatif impossible.");
            canvas=AssProfile.Canvas(video);
        }
        var work=Path.Combine(directory,"subtitles-"+Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(node)))[..16]);
        Directory.CreateDirectory(work);
        try
        {
            var raw=new List<(int Ordinal,int Id,string File,bool Srt)>(); var texts=new Dictionary<int,string>(); var empty=new HashSet<int>();
            for(var ordinal=0;ordinal<subtitleStreams.Length;ordinal++)
            {
                var stream=subtitleStreams[ordinal]; var codec=(stream.TryGetProperty("codec_name",out var name)?name.GetString()??"":"").ToLowerInvariant();
                if(!SubtitlePlanner.IsText(codec)) continue;
                if(codec is "subrip" or "srt" or "ass" or "ssa") raw.Add((ordinal,subtitleIds[ordinal],Path.Combine(work,
                    ordinal+(codec is "subrip" or "srt"?".srt":".ass")),codec is "subrip" or "srt"));
                else
                {
                    var file=Path.Combine(work,ordinal+".ass");
                    await tools.CheckedAsync("ffmpeg",["-nostdin","-hide_banner","-loglevel","error","-y","-i",current,"-map",
                        "0:"+stream.GetProperty("index").GetInt32(),"-c:s","ass",file],directory,ct);
                    texts[ordinal]=await File.ReadAllTextAsync(file,ct);
                }
            }
            if(raw.Count>0) await tools.CheckedAsync("mkvextract",["tracks",current,..raw.Select(item=>item.Id+":"+item.File)],directory,ct);
            foreach(var item in raw)
            {
                if(!File.Exists(item.File)) throw new SubtitleException($"Extraction de la piste {item.Ordinal} impossible : fichier laissé intact.");
                if(!item.Srt) { texts[item.Ordinal]=await File.ReadAllTextAsync(item.File,ct); continue; }
                if(SubtitlePlanner.SrtHasNoVisibleText(await File.ReadAllTextAsync(item.File,ct))) { empty.Add(item.Ordinal); texts[item.Ordinal]="[Events]\n"; continue; }
                var converted=Path.Combine(work,item.Ordinal+"-converted.ass");
                await tools.CheckedAsync("ffmpeg",["-nostdin","-hide_banner","-loglevel","error","-y","-i",item.File,"-c:s","ass",converted],directory,ct);
                texts[item.Ordinal]=await File.ReadAllTextAsync(converted,ct);
            }
            var tracks=new List<SubtitleTrack>();
            for(var ordinal=0;ordinal<subtitleStreams.Length;ordinal++)
            {
                var stream=subtitleStreams[ordinal]; var properties=allTracks.First(t=>t.GetProperty("id").GetInt32()==subtitleIds[ordinal]).GetProperty("properties");
                bool Property(string name)=>properties.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.True;
                tracks.Add(new(ordinal,stream.TryGetProperty("codec_name",out var codec)?codec.GetString()??"":"",Tag(stream,"language"),Tag(stream,"title"),Disposition(stream,"default"),
                    Disposition(stream,"forced")||Property("forced_track"),Disposition(stream,"hearing_impaired")||Property("flag_hearing_impaired"),
                    texts.TryGetValue(ordinal,out var text)?text:null,empty.Contains(ordinal)));
            }
            var plan=SubtitlePlanner.Plan(tracks,SubtitlePlanner.ReferenceAudioIsFrench(probe),canvas,options);
            var lines=plan.Notes.Concat(plan.Decisions.Select(d=>$"Sous-titre {d.Ordinal} : {(d.Drop?"retiré":d.ReplacementAss is not null?d.Note:"conservé")}, « {d.Title} »{(d.Default?", par défaut":"")}{(d.Forced?", forcé":"")}")).ToArray();
            if(!plan.Changed) return new(null,[],JsonSerializer.SerializeToElement(plan.Notes.ToArray()));
            if(planOnly) return new(null,[],null,null,true);
            var file2=Output(directory,node,".mkv"); var args=new List<string> { "-o",file2 };
            var kept=plan.Decisions.Where(d=>!d.Drop&&d.ReplacementAss is null).ToArray();
            if(kept.Length==0) args.Add("--no-subtitles"); else args.AddRange(["--subtitle-tracks",string.Join(',',kept.Select(d=>subtitleIds[d.Ordinal]))]);
            foreach(var d in kept)
            {
                var id=subtitleIds[d.Ordinal]; var original=tracks[d.Ordinal];
                if(d.Title!=original.Title&&d.Title.Length>0) args.AddRange(["--track-name",id+":"+d.Title]);
                if(d.Language!=original.Language&&d.Language.Length>0) args.AddRange(["--language",id+":"+d.Language]);
                args.AddRange(["--default-track-flag",id+":"+(d.Default?"1":"0"),"--forced-display-flag",id+":"+(d.Forced?"1":"0"),"--hearing-impaired-flag",id+":"+(d.HearingImpaired?"1":"0")]);
            }
            args.Add(current);
            var order=new List<string>(); var replacements=new Dictionary<int,int>(); var fileIndex=1;
            foreach(var d in plan.Decisions.Where(d=>!d.Drop&&d.ReplacementAss is not null))
            {
                var replacement=Path.Combine(work,"replacement-"+d.Ordinal+".ass");
                await File.WriteAllTextAsync(replacement,d.ReplacementAss!,new System.Text.UTF8Encoding(false),ct);
                if(d.Title.Length>0) args.AddRange(["--track-name","0:"+d.Title]);
                if(d.Language.Length>0&&d.Language!="und") args.AddRange(["--language","0:"+d.Language]);
                args.AddRange(["--default-track-flag","0:"+(d.Default?"1":"0"),"--forced-display-flag","0:"+(d.Forced?"1":"0"),"--hearing-impaired-flag","0:"+(d.HearingImpaired?"1":"0"),replacement]);
                replacements[d.Ordinal]=fileIndex++;
            }
            foreach(var track in allTracks)
            {
                var id=track.GetProperty("id").GetInt32();
                if(track.GetProperty("type").GetString()!="subtitles") { order.Add("0:"+id); continue; }
                var ordinal=Array.IndexOf(subtitleIds,id); var decision=plan.Decisions[ordinal];
                if(decision.Drop) continue;
                order.Add(replacements.TryGetValue(ordinal,out var index)?index+":0":"0:"+id);
            }
            args.AddRange(["--track-order",string.Join(',',order)]);
            await tools.CheckedAsync("mkvmerge",args,directory,ct);
            return new(file2,[],JsonSerializer.SerializeToElement(lines));
        }
        finally { Directory.Delete(work,true); }
    }

    private static bool HasProfileAss(JsonElement identification)
    {
        if(!identification.TryGetProperty("tracks",out var tracks)) return false;
        foreach(var track in tracks.EnumerateArray())
        {
            if(track.GetProperty("type").GetString()!="subtitles"||!track.TryGetProperty("properties",out var properties)) continue;
            var codecId=properties.TryGetProperty("codec_id",out var id)?id.GetString()??"":"";
            if(!codecId.StartsWith("S_TEXT/ASS",StringComparison.Ordinal)&&!codecId.StartsWith("S_TEXT/SSA",StringComparison.Ordinal)) continue;
            if(!properties.TryGetProperty("codec_private_data",out var hex)||hex.ValueKind!=JsonValueKind.String) continue;
            try { if(System.Text.Encoding.UTF8.GetString(Convert.FromHexString(hex.GetString()!)).Contains("SubtitleProfileVersion: "+AssProfile.Version,StringComparison.Ordinal)) return true; }
            catch(FormatException) { }
        }
        return false;
    }

    /// <summary>Polices (0057) : ajoute en pièces jointes les polices d'un jeu du dossier déclaré par le nœud (<c>ENCODING_FONT_DIR</c>) qui ne sont pas déjà dans le MKV. Jamais de chemin libre.</summary>
    public async Task<MediaActionOutcome> AddFontsAsync(IReadOnlyDictionary<string,JsonElement> c,string current,string directory,string node,
        CancellationToken ct,string? resourceFolder=null,bool editWorkingCopy=false)
    {
        if(Path.GetExtension(current)!=".mkv") return new(null,[]);
        var extensions = new[] { ".ttf", ".otf", ".ttc" };
        string folder,set; List<string> names;
        if(resourceFolder is not null)
        {
            // Ressource du catalogue (ADR 0006) : déjà téléchargée et vérifiée par le runner ; toutes ses polices sont ajoutées.
            folder=resourceFolder; set="ressource";
            names=Directory.EnumerateFiles(folder).Select(Path.GetFileName).Where(name=>extensions.Contains(Path.GetExtension(name)!.ToLowerInvariant())).Order(StringComparer.Ordinal).ToList()!;
            if(names.Count==0) throw new SubtitleException("La ressource ne contient aucune police.");
        }
        else
        {
        var root=Path.GetFullPath(Environment.GetEnvironmentVariable("ENCODING_FONT_DIR") is { Length: >0 } configured?configured:"/app/common/Fonts");
        set=Text(c,"fontSet","Crunchyroll");
        if(set.Length==0||!System.Text.RegularExpressions.Regex.IsMatch(set,@"^[A-Za-z0-9][A-Za-z0-9 ._-]*$")||set.Contains("..",
            StringComparison.Ordinal)) throw new SubtitleException("Nom de jeu de polices invalide.");
        folder=Path.GetFullPath(Path.Combine(root,set));
        if(!folder.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,
            StringComparison.Ordinal)||!Directory.Exists(folder)) throw new SubtitleException($"Jeu de polices « {set} » introuvable sur ce nœud.");
        names=Text(c,"fonts").Split([',','\n',';'],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).ToList();
        if(names.Count==0) names=Directory.EnumerateFiles(folder).Select(Path.GetFileName).Where(name=>extensions.Contains(Path.GetExtension(name)!.ToLowerInvariant())).Order(StringComparer.Ordinal).ToList()!;
        if(names.Count==0) throw new SubtitleException($"Le jeu de polices « {set} » ne contient aucune police.");
        }
        foreach(var name in names)
            if(name!=Path.GetFileName(name)||!extensions.Contains(Path.GetExtension(name).ToLowerInvariant())||!File.Exists(Path.Combine(folder,name)))
                throw new SubtitleException($"Police « {name} » introuvable dans le jeu « {set} ».");
        using var identification=JsonDocument.Parse(await tools.CheckedAsync("mkvmerge",["-J",current],directory,ct));
        // Comme FileFlows : les polices du profil ne servent qu'aux pistes ASS au profil courant ; sans elles (aucun sous-titre, bitmaps seuls, ASS personnalisés) le fichier reste intact.
        if(!HasProfileAss(identification.RootElement)) return new(null,[]);
        var existingFonts = await ExtractAndInspectExistingFontsAsync(identification.RootElement, current, directory, ct);
        var missing = new List<string>();
        var notes = new List<string>();

        foreach (var name in names)
        {
            var candidateFile = Path.Combine(folder, name);
            var candidateDesc = FontMatcher.Read(candidateFile, name);
            var existingMatch = existingFonts.FirstOrDefault(existing => FontMatcher.IsEquivalent(candidateDesc, existing));
            if (existingMatch is not null)
            {
                var note = $"Police « {name} » ignorée : déjà présente (« {existingMatch.FileName} »).";
                notes.Add(note);
                logger?.LogInformation("{Note}", note);
            }
            else
            {
                missing.Add(name);
                var note = $"Police « {name} » ajoutée ({candidateDesc.Size} octets).";
                notes.Add(note);
                logger?.LogInformation("{Note}", note);
            }
        }

        if (missing.Count == 0) return new(null, [], JsonSerializer.SerializeToElement(notes));
        var file = editWorkingCopy ? MutableAssemblyFile(current, directory) : Output(directory, node, ".mkv");
        var args = new List<string>(editWorkingCopy ? [file] : ["-o", file]);
        foreach (var name in missing) args.AddRange([editWorkingCopy ? "--add-attachment" : "--attach-file", Path.Combine(folder, name)]);
        if (!editWorkingCopy) args.Add(current);
        await tools.CheckedAsync(editWorkingCopy ? "mkvpropedit" : "mkvmerge", args, directory, ct);
        return new(file, [], JsonSerializer.SerializeToElement(notes));
    }

    private async Task<IReadOnlyList<FontDescriptor>> ExtractAndInspectExistingFontsAsync(JsonElement root, string current, string directory, CancellationToken ct)
    {
        if (!root.TryGetProperty("attachments", out var attachments)) return [];
        var extensions = new[] { ".ttf", ".otf", ".ttc" };
        var fontAttachments = attachments.EnumerateArray().Where(a =>
        {
            var fn = a.TryGetProperty("file_name", out var f) ? f.GetString() ?? "" : "";
            return extensions.Contains(Path.GetExtension(fn).ToLowerInvariant());
        }).ToArray();
        if (fontAttachments.Length == 0) return [];

        if (tools.Find("mkvextract") is not null)
        {
            var tempDir = Path.Combine(directory, "mkv-fonts-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempDir);
                var extractArgs = new List<string> { "attachments", current };
                var targets = new List<(JsonElement Attachment, string FilePath)>();
                foreach (var att in fontAttachments)
                {
                    var id = att.GetProperty("id").GetInt32();
                    var fn = att.GetProperty("file_name").GetString() ?? "";
                    var target = Path.Combine(tempDir, $"{id}_{fn}");
                    targets.Add((att, target));
                    extractArgs.Add($"{id}:{target}");
                }
                await tools.CheckedAsync("mkvextract", extractArgs, directory, ct);
                var result = new List<FontDescriptor>();
                foreach (var (att, target) in targets)
                {
                    var fn = att.GetProperty("file_name").GetString() ?? "";
                    var size = att.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    result.Add(File.Exists(target) ? FontMatcher.Read(target, fn, size) : FontMatcher.FromFileName(fn, size));
                }
                return result;
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Extraction des polices du MKV impossible, repli sur le nom normalisé.");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        return fontAttachments.Select(att =>
        {
            var fn = att.GetProperty("file_name").GetString() ?? "";
            var size = att.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            return FontMatcher.FromFileName(fn, size);
        }).ToArray();
    }
}
