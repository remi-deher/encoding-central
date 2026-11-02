namespace Encoding.Contracts.Executions;

/// <summary>Politique de bibliothèque : le remplacement du fichier source est désactivé tant qu'elle n'est pas activée explicitement.</summary>
public sealed record LibraryReplacementPolicy(bool Enabled, int GracePeriodHours, int Revision);
public sealed record SaveLibraryReplacementPolicy(bool Enabled, int GracePeriodHours, int ExpectedRevision);

/// <summary>Résultat du remplacement tenté par le nœud d'accès (<c>replaced</c> ou <c>failed</c> avec source intacte).</summary>
public sealed record SourceReplacementReport(string Status, string? FinalPath, string? BackupPath, long ByteLength, DateTimeOffset ModifiedAt, string? Detail);

public sealed record SourceReplacementResponse(Guid ExecutionId, string State, string OriginalPath, string? FinalPath, string? BackupPath,
    string SourceSha256, string ResultSha256, DateTimeOffset ReplacedAt, DateTimeOffset? GraceUntil, DateTimeOffset? FinishedAt, string? Detail);

/// <summary>Action demandée au nœud d'accès : <c>rollback</c> (restaurer la source) ou <c>finalize</c> (supprimer la sauvegarde après la période de grâce).</summary>
public sealed record RuntimeReplacementTask(Guid ExecutionId, string Action, string Root, string OriginalPath, string FinalPath, string BackupPath,
    string SourceSha256, string ResultSha256,bool SourceWriteGuardsSupported=false);
public sealed record ReportReplacementTask(string Outcome, string? Detail, long? ByteLength = null, DateTimeOffset? ModifiedAt = null);

/// <summary>A policy replacement deferred after the validated, non-overwriting publication.</summary>
public sealed record PendingSourceWrite(Guid ExecutionId,Guid ClaimId,string Root,string OriginalPath,string PublishedPath,
    string SourceSha256,string ResultSha256,string ResultExtension,int GraceHours,Guid? LibraryId=null,IReadOnlyList<RuntimePublicationGuard>? Guards=null);
public sealed record PendingSourceWriteFence(Guid ClaimId,string Root);
public sealed record PendingSourceWriteCompletion(Guid ClaimId,SourceReplacementReport? Report);

public sealed record SourceWriteAuthorization(long Epoch,string Root);
public sealed record SourceWriteLocation(string Root);

public static class SourceWriteProtocol
{
    public const string Capability="runtime/source-write-guard-v1";
}
