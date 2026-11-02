using System.Data.Common;
using Encoding.Api.Infrastructure.Data;

namespace Encoding.Api.Infrastructure;

/// <summary>
/// Traduit une violation d’unicité du stockage en réponse métier, pour que les routes ne dépendent pas des types de base de données.
/// </summary>
public static class UniqueConflict
{
    public static async Task<IResult> GuardAsync(Func<Task<IResult>> action, Func<IResult> onConflict)
    {
        try { return await action(); }
        catch (DbException exception) when (Sql.IsUniqueViolation(exception)) { return onConflict(); }
    }
}
