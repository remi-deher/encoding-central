namespace Encoding.Api.Infrastructure.Data.Sqlite;

/// <summary>
/// Écriture accessoire d'une requête de lecture (horodatage de dernière utilisation, prolongation de session) : tentée au mieux, sans jamais
/// faire échouer ni ralentir notablement la lecture. L'admission dans l'écrivain SQLite (<see cref="SqliteWriter"/>, attente jusqu'à
/// <see cref="SqliteWriterOptions.WaitTimeout"/>) est bornée par <see cref="Budget"/> ; une écriture non admise à temps ou refusée est abandonnée
/// et sera retentée à la requête suivante. Ne convient pas à une écriture dont dépend l'exactitude du résultat (#32).
/// </summary>
public static class BestEffortWrite
{
    /// <summary>Attente maximale ajoutée à une lecture quand l'écrivain est occupé.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);

    /// <summary>Exécute <paramref name="write"/> ; <c>false</c> si elle n'a pas pu se faire dans le budget ou a échoué (rien n'est propagé).</summary>
    public static async Task<bool> TryAsync(Func<CancellationToken, Task> write, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Budget);
        try
        {
            await write(limit.Token);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Écrivain occupé (SqliteWriteUnavailableException), budget dépassé (OperationCanceledException), base occupée (SQLITE_BUSY/LOCKED)
            // ou toute autre erreur : l'écriture accessoire est abandonnée, la lecture continue.
            return false;
        }
    }
}
