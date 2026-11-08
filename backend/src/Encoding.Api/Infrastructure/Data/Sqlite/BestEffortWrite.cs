namespace Encoding.Api.Infrastructure.Data.Sqlite;

/// <summary>
/// Écriture accessoire d'une requête de lecture (horodatage de dernière utilisation, prolongation de session) : tentée au mieux, sans jamais
/// faire échouer ni ralentir notablement la lecture. L'admission dans l'écrivain SQLite (<see cref="SqliteWriter"/>, attente jusqu'à
/// <see cref="SqliteWriterOptions.WaitTimeout"/>) est bornée par <see cref="Budget"/> ; une écriture non admise à temps ou refusée est abandonnée
/// et sera retentée à la requête suivante. Ne convient pas à une écriture dont dépend l'exactitude du résultat (#32).
/// Chaque abandon est compté par motif (<see cref="Abandoned"/>, exposé sur <c>/metrics</c>) ; aucun identifiant n'est journalisé.
/// </summary>
public static class BestEffortWrite
{
    /// <summary>Attente maximale ajoutée à une lecture quand l'écrivain est occupé.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);

    private static long busy, timeout, failed;

    /// <summary>Abandons depuis le démarrage : écrivain ou base occupés, budget dépassé, autre erreur.</summary>
    public static (long Busy, long Timeout, long Failed) Abandoned =>
        (Interlocked.Read(ref busy), Interlocked.Read(ref timeout), Interlocked.Read(ref failed));

    /// <summary>
    /// Exécute <paramref name="write"/> ; <c>false</c> si elle n'a pas pu se faire dans le budget ou a échoué.
    /// L'annulation de l'appelant (<paramref name="ct"/>, requête abandonnée) n'est PAS avalée : elle remonte comme
    /// <see cref="OperationCanceledException"/>, sans être comptée comme un abandon. Les autres erreurs, sauf <see cref="OutOfMemoryException"/>, sont avalées et comptées.
    /// </summary>
    public static async Task<bool> TryAsync(Func<CancellationToken, Task> write, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Budget);
        try
        {
            await write(limit.Token);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // la requête elle-même est annulée : ce n'est pas un abandon de l'écriture accessoire
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (exception is SqliteWriteUnavailableException or Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 }) Interlocked.Increment(ref busy);
            else if (exception is OperationCanceledException or TimeoutException) Interlocked.Increment(ref timeout);
            else Interlocked.Increment(ref failed);
            return false;
        }
    }
}
