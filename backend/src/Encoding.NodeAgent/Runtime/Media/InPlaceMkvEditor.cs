using System.Text.Json;
using Encoding.NodeAgent.Runtime;

namespace Encoding.NodeAgent.Runtime.Media;

public sealed class InPlaceEditException(string message) : Exception(message);

public sealed record InPlaceEditResult(string Status,string Detail,string? NewSha256=null);

/// <summary>
/// Modification d'un MKV de la bibliothèque sur place avec mkvpropedit (architecture 0002). La source n'est jamais modifiée sans
/// copie intégrale vérifiée dans <c>.encoding-replaced/&lt;exécution&gt;/</c> ; un écart de pistes, de pièces jointes, de chapitres
/// ou de durée, ou une propriété non appliquée, restaure le fichier d'origine. Une intention écrite avant la modification permet
/// de trancher une reprise après perte du nœud.
/// </summary>
public sealed class InPlaceMkvEditor(IProcessToolRunner tools)
{
    public const string IntentName="in-place.json";
    public const string BackupName="before-edit.mkv";
    private sealed record Snapshot(string[] Tracks,string[] Attachments,int Chapters,double Duration);
    private sealed record Intent(string State,string SourceSha256,long Size);

    public static string BackupDirectory(string root,Guid executionId)=>SafeMediaPaths.Resolve(root,SourceReplacer.BackupDirectory+"/"+executionId.ToString("D"));

    private async Task<(JsonDocument Document,Snapshot Snapshot)> IdentifyAsync(string file,string directory,CancellationToken ct)
    {
        var document=JsonDocument.Parse(await tools.CheckedAsync("mkvmerge",["-J",file],directory,ct)); var root=document.RootElement;
        var tracks=root.GetProperty("tracks").EnumerateArray().Select(t=>t.GetProperty("type").GetString()+":"+t.GetProperty("codec").GetString()).ToArray();
        var attachments=root.TryGetProperty("attachments",
            out var a)?a.EnumerateArray().Select(x=>x.GetProperty("file_name").GetString()+":"+x.GetProperty("size").GetInt64()).Order(StringComparer.Ordinal).ToArray():[];
        var chapters=root.TryGetProperty("chapters",out var c)?c.EnumerateArray().Sum(x=>x.TryGetProperty("num_entries",out var n)?n.GetInt32():0):0;
        var duration=root.GetProperty("container").TryGetProperty("properties",out var p)&&p.TryGetProperty("duration",out var d)?d.GetInt64()/1e9:0;
        return (document,new(tracks,attachments,chapters,duration));
    }

    private static string? Compare(Snapshot before,Snapshot after)
    {
        if(!before.Tracks.SequenceEqual(after.Tracks)) return "Pistes ou codecs modifiés.";
        if(!before.Attachments.SequenceEqual(after.Attachments)) return "Pièces jointes modifiées.";
        if(before.Chapters!=after.Chapters) return "Chapitres modifiés.";
        if(Math.Abs(before.Duration-after.Duration)>0.5) return "Durée modifiée.";
        return null;
    }

    private static async Task WriteIntentAsync(string directory,string state,string sha,long size,CancellationToken ct)
    {
        var path=Path.Combine(directory,IntentName); var temporary=path+".tmp";
        await File.WriteAllTextAsync(temporary,JsonSerializer.Serialize(new Intent(state,sha,size)),ct); File.Move(temporary,path,true);
    }

    private static async Task RestoreAsync(string original,string backup,string sha,DateTime modified,CancellationToken ct)
    {
        var staged=original+"."+Guid.NewGuid().ToString("N")+".restore";
        try
        {
            File.Copy(backup,staged,true);
            if(await SafeMediaPaths.DigestAsync(staged,CancellationToken.None)!=sha) throw new IOException("La sauvegarde ne correspond plus à la source d'origine.");
            File.Move(staged,original,true); File.SetLastWriteTimeUtc(original,modified);
        }
        finally { if(File.Exists(staged)) File.Delete(staged); }
    }

    /// <param name="editArguments">Arguments mkvpropedit placés après le fichier (<c>--edit … --set …</c>), construits depuis <c>mkvmerge -J</c> avant modification.</param>
    /// <param name="expectedChange">Rejoue sur la sortie de <c>mkvmerge -J</c> après modification ; retourne un message si la propriété attendue est absente.</param>
    /// <param name="beforeWrite">Dernier contrôle après la sauvegarde et avant l'écriture ; retourne un message pour refuser, null pour continuer.</param>
    /// <param name="alreadyApplied">Évalué sur la sortie de <c>mkvmerge -J</c> avant modification ; vrai si rien ne doit changer.</param>
    public async Task<InPlaceEditResult> ApplyAsync(string root,string lockDirectory,Guid executionId,string mediaPath,string expectedSha,Func<JsonElement,IReadOnlyList<string>> editArguments,
        Func<JsonElement,bool> alreadyApplied,Func<JsonElement,string?> expectedChange,string workDirectory,bool keepBackup,Func<bool> stillAuthorized,
            CancellationToken ct,Func<CancellationToken,Task<string?>>? beforeWrite=null)
    {
        var original=SafeMediaPaths.Resolve(root,mediaPath);
        if(!string.Equals(Path.GetExtension(original),".mkv",StringComparison.OrdinalIgnoreCase)) throw new InPlaceEditException("Seuls les fichiers MKV peuvent être modifiés sur place.");
        if(!File.Exists(original)) throw new InPlaceEditException("La source est introuvable sur ce nœud.");
        IDisposable mediaLock;
        try { mediaLock=SafeMediaPaths.Lock(original,lockDirectory); }
        catch(SafeMediaPaths.MediaLockBusyException) { throw new InPlaceEditException("Le média est utilisé par un autre traitement ; la source n'a pas été modifiée."); }
        using(mediaLock)
        {
            IDisposable sourceLock;
            try {sourceLock=SharedSourceWriteLock.Acquire(original);}
            catch(SafeMediaPaths.MediaLockBusyException) {throw new InPlaceEditException("Une écriture utilise déjà ce média ; la source n’a pas été modifiée.");}
            using var shared=sourceLock;
            if(await SafeMediaPaths.DigestAsync(original,ct)!=expectedSha) throw new InPlaceEditException("La source a changé depuis le scan ; elle n'a pas été modifiée.");
            var (beforeDocument,before)=await IdentifyAsync(original,workDirectory,ct);
            IReadOnlyList<string> arguments;
            using(beforeDocument)
            {
                if(alreadyApplied(beforeDocument.RootElement)) return new("unchanged","Les propriétés demandées sont déjà en place.");
                arguments=editArguments(beforeDocument.RootElement);
            }
            var directory=BackupDirectory(root,executionId); Directory.CreateDirectory(directory);
            var backup=Path.Combine(directory,BackupName); var size=new FileInfo(original).Length; var modified=File.GetLastWriteTimeUtc(original);
            File.Copy(original,backup,true);
            if(await SafeMediaPaths.DigestAsync(backup,ct)!=expectedSha) { File.Delete(backup);
                throw new InPlaceEditException("La sauvegarde de la source est invalide ; la source n'a pas été modifiée."); }
            if(!stillAuthorized()) { File.Delete(backup); throw new InPlaceEditException("Le bail n'est plus valide ; la source n'a pas été modifiée."); }
            // Dernier contrôle juste avant d'écrire : la sauvegarde peut durer, une lecture a pu démarrer entre-temps.
            if(beforeWrite is not null && await beforeWrite(ct) is { } refusal) { File.Delete(backup); throw new InPlaceEditException(refusal+" La source n'a pas été modifiée."); }
            await WriteIntentAsync(directory,"started",expectedSha,size,ct);
            string? failure=null;
            try
            {
                await tools.CheckedAsync("mkvpropedit",[original,..arguments],workDirectory,ct);
                var (afterDocument,after)=await IdentifyAsync(original,workDirectory,ct);
                using(afterDocument) failure=Compare(before,after)??expectedChange(afterDocument.RootElement);
            }
            catch(Exception exception) when(exception is not OperationCanceledException) { failure="mkvpropedit a échoué ("+exception.GetType().Name+")."; }
            catch(OperationCanceledException)
            {
                // Annulation en cours d'écriture : l'état du fichier est incertain, on le rétablit avant de propager.
                await RestoreAsync(original,backup,expectedSha,modified,CancellationToken.None); await WriteIntentAsync(directory,"restored",expectedSha,size,CancellationToken.None);
                throw;
            }
            if(failure is not null)
            {
                try { await RestoreAsync(original,backup,expectedSha,modified,CancellationToken.None); await WriteIntentAsync(directory,"restored",expectedSha,size,CancellationToken.None); }
                catch(Exception exception) { throw new InPlaceEditException(failure+" Restauration impossible ("+exception.GetType().Name+") : sauvegarde conservée dans "+SourceReplacer.BackupDirectory+"/"+executionId.ToString("D")+".");
                    }
                throw new InPlaceEditException(failure+" Fichier d'origine restauré.");
            }
            var sha=await SafeMediaPaths.DigestAsync(original,ct);
            await WriteIntentAsync(directory,"done",expectedSha,size,ct);
            if(!keepBackup) { File.Delete(backup); File.Delete(Path.Combine(directory,IntentName)); if(!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
            return new("edited","Propriétés appliquées sur place ; contrôle des pistes, pièces jointes, chapitres et durée réussi.",sha);
        }
    }
}
