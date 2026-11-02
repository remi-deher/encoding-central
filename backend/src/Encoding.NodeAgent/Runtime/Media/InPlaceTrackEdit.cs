using System.Text.Json;

namespace Encoding.NodeAgent.Runtime.Media;

/// <summary>Propriétés de piste et titre du segment appliqués sur place (architecture 0002) : arguments mkvpropedit et contrôles avant/après.</summary>
public sealed record InPlaceTrackEdit(int TrackId,string Language,string Name,bool Default,bool Forced,string? ContainerTitle)
{
    // mkvpropedit stocke les codes ISO 639-2/B ; mkvmerge les restitue sous cette forme (fra devient fre).
    private static readonly Dictionary<string,string> Bibliographic=new(StringComparer.OrdinalIgnoreCase)
    {
        ["fra"]="fre",["deu"]="ger",["nld"]="dut",["ces"]="cze",["zho"]="chi",["ell"]="gre",["fas"]="per",["isl"]="ice",["mkd"]="mac",["mya"]="bur",
        ["ron"]="rum",["slk"]="slo",["sqi"]="alb",["bod"]="tib",["cym"]="wel",["eus"]="baq",["hye"]="arm",["kat"]="geo",["mri"]="mao",["msa"]="may"
    };

    public static InPlaceTrackEdit From(IReadOnlyDictionary<string,JsonElement> c)
    {
        var title=MediaActions.Text(c,"containerTitle");
        return new(int.Parse(MediaActions.Number(c,"trackId"),System.Globalization.CultureInfo.InvariantCulture),MediaActions.Text(c,"language"),MediaActions.Text(c,"trackName"),
            MediaActions.Flag(c,"defaultTrack"),MediaActions.Flag(c,"forcedTrack"),title==""?null:title);
    }

    private static JsonElement? Find(JsonElement identification,int id)
    {
        foreach(var track in identification.GetProperty("tracks").EnumerateArray()) if(track.GetProperty("id").GetInt32()==id) return track;
        return null;
    }

    private static bool SameLanguage(JsonElement properties,string wanted)
    {
        if(wanted=="") return true;
        var stored=properties.TryGetProperty("language",out var l)?l.GetString():null; var ietf=properties.TryGetProperty("language_ietf",out var i)?i.GetString():null;
        var canonical=Bibliographic.GetValueOrDefault(wanted,wanted);
        return string.Equals(stored,canonical,StringComparison.OrdinalIgnoreCase)||string.Equals(stored,wanted,
            StringComparison.OrdinalIgnoreCase)||string.Equals(ietf,wanted,StringComparison.OrdinalIgnoreCase);
    }

    private static string? Check(InPlaceTrackEdit edit,JsonElement identification,bool containerToo)
    {
        if(Find(identification,edit.TrackId) is not { } track) return "La piste "+edit.TrackId+" est introuvable.";
        var p=track.GetProperty("properties");
        if(!SameLanguage(p,edit.Language)) return "La langue de la piste n'a pas été appliquée.";
        var name=p.TryGetProperty("track_name",out var n)?n.GetString()??"":"";
        if(name!=edit.Name) return "Le nom de la piste n'a pas été appliqué.";
        if((p.TryGetProperty("default_track",out var d)&&d.GetBoolean())!=edit.Default) return "Le drapeau par défaut n'a pas été appliqué.";
        if((p.TryGetProperty("forced_track",out var f)&&f.GetBoolean())!=edit.Forced) return "Le drapeau forcé n'a pas été appliqué.";
        if(containerToo&&edit.ContainerTitle is { } title)
        {
            var current=identification.GetProperty("container").TryGetProperty("properties",out var cp)&&cp.TryGetProperty("title",out var t)?t.GetString()??"":"";
            if(current!=title) return "Le titre du conteneur n'a pas été appliqué.";
        }
        return null;
    }

    public bool AlreadyApplied(JsonElement identification)=>Check(this,identification,true) is null;
    public string? Verify(JsonElement identification)=>Check(this,identification,true);

    public IReadOnlyList<string> Arguments(JsonElement identification)
    {
        var track=Find(identification,TrackId)??throw new InPlaceEditException("La piste "+TrackId+" est introuvable.");
        var args=new List<string> { "--edit","track:="+track.GetProperty("properties").GetProperty("uid").GetUInt64() };
        if(Language!="") args.AddRange(["--set","language="+Language]);
        args.AddRange(["--set","name="+Name,"--set","flag-default="+(Default?"1":"0"),"--set","flag-forced="+(Forced?"1":"0")]);
        if(ContainerTitle is { } title) args.AddRange(["--edit","info","--set","title="+title]);
        return args;
    }
}
