using System.Text.Json;

namespace Encoding.NodeAgent.Runtime.Media;

public sealed class MediaScanner(IProcessToolRunner tools)
{
    public async Task<JsonElement> ScanAsync(string path,CancellationToken ct)
    {
        var json=await tools.CheckedAsync("ffprobe",["-v","error","-show_format","-show_streams","-show_chapters","-of","json",path],Path.GetDirectoryName(path)!,ct);
        using var document=JsonDocument.Parse(json);
        if(!document.RootElement.TryGetProperty("streams",out var streams) || streams.GetArrayLength()==0) throw new IOException("Aucune piste média valide détectée.");
        var sanitized=System.Text.Json.Nodes.JsonNode.Parse(document.RootElement.GetRawText())!;
        if(sanitized["format"] is System.Text.Json.Nodes.JsonObject format) format["filename"]=Path.GetFileName(path);
        return JsonSerializer.SerializeToElement(sanitized);
    }
    public async Task<JsonElement> ValidateAsync(string candidate,string mediaType,JsonElement? before,CancellationToken ct)
    {
        if(!File.Exists(candidate) || new FileInfo(candidate).Length==0) throw new IOException("Le résultat média est vide.");
        var metadata=await ScanAsync(candidate,ct);
        var expected=mediaType=="music"?"audio":mediaType=="image"?"video":mediaType=="subtitles"?"subtitle":"video";
        if(!metadata.GetProperty("streams").EnumerateArray().Any(stream=>stream.TryGetProperty("codec_type",out var type)
            && type.GetString()==expected)) throw new IOException("Le résultat ne contient pas le média attendu.");
        if(mediaType is "video" or "music")
        {
            static double? Duration(JsonElement? value)=>value is { } json && json.TryGetProperty("format",out var format) && format.TryGetProperty("duration",out var duration)
                && double.TryParse(duration.GetString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var number)?number:null;
            var a=Duration(before); var b=Duration(metadata);
            if(a>0 && (b is null || Math.Abs(a.Value-b.Value)>Math.Max(2,a.Value*0.02))) throw new IOException("La durée du résultat est incompatible avec la source.");
        }
        return metadata;
    }
    /// <summary>Décodage complet des flux audio/vidéo (ADR 0007) : exigé une fois, juste avant la publication d'un résultat.</summary>
    public async Task DecodeAsync(string candidate,string mediaType,CancellationToken ct,bool decodeVideo=true,double? duration=null)
    {
        if(mediaType is not ("video" or "music")) return;
        var ffmpeg = tools.Find("ffmpeg") ?? throw new IOException("Outil indisponible : ffmpeg.");
        var dir = Path.GetDirectoryName(candidate)!;

        if (mediaType == "video" && !decodeVideo)
        {
            // La vidéo est copiée bit-à-bit : on décode intégralement l'audio (modifié),
            // et on échantillonne rapidement la vidéo aux points clés (début, milieu, fin)
            // pour garantir le démultiplexage et les trames clés sans décoder des dizaines de milliers d'images CPU.
            var audioResult = await tools.RunAsync(ffmpeg,
                ["-nostdin", "-v", "error", "-xerror", "-i", candidate, "-map", "0:a?", "-f", "null", "-"],
                dir, TimeSpan.FromHours(1), ct);
            if (audioResult.ExitCode != 0 || !string.IsNullOrWhiteSpace(audioResult.Error))
            {
                var detail = audioResult.Error.Trim(); if (detail.Length > 400) detail = detail[..400];
                throw new MediaToolException("ffmpeg (décodage audio de contrôle avant publication)", audioResult.ExitCode, detail);
            }

            var dur = duration;
            if (dur is null or <= 0)
            {
                var meta = await ScanAsync(candidate, ct);
                if (meta.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var dProp)
                    && double.TryParse(dProp.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dVal))
                    dur = dVal;
            }

            if (dur is { } length and > 0)
            {
                foreach (var at in new[] { 0d, Math.Max(0, length / 2 - 2), Math.Max(0, length - 5) }.Distinct())
                {
                    var sampleResult = await tools.RunAsync(ffmpeg,
                        ["-nostdin", "-v", "error", "-xerror", "-ss", at.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                         "-i", candidate, "-t", "5", "-map", "0:v?", "-fps_mode", "vfr", "-f", "null", "-"],
                        dir, TimeSpan.FromMinutes(1), ct);
                    if (sampleResult.ExitCode != 0 || !string.IsNullOrWhiteSpace(sampleResult.Error))
                    {
                        var detail = sampleResult.Error.Trim(); if (detail.Length > 400) detail = detail[..400];
                        throw new MediaToolException("ffmpeg (échantillonnage vidéo de contrôle avant publication)", sampleResult.ExitCode, detail);
                    }
                }
            }
            return;
        }

        var result=await tools.RunAsync(ffmpeg,
            ["-nostdin","-v","error","-xerror","-i",candidate,"-map","0:v?","-map","0:a?","-fps_mode","vfr","-f","null","-"],dir,TimeSpan.FromHours(6),ct);
        // ffmpeg 6.x sort en 0 sur une image corrompue ou un conteneur abîmé, même avec -xerror : avec « -v error », toute ligne d'erreur sur stderr est un échec.
        if(result.ExitCode!=0 || !string.IsNullOrWhiteSpace(result.Error))
        {
            var detail=result.Error.Trim(); if(detail.Length>400) detail=detail[..400];
            throw new MediaToolException("ffmpeg (décodage de contrôle avant publication)",result.ExitCode,detail);
        }
    }
}
