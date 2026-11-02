using Encoding.Contracts.Executions;
using Encoding.Core;

namespace Encoding.NodeAgent.Runtime;

/// <summary>
/// Remplacement explicite d'un fichier source par un résultat déjà validé et publié.
/// La source est d'abord copiée et vérifiée dans <c>.encoding-replaced/&lt;exécution&gt;/</c> ; elle n'est jamais supprimée avant
/// la sauvegarde et la copie temporaire du résultat. Toute étape ratée restaure la source. Les opérations sont reprenables (idempotentes).
/// </summary>
public static class SourceReplacer
{
    public const string BackupDirectory = ".encoding-replaced";
    public static string BackupPath(Guid executionId, string mediaPath) => $"{BackupDirectory}/{executionId:D}/original{Path.GetExtension(mediaPath)}";
    public static string FinalPath(string mediaPath, string resultExtension)
    {
        var slash = mediaPath.LastIndexOf('/');
        var name = Path.GetFileNameWithoutExtension(mediaPath);
        return (slash < 0 ? string.Empty : mediaPath[..(slash + 1)]) + name + resultExtension;
    }

    private static SourceReplacementReport Failed(string detail) => new("failed", null, null, 0, DateTimeOffset.UtcNow, detail);
    private static async Task CopyAsync(string source, string target, DateTime modifiedUtc, CancellationToken ct)
    {
        await using (var input = File.OpenRead(source))
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        { await input.CopyToAsync(output, ct); await output.FlushAsync(ct); output.Flush(true); }
        File.SetLastWriteTimeUtc(target, modifiedUtc);
    }
    private static SourceReplacementReport Replaced(string finalRelative, string backupRelative, string final)
    {
        var info = new FileInfo(final);
        return new("replaced", finalRelative, backupRelative, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), null);
    }

    /// <summary>Retourne le rapport si le remplacement avait déjà abouti (reprise après une perte avant la confirmation au serveur).</summary>
    public static async Task<SourceReplacementReport?> ResumeAsync(string root, Guid executionId, string mediaPath, string sourceSha, string resultSha, string resultExtension, CancellationToken ct)
    {
        var backupRelative = BackupPath(executionId, mediaPath); var finalRelative = FinalPath(mediaPath, resultExtension);
        var backup = SafeMediaPaths.Resolve(root, backupRelative); var final = SafeMediaPaths.Resolve(root, finalRelative);
        if (!File.Exists(backup) || !File.Exists(final)) return null;
        if (await SafeMediaPaths.DigestAsync(backup, ct) != sourceSha || await SafeMediaPaths.DigestAsync(final, ct) != resultSha) return null;
        var original = SafeMediaPaths.Resolve(root, mediaPath);
        if (original != final && File.Exists(original)) return null;
        return Replaced(finalRelative, backupRelative, final);
    }

    /// <param name="resultFile">Fichier local du résultat, déjà validé et dont l'empreinte est <paramref name="resultSha"/>.</param>
    /// <param name="stillAuthorized">Contrôle final du bail et de l'exécution juste avant de modifier la bibliothèque.</param>
    public static async Task<SourceReplacementReport> ReplaceAsync(string root, Guid executionId, string mediaPath, string resultFile, string sourceSha, string resultSha,
        string resultExtension, Func<CancellationToken, Task<bool>> stillAuthorized, CancellationToken ct)
    {
        if (await ResumeAsync(root, executionId, mediaPath, sourceSha, resultSha, resultExtension, ct) is { } resumed) return resumed;
        var original = SafeMediaPaths.Resolve(root, mediaPath);
        using var shared=SharedSourceWriteLock.Acquire(original);
        var finalRelative = FinalPath(mediaPath, resultExtension); var final = SafeMediaPaths.Resolve(root, finalRelative);
        var backupRelative = BackupPath(executionId, mediaPath); var backup = SafeMediaPaths.Resolve(root, backupRelative);
        if (!File.Exists(original) || await SafeMediaPaths.DigestAsync(original, ct) != sourceSha) return Failed("La source a changé ; elle est conservée.");
        if (original != final && File.Exists(final)) return Failed("Un autre fichier occupe déjà le nom du résultat ; la source est conservée.");
        var modified = File.GetLastWriteTimeUtc(original);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!); backup = SafeMediaPaths.Resolve(root, backupRelative);
        var stagedBackup = backup + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var stagedResult = final + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var mutated = false; var createdBackup = false; var success = false;
        try
        {
            if (!File.Exists(backup))
            {
                await CopyAsync(original, stagedBackup, modified, ct);
                if (await SafeMediaPaths.DigestAsync(stagedBackup, ct) != sourceSha) return Failed("La sauvegarde de la source est invalide ; la source est conservée.");
                AtomicFilePublication.Publish(stagedBackup, backup); createdBackup = true;
            }
            else if (await SafeMediaPaths.DigestAsync(backup, ct) != sourceSha) return Failed("Une sauvegarde différente existe déjà ; la source est conservée.");
            await CopyAsync(resultFile, stagedResult, DateTime.UtcNow, ct);
            if (await SafeMediaPaths.DigestAsync(stagedResult, ct) != resultSha) return Failed("La copie du résultat est invalide ; la source est conservée.");
            if (!await stillAuthorized(ct)) return Failed("Le bail ou l'exécution n'est plus valide ; la source est conservée.");
            if (await SafeMediaPaths.DigestAsync(original, ct) != sourceSha) return Failed("La source a changé avant le remplacement ; elle est conservée.");
            mutated = true;
            if (original == final) { File.Move(stagedResult, original, true); AtomicFilePublication.Confirm(original); }
            else { AtomicFilePublication.Publish(stagedResult, final); File.Delete(original); AtomicFilePublication.Confirm(final); }
            if (await SafeMediaPaths.DigestAsync(final, ct) != resultSha) throw new IOException("Le fichier remplaçant ne correspond pas au résultat validé.");
            success = true;
            return Replaced(finalRelative, backupRelative, final);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (mutated)
            {
                try { await RestoreFilesAsync(root, mediaPath, finalRelative, backupRelative, sourceSha, resultSha, true, ct); }
                catch (Exception) { return Failed("Remplacement interrompu ; la restauration automatique a échoué. Sauvegarde conservée dans " + backupRelative + "."); }
            }
            return Failed("Remplacement impossible (" + exception.GetType().Name + ") ; la source est conservée.");
        }
        finally
        {
            foreach (var temporary in new[] { stagedBackup, stagedResult }) if (File.Exists(temporary)) File.Delete(temporary);
            if (!success && createdBackup
                && !mutated) { File.Delete(backup); var directory = Path.GetDirectoryName(backup)!; if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
        }
    }

    /// <summary>Restaure la source depuis la sauvegarde. Refuse si le fichier remplaçant a été modifié depuis.</summary>
    public static async Task<SourceReplacementReport> RollbackAsync(string root, RuntimeReplacementTask task, CancellationToken ct,Func<CancellationToken,Task<bool>>? stillAuthorized=null)
    {
        using var shared=SharedSourceWriteLock.Acquire(SafeMediaPaths.Resolve(root,task.OriginalPath));
        try
        {
            var report = await RestoreFilesAsync(root, task.OriginalPath, task.FinalPath, task.BackupPath, task.SourceSha256, task.ResultSha256, false, ct,stillAuthorized);
            return report;
        }
        catch (InvalidOperationException exception) { return Failed(exception.Message); }
    }

    private static async Task<SourceReplacementReport> RestoreFilesAsync(string root, string mediaPath, string finalRelative, string backupRelative,
        string sourceSha, string resultSha, bool force, CancellationToken ct,Func<CancellationToken,Task<bool>>? stillAuthorized=null)
    {
        var original = SafeMediaPaths.Resolve(root, mediaPath); var final = SafeMediaPaths.Resolve(root, finalRelative); var backup = SafeMediaPaths.Resolve(root, backupRelative);
        if (!File.Exists(backup))
        {
            if (File.Exists(original) && await SafeMediaPaths.DigestAsync(original, ct) == sourceSha && (original == final || !File.Exists(final)))
                return Restored(mediaPath, original);
            throw new InvalidOperationException("La sauvegarde est absente ; restauration impossible.");
        }
        if (await SafeMediaPaths.DigestAsync(backup, ct) != sourceSha) throw new InvalidOperationException("La sauvegarde a été altérée ; restauration refusée.");
        if (!force && File.Exists(final) && await SafeMediaPaths.DigestAsync(final,
            ct) != resultSha) throw new InvalidOperationException("Le fichier remplaçant a été modifié depuis le remplacement ; restauration refusée.");
        if (!force && original != final && File.Exists(original) && await SafeMediaPaths.DigestAsync(original,
            ct) != sourceSha) throw new InvalidOperationException("Un autre fichier occupe l'emplacement d'origine ; restauration refusée.");
        var staged = original + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (!(original != final && File.Exists(original) && await SafeMediaPaths.DigestAsync(original, ct) == sourceSha))
            {
                await CopyAsync(backup, staged, File.GetLastWriteTimeUtc(backup), ct);
                if (await SafeMediaPaths.DigestAsync(staged, ct) != sourceSha) throw new InvalidOperationException("La copie restaurée est invalide.");
                if(stillAuthorized is not null && !await stillAuthorized(ct)) throw new SourceWriteDeferredException();
                if (original == final) File.Move(staged, original, true); else AtomicFilePublication.Publish(staged, original);
                AtomicFilePublication.Confirm(original);
            }
            if (original != final && File.Exists(final) && (force || await SafeMediaPaths.DigestAsync(final, ct) == resultSha)) File.Delete(final);
            File.Delete(backup);
            var directory = Path.GetDirectoryName(backup)!; if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            return Restored(mediaPath, original);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }
    private static SourceReplacementReport Restored(string mediaPath, string original)
    {
        var info = new FileInfo(original);
        return new("restored", mediaPath, null, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), null);
    }

    /// <summary>Supprime la sauvegarde après la période de grâce, seulement si le fichier remplaçant existe encore.</summary>
    public static bool Finalize(string root, RuntimeReplacementTask task, out string? detail)
    {
        using var shared=SharedSourceWriteLock.Acquire(SafeMediaPaths.Resolve(root,task.OriginalPath));
        var final = SafeMediaPaths.Resolve(root, task.FinalPath); var backup = SafeMediaPaths.Resolve(root, task.BackupPath);
        if (!File.Exists(final)) { detail = "Le fichier remplaçant est absent ; la sauvegarde est conservée."; return false; }
        if (File.Exists(backup)) File.Delete(backup);
        var directory = Path.GetDirectoryName(backup)!;
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        detail = null; return true;
    }
}
