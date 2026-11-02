using System.Security.Cryptography;
using Encoding.Contracts.Executions;
using Encoding.NodeAgent.Runtime;

namespace Encoding.Runtime.Tests;

/// <summary>Garanties de fichiers du remplacement de source : sauvegarde vérifiée avant tout changement, restauration, reprise après perte du nœud d'accès.</summary>
internal static class SourceReplacementScenarios
{
    private static void Check(bool condition,string message){ if(!condition) throw new InvalidOperationException(message); }
    private static string Sha(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed class Library:IDisposable
    {
        public readonly string Root=Path.Combine(Path.GetTempPath(),"encoding-replace-"+Guid.NewGuid().ToString("N"));
        public readonly Guid Execution=Guid.NewGuid();
        public Library(){ Directory.CreateDirectory(Path.Combine(Root,"show")); }
        public string Path_(string relative)=>Path.Combine(Root,relative);
        public void Dispose(){ if(Directory.Exists(Root)) Directory.Delete(Root,true); }
        public (string Source,string Result,byte[] SourceBytes,byte[] ResultBytes) Write(string name,DateTime modified)
        {
            var source=System.Text.Encoding.UTF8.GetBytes("original-media-"+Guid.NewGuid()); var result=System.Text.Encoding.UTF8.GetBytes("encoded-result-"+Guid.NewGuid());
            File.WriteAllBytes(Path_("show/"+name),source); File.SetLastWriteTimeUtc(Path_("show/"+name),modified);
            var resultFile=Path_(".encoding-results/"+Execution.ToString("D")+"/result.mkv"); Directory.CreateDirectory(Path.GetDirectoryName(resultFile)!); File.WriteAllBytes(resultFile,result);
            return("show/"+name,resultFile,source,result);
        }
    }
    private static Task<bool> Yes(CancellationToken _)=>Task.FromResult(true);

    public static async Task RunAsync()
    {
        var ct=CancellationToken.None; var modified=new DateTime(2024,1,2,3,4,5,DateTimeKind.Utc);
        Check(SourceReplacer.FinalPath("show/Film.avi",".mkv")=="show/Film.mkv" && SourceReplacer.FinalPath("Film.mp4",".mp4")=="Film.mp4","The replacement keeps the media directory and base name.");
        Check(SourceReplacer.BackupPath(Guid.Empty,"show/Film.avi")==".encoding-replaced/00000000-0000-0000-0000-000000000000/original.avi","The backup path is deterministic.");

        using(var library=new Library())
        {
            var(source,result,sourceBytes,resultBytes)=library.Write("Film.mkv",modified);
            var report=await SourceReplacer.ReplaceAsync(library.Root,library.Execution,source,result,Sha(sourceBytes),Sha(resultBytes),".mkv",Yes,ct);
            Check(report.Status=="replaced" && report.FinalPath==source && report.ByteLength==resultBytes.Length,"Same-extension replacement succeeds.");
            Check(File.ReadAllBytes(library.Path_(source)).SequenceEqual(resultBytes),"The library file now holds the validated result.");
            var backup=library.Path_(report.BackupPath!);
            Check(File.ReadAllBytes(backup).SequenceEqual(sourceBytes) && File.GetLastWriteTimeUtc(backup)==modified,"The backup holds the exact original bytes and date.");
            Check(File.Exists(result),"The distinct published result stays in .encoding-results.");
            Check(!Directory.EnumerateFiles(library.Root,"*.tmp",SearchOption.AllDirectories).Any(),"No temporary file remains after replacement.");
            var again=await SourceReplacer.ReplaceAsync(library.Root,library.Execution,source,result,Sha(sourceBytes),Sha(resultBytes),".mkv",Yes,ct);
            Check(again.Status=="replaced" && again.FinalPath==report.FinalPath,"A repeated replacement (lost confirmation) is detected and acknowledged, not redone.");
            Check((await SourceReplacer.ResumeAsync(library.Root,library.Execution,source,Sha(sourceBytes),Sha(resultBytes),".mkv",ct)) is not null,"A node restart can resume a completed replacement.");

            var task=new RuntimeReplacementTask(library.Execution,"rollback",library.Root,source,source,report.BackupPath!,Sha(sourceBytes),Sha(resultBytes));
            File.AppendAllText(library.Path_(source),"edited by someone else");
            var refused=await SourceReplacer.RollbackAsync(library.Root,task,ct);
            Check(refused.Status=="failed" && File.Exists(backup),"A rollback refuses to overwrite a replacement edited afterwards, and keeps the backup.");
            File.WriteAllBytes(library.Path_(source),resultBytes);
            var restored=await SourceReplacer.RollbackAsync(library.Root,task,ct);
            Check(restored.Status=="restored" && File.ReadAllBytes(library.Path_(source)).SequenceEqual(sourceBytes) && File.GetLastWriteTimeUtc(library.Path_(source))==modified,"Rollback restores the original bytes and modification date.");
            Check(!File.Exists(backup) && !Directory.Exists(Path.GetDirectoryName(backup)!),"A completed rollback removes its backup.");
            Check((await SourceReplacer.RollbackAsync(library.Root,task,ct)).Status=="restored","A repeated rollback is idempotent.");
        }
        using(var library=new Library())
        {
            var(source,result,sourceBytes,resultBytes)=library.Write("Old.avi",modified);
            var report=await SourceReplacer.ReplaceAsync(library.Root,library.Execution,source,result,Sha(sourceBytes),Sha(resultBytes),".mkv",Yes,ct);
            Check(report.Status=="replaced" && report.FinalPath=="show/Old.mkv" && !File.Exists(library.Path_(source)) && File.Exists(library.Path_("show/Old.mkv")),"A changed container replaces the old name in the same directory.");
            var task=new RuntimeReplacementTask(library.Execution,"rollback",library.Root,source,report.FinalPath!,report.BackupPath!,Sha(sourceBytes),Sha(resultBytes));
            Check((await SourceReplacer.RollbackAsync(library.Root,task,ct)).Status=="restored" && File.ReadAllBytes(library.Path_(source)).SequenceEqual(sourceBytes) && !File.Exists(library.Path_("show/Old.mkv")),"Rollback restores the old name and removes the replacement.");
        }
        using(var library=new Library())
        {
            var(source,result,sourceBytes,resultBytes)=library.Write("Safe.mkv",modified);
            var denied=await SourceReplacer.ReplaceAsync(library.Root,library.Execution,source,result,Sha(sourceBytes),Sha(resultBytes),".mkv",_=>Task.FromResult(false),ct);
            Check(denied.Status=="failed" && File.ReadAllBytes(library.Path_(source)).SequenceEqual(sourceBytes),"A lost lease at the last check leaves the source untouched.");
            Check(!Directory.Exists(library.Path_(".encoding-replaced")) || !Directory.EnumerateFiles(library.Path_(".encoding-replaced"),"*",SearchOption.AllDirectories).Any(),"A refused replacement leaves no orphan backup.");
            var wrongResult=await SourceReplacer.ReplaceAsync(library.Root,library.Execution,source,result,Sha(sourceBytes),new string('a',64),".mkv",Yes,ct);
            Check(wrongResult.Status=="failed" && File.ReadAllBytes(library.Path_(source)).SequenceEqual(sourceBytes),"A result whose digest differs from the validated one never replaces the source.");
            var stale=await SourceReplacer.ReplaceAsync(library.Root,library.Execution,source,result,new string('b',64),Sha(resultBytes),".mkv",Yes,ct);
            Check(stale.Status=="failed" && File.ReadAllBytes(library.Path_(source)).SequenceEqual(sourceBytes),"A source that changed since analysis is not replaced.");
            File.WriteAllText(library.Path_("show/Safe.mp4"),"another file");
            var conflict=await SourceReplacer.ReplaceAsync(library.Root,library.Execution,source,result,Sha(sourceBytes),Sha(resultBytes),".mp4",Yes,ct);
            Check(conflict.Status=="failed" && File.ReadAllText(library.Path_("show/Safe.mp4"))=="another file","An existing file with the target name is never overwritten.");
        }
        using(var library=new Library())
        {
            // Crash simulation: node lost after the backup was published but before the library file changed.
            var(source,result,sourceBytes,resultBytes)=library.Write("Crash.mkv",modified);
            var backup=library.Path_(SourceReplacer.BackupPath(library.Execution,source)); Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(library.Path_(source),backup);
            File.WriteAllBytes(library.Path_("show/Crash.mkv.deadbeef.tmp"),[1,2,3]);
            Check((await SourceReplacer.ResumeAsync(library.Root,library.Execution,source,Sha(sourceBytes),Sha(resultBytes),".mkv",ct)) is null,"A backup alone is not a completed replacement.");
            var report=await SourceReplacer.ReplaceAsync(library.Root,library.Execution,source,result,Sha(sourceBytes),Sha(resultBytes),".mkv",Yes,ct);
            Check(report.Status=="replaced" && File.ReadAllBytes(library.Path_(source)).SequenceEqual(resultBytes),"A restarted node finishes a replacement interrupted after the backup.");
        }
        using(var library=new Library())
        {
            var(source,result,sourceBytes,resultBytes)=library.Write("Grace.mkv",modified);
            var report=await SourceReplacer.ReplaceAsync(library.Root,library.Execution,source,result,Sha(sourceBytes),Sha(resultBytes),".mkv",Yes,ct);
            var task=new RuntimeReplacementTask(library.Execution,"finalize",library.Root,source,source,report.BackupPath!,Sha(sourceBytes),Sha(resultBytes));
            File.Delete(library.Path_(source));
            Check(!SourceReplacer.Finalize(library.Root,task,out var detail) && detail is not null && File.Exists(library.Path_(report.BackupPath!)),"The backup is never purged when the replacement file is missing.");
            File.WriteAllBytes(library.Path_(source),resultBytes);
            Check(SourceReplacer.Finalize(library.Root,task,out _) && !File.Exists(library.Path_(report.BackupPath!)) && File.Exists(library.Path_(source)),"After the grace period the backup is removed and the replacement stays.");
        }
        Console.WriteLine("PASS: source replacement backup, atomic swap, rollback, interrupted-node resume and grace purge.");
    }
}
