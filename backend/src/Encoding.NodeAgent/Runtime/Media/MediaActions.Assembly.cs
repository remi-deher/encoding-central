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
        if(!Path.GetExtension(current).Equals(".mkv",StringComparison.OrdinalIgnoreCase))
            throw new SubtitleException("L’assemblage MKV nécessite une entrée Matroska ; ajoutez le remuxage MKV au builder FFmpeg précédent.");
        var candidate=current;
        var notes=new List<string>();
        void Adopt(MediaActionOutcome outcome)
        {
            if(outcome.Primary is { } path) candidate=path;
            if(outcome.Value is { ValueKind:JsonValueKind.Array } values)
                notes.AddRange(values.EnumerateArray().Where(value=>value.ValueKind==JsonValueKind.String).Select(value=>value.GetString()!));
        }
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
        return new(candidate==current?null:candidate,[],JsonSerializer.SerializeToElement(notes));
    }
}
