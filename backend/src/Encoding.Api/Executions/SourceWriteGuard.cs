using System.Data.Common;
using Encoding.Api.Infrastructure.Data;

namespace Encoding.Api.Executions;

/// <summary>
/// Autorisation d'écrire sur une source. Le central est l'unique serveur ([ADR 0036](../../../../docs/architecture/adr/0036-separation-stricte-central-worker.md)) :
/// il n'y a plus de pair à attendre. Reste à vérifier que le worker qui écrit est admis, non en quarantaine et compatible.
/// L'exclusion sur le système de fichiers reste vérifiée par le worker qui écrit.
/// </summary>
public sealed class SourceWriteGuard(DbDataSource db)
{
    internal async Task<bool> NodeCanWriteAsync(Guid node,DbConnection c,DbTransaction? tx,CancellationToken ct)
    {
        await using var actor=Sql.Command("SELECT EXISTS(SELECT 1 FROM workers WHERE id=@node AND deleted_at IS NULL AND NOT quarantined AND incompatible_reason IS NULL)",c,tx);
        actor.Parameters.AddWithValue("node",node);
        return await actor.ScalarBoolAsync(ct);
    }

    internal async Task<bool> NodeCanWriteAsync(Guid node,CancellationToken ct)
    {
        await using var c=await db.OpenConnectionAsync(ct);
        return await NodeCanWriteAsync(node,c,null,ct);
    }
}
