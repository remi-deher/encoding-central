using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using Encoding.Api.Executions;

namespace Encoding.Api.Infrastructure.Operations;

public sealed record RetentionResult(int Candidates, int Purged, long BytesFreed, int TempFilesRemoved, bool DryRun);

/// <summary>
/// Supprime du volume partagé les artefacts d’exécutions terminées depuis plus longtemps que la rétention.
/// Les lignes d’historique et empreintes restent dans la base locale ; seule la marque <c>artifacts_purged_at</c> est ajoutée.
/// Les fichiers publiés dans les bibliothèques ne sont jamais concernés.
/// </summary>
public sealed class ArtifactRetention(DbDataSource dataSource, RuntimeArtifactStore store, OperationsOptions options, ILogger<ArtifactRetention> logger)
{
    private const string CandidateSql = """
        SELECT e.id
        FROM executions e
        JOIN execution_work w ON w.execution_id = e.id
        WHERE w.artifacts_purged_at IS NULL
          AND e.finished_at IS NOT NULL
          AND NOT EXISTS(SELECT 1 FROM task_quarantines q WHERE q.execution_id=e.id)
          AND NOT EXISTS(SELECT 1 FROM task_transfers t WHERE t.execution_id=e.id AND t.state IN ('proposed','received'))
          AND NOT EXISTS(SELECT 1 FROM pending_source_writes p WHERE p.execution_id=e.id AND p.state IN ('waiting','claimed'))
          AND ((e.state IN ('succeeded','cancelled') AND e.finished_at < clock_timestamp() - make_interval(days => @days))
            OR (e.state = 'failed' AND e.finished_at < clock_timestamp() - make_interval(days => @failedDays)))
          AND NOT (e.state = 'succeeded' AND w.publication_requested AND w.published_path IS NULL)
          AND NOT EXISTS (SELECT 1 FROM media_reservations r WHERE r.execution_id = e.id)
        ORDER BY e.finished_at, e.id
        LIMIT @batch
        FOR UPDATE OF w SKIP LOCKED
        """;

    public async Task<RetentionResult> RunOnceAsync(CancellationToken cancellationToken)
    {

        var settings = options.ArtifactRetention;
        var ids = new List<Guid>();
        int purged = 0; long freed = 0;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var select = Sql.Command(CandidateSql, connection, transaction))
        {
            select.Parameters.AddWithValue("days", settings.Days);
            select.Parameters.AddWithValue("failedDays", settings.FailedDays);
            select.Parameters.AddWithValue("batch", settings.BatchSize);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetGuid(0));
        }

        foreach (var id in ids)
        {
            var directory = store.DirectoryFor(id);
            long bytes = DirectorySize(directory);
            if (!settings.DryRun)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                await using var mark = Sql.Command("UPDATE execution_work SET artifacts_purged_at = clock_timestamp() WHERE execution_id = @id", connection, transaction);
                mark.Parameters.AddWithValue("id", id);
                await mark.ExecuteNonQueryAsync(cancellationToken);
            }
            purged++; freed += bytes;
            logger.LogInformation("Artefacts de l’exécution {ExecutionId} {Action} ({Bytes} octets).", id, settings.DryRun ? "à supprimer (simulation)" : "supprimés", bytes);
        }
        await transaction.CommitAsync(cancellationToken);

        var temporary = RemoveStaleTemporaryFiles(settings);
        var result = new RetentionResult(ids.Count, purged, freed, temporary, settings.DryRun);
        OperationsMetrics.RetentionRun(result);
        return result;
    }

    private int RemoveStaleTemporaryFiles(ArtifactRetentionOptions settings)
    {
        if (!Directory.Exists(store.Root)) return 0;
        var limit = DateTime.UtcNow.AddHours(-settings.TempFileHours);
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(store.Root, "*.tmp", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) >= limit) continue;
                if (!settings.DryRun) File.Delete(file);
                removed++;
            }
            catch (IOException exception) { logger.LogWarning("Fichier temporaire d’artefact non supprimé : {Type}.", exception.GetType().Name); }
            catch (UnauthorizedAccessException exception) { logger.LogWarning("Fichier temporaire d’artefact non supprimé : {Type}.", exception.GetType().Name); }
        }
        return removed;
    }

    private static long DirectorySize(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(file => { try { return new FileInfo(file).Length; } catch (IOException) { return 0L; } })
            : 0L;
}

public sealed class ArtifactRetentionService(ArtifactRetention retention, OperationsOptions options, ILogger<ArtifactRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.ArtifactRetention;
        if (!settings.Enabled)
        {
            logger.LogInformation("Rétention des artefacts désactivée (Operations:ArtifactRetention:Enabled).");
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await retention.RunOnceAsync(stoppingToken);
                if (result.Purged > 0 || result.TempFilesRemoved > 0)
                    logger.LogInformation("Rétention : {Purged} exécutions, {Bytes} octets, {Temporary} fichiers temporaires{Simulation}.", result.Purged,
                        result.BytesFreed, result.TempFilesRemoved, result.DryRun ? " (simulation)" : "");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                OperationsMetrics.RetentionFailed();
                logger.LogError(exception, "Échec du nettoyage des artefacts.");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(settings.IntervalMinutes), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
