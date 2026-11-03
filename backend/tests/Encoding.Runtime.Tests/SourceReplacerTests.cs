using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Contracts.Executions;
using Encoding.WorkerAgent.Runtime;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>
/// Remplacement de source : course avec un tiers (M3), reprise après coupure, idempotence. Uniquement des fichiers temporaires synthétiques ;
/// aucun média réel n'est jamais touché.
/// </summary>
public sealed class SourceReplacerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "encoding-replacer-" + Guid.NewGuid().ToString("N"));
    private readonly Guid execution = Guid.NewGuid();
    private static readonly DateTime Modified = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static Task<bool> Yes(CancellationToken _) => Task.FromResult(true);

    public SourceReplacerTests() => Directory.CreateDirectory(Path.Combine(root, "show"));
    public void Dispose() => Directory.Delete(root, true);

    private string Full(string relative) => Path.Combine(root, relative);
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private (string Source, string Result, byte[] SourceBytes, byte[] ResultBytes) Create(string name)
    {
        var source = System.Text.Encoding.UTF8.GetBytes("original-" + Guid.NewGuid());
        var result = System.Text.Encoding.UTF8.GetBytes("result-" + Guid.NewGuid());
        File.WriteAllBytes(Full("show/" + name), source); File.SetLastWriteTimeUtc(Full("show/" + name), Modified);
        var resultFile = Full(".encoding-results/" + execution.ToString("D") + "/result.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(resultFile)!); File.WriteAllBytes(resultFile, result);
        return ("show/" + name, resultFile, source, result);
    }

    private Task<SourceReplacementReport> ReplaceAsync((string Source, string Result, byte[] SourceBytes, byte[] ResultBytes) media, string extension,
        Func<CancellationToken, Task<bool>>? authorized = null) =>
        SourceReplacer.ReplaceAsync(root, execution, media.Source, media.Result, Sha(media.SourceBytes), Sha(media.ResultBytes), extension, authorized ?? Yes, CancellationToken.None);

    private RuntimeReplacementTask RollbackTask(string original, SourceReplacementReport report, byte[] source, byte[] result, string action = "rollback") =>
        new(execution, action, root, original, report.FinalPath!, report.BackupPath!, Sha(source), Sha(result));

    [Fact]
    public async Task ThirdPartyFileCreatedBeforePublicationIsNeverReplacedNorDeleted()
    {
        var media = Create("Old.avi");
        // Course M3 : un tiers crée le fichier final après le contrôle initial, juste avant la publication.
        var report = await ReplaceAsync(media, ".mkv", _ => { File.WriteAllText(Full("show/Old.mkv"), "third party"); return Task.FromResult(true); });

        Assert.Equal("failed", report.Status);
        Assert.Equal("third party", File.ReadAllText(Full("show/Old.mkv")));
        Assert.Equal(media.SourceBytes, File.ReadAllBytes(Full(media.Source)));
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
        Assert.False(File.Exists(Full(SourceReplacer.BackupPath(execution, media.Source))));
    }

    [Fact]
    public async Task ForcedRestoreOnlyDeletesTheFinalFileProducedByThisExecution()
    {
        var media = Create("Old.avi");
        var report = await ReplaceAsync(media, ".mkv");
        Assert.Equal("replaced", report.Status);
        // Un tiers remplace le résultat, l'original a disparu : une restauration forcée ne doit pas supprimer ce fichier.
        File.WriteAllText(Full("show/Old.mkv"), "third party"); File.Delete(Full(media.Source));

        var restored = await SourceReplacer.RestoreFilesAsync(root, media.Source, report.FinalPath!, report.BackupPath!, Sha(media.SourceBytes), Sha(media.ResultBytes), true, CancellationToken.None);

        Assert.Equal("restored", restored.Status);
        Assert.Equal(media.SourceBytes, File.ReadAllBytes(Full(media.Source)));
        Assert.Equal("third party", File.ReadAllText(Full("show/Old.mkv")));
    }

    [Fact]
    public async Task ForcedRestoreRemovesTheResultPublishedByThisExecution()
    {
        var media = Create("Old.avi");
        var report = await ReplaceAsync(media, ".mkv");

        var restored = await SourceReplacer.RestoreFilesAsync(root, media.Source, report.FinalPath!, report.BackupPath!, Sha(media.SourceBytes), Sha(media.ResultBytes), true, CancellationToken.None);

        Assert.Equal("restored", restored.Status);
        Assert.False(File.Exists(Full("show/Old.mkv")));
        Assert.Equal(media.SourceBytes, File.ReadAllBytes(Full(media.Source)));
    }

    [Fact]
    public async Task ForcedRestoreNeverOverwritesAThirdPartyFileAtTheOriginalName()
    {
        var media = Create("Same.mkv");
        var report = await ReplaceAsync(media, ".mkv");
        File.WriteAllText(Full(media.Source), "third party");

        await Assert.ThrowsAsync<InvalidOperationException>(() => SourceReplacer.RestoreFilesAsync(root, media.Source, report.FinalPath!, report.BackupPath!,
            Sha(media.SourceBytes), Sha(media.ResultBytes), true, CancellationToken.None));

        Assert.Equal("third party", File.ReadAllText(Full(media.Source)));
        Assert.True(File.Exists(Full(report.BackupPath!)));
    }

    [Fact]
    public async Task RollbackRefusesAndPreservesAFinalFileEditedAfterwards()
    {
        var media = Create("Old.avi");
        var report = await ReplaceAsync(media, ".mkv");
        File.AppendAllText(Full("show/Old.mkv"), "edited");

        var refused = await SourceReplacer.RollbackAsync(root, RollbackTask(media.Source, report, media.SourceBytes, media.ResultBytes), CancellationToken.None);

        Assert.Equal("failed", refused.Status);
        Assert.True(File.Exists(Full("show/Old.mkv")));
        Assert.True(File.Exists(Full(report.BackupPath!)));
    }

    [Fact]
    public async Task ReplacementIsIdempotentAndDoesNotRedoWork()
    {
        var media = Create("Old.avi");
        var first = await ReplaceAsync(media, ".mkv");
        var stamp = File.GetLastWriteTimeUtc(Full("show/Old.mkv"));

        var again = await ReplaceAsync(media, ".mkv", _ => throw new InvalidOperationException("Une reprise ne doit pas redemander d'autorisation."));

        Assert.Equal("replaced", again.Status);
        Assert.Equal(first.FinalPath, again.FinalPath);
        Assert.Equal(first.BackupPath, again.BackupPath);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Full("show/Old.mkv")));
        Assert.Equal(media.SourceBytes, File.ReadAllBytes(Full(first.BackupPath!)));
    }

    [Fact]
    public async Task RollbackIsIdempotent()
    {
        var media = Create("Old.avi");
        var report = await ReplaceAsync(media, ".mkv");
        var task = RollbackTask(media.Source, report, media.SourceBytes, media.ResultBytes);

        Assert.Equal("restored", (await SourceReplacer.RollbackAsync(root, task, CancellationToken.None)).Status);
        Assert.Equal("restored", (await SourceReplacer.RollbackAsync(root, task, CancellationToken.None)).Status);
        Assert.Equal(media.SourceBytes, File.ReadAllBytes(Full(media.Source)));
        Assert.False(File.Exists(Full("show/Old.mkv")));
    }

    [Fact]
    public async Task InterruptedAfterBackupBeforeAnyChangeIsResumedToCompletion()
    {
        var media = Create("Cut.mkv");
        var backup = Full(SourceReplacer.BackupPath(execution, media.Source));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(Full(media.Source), backup);

        Assert.Null(await SourceReplacer.ResumeAsync(root, execution, media.Source, Sha(media.SourceBytes), Sha(media.ResultBytes), ".mkv", CancellationToken.None));
        var report = await ReplaceAsync(media, ".mkv");

        Assert.Equal("replaced", report.Status);
        Assert.Equal(media.ResultBytes, File.ReadAllBytes(Full(media.Source)));
        Assert.Equal(media.SourceBytes, File.ReadAllBytes(backup));
    }

    [Fact]
    public async Task InterruptedAfterPublishingBeforeDeletingTheOldNameIsResumed()
    {
        var media = Create("Cut.avi");
        var backup = Full(SourceReplacer.BackupPath(execution, media.Source));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(Full(media.Source), backup);
        File.WriteAllBytes(Full("show/Cut.mkv"), media.ResultBytes); // résultat publié par l'exécution interrompue

        var report = await ReplaceAsync(media, ".mkv");

        Assert.Equal("replaced", report.Status);
        Assert.False(File.Exists(Full(media.Source)));
        Assert.Equal(media.ResultBytes, File.ReadAllBytes(Full("show/Cut.mkv")));
        Assert.Equal(media.SourceBytes, File.ReadAllBytes(backup));
    }

    [Fact]
    public async Task UnrelatedFileAtTheFinalNameIsNotTakenForAPublishedResult()
    {
        var media = Create("Cut.avi");
        var backup = Full(SourceReplacer.BackupPath(execution, media.Source));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(Full(media.Source), backup);
        File.WriteAllText(Full("show/Cut.mkv"), "third party");

        var report = await ReplaceAsync(media, ".mkv");

        Assert.Equal("failed", report.Status);
        Assert.Equal("third party", File.ReadAllText(Full("show/Cut.mkv")));
        Assert.Equal(media.SourceBytes, File.ReadAllBytes(Full(media.Source)));
    }

    [Fact]
    public async Task SourceIsKeptWhenLeaseIsLostOrDigestsDiffer()
    {
        var media = Create("Safe.mkv");

        Assert.Equal("failed", (await ReplaceAsync(media, ".mkv", _ => Task.FromResult(false))).Status);
        Assert.Equal("failed", (await SourceReplacer.ReplaceAsync(root, execution, media.Source, media.Result, new string('b', 64), Sha(media.ResultBytes), ".mkv", Yes, CancellationToken.None)).Status);
        Assert.Equal("failed", (await SourceReplacer.ReplaceAsync(root, execution, media.Source, media.Result, Sha(media.SourceBytes), new string('a', 64), ".mkv", Yes, CancellationToken.None)).Status);

        Assert.Equal(media.SourceBytes, File.ReadAllBytes(Full(media.Source)));
        Assert.False(File.Exists(Full(SourceReplacer.BackupPath(execution, media.Source))));
    }

    [Fact]
    public void FinalizeKeepsBackupWhenReplacementFileIsMissing()
    {
        var media = Create("Grace.mkv");
        var backup = Full(SourceReplacer.BackupPath(execution, media.Source));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(Full(media.Source), backup); File.Delete(Full(media.Source));
        var task = new RuntimeReplacementTask(execution, "finalize", root, media.Source, media.Source, SourceReplacer.BackupPath(execution, media.Source), Sha(media.SourceBytes), Sha(media.ResultBytes));

        Assert.False(SourceReplacer.Finalize(root, task, out var detail));
        Assert.NotNull(detail);
        Assert.True(File.Exists(backup));
    }
}
