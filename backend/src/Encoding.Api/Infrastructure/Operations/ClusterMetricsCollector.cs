using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using System.Diagnostics;
using Encoding.Api.Nodes;
using Encoding.Contracts.Executions;

namespace Encoding.Api.Infrastructure.Operations;

/// <summary>Construit la page <c>/metrics</c> : état du cluster dans SQLite et mesures du processus local.</summary>
public sealed class ClusterMetricsCollector(DbDataSource dataSource, NodePresenceOptions presence, IHostEnvironment environment)
{
    private static readonly string[] MetricExecutionStates =
    [
        ExecutionStates.Queued, ExecutionStates.Running, ExecutionStates.Cancelling,
        ExecutionStates.Succeeded, ExecutionStates.Failed, ExecutionStates.Cancelled, ExecutionStates.Deferred
    ];
    private static readonly string[] BackupStatuses = ["pending", "ready", "failed"];

    public async Task<string> CollectAsync(CancellationToken cancellationToken)
    {
        var writer = new PrometheusWriter();
        writer.Gauge("encoding_up", "1 si ce serveur répond.", 1);
        try { await WriteClusterAsync(writer, cancellationToken); }
        catch (DbException)
        {
            writer.Gauge("encoding_database_up", "1 si la base locale répond.", 0);
        }
        WriteProcess(writer);
        WriteRetention(writer);
        WriteHttp(writer);
        WriteSqliteWriter(writer);
        return writer.ToString();
    }

    private void WriteSqliteWriter(PrometheusWriter writer)
    {
        if (dataSource is not Data.Sqlite.SqliteDataSource sqlite) return;
        var state = sqlite.Writer.Snapshot();
        writer.Gauge("encoding_sqlite_writer_enabled", "1 si la file locale d’écriture est activée.", sqlite.Writer.Options.Enabled ? 1 : 0);
        writer.Gauge("encoding_sqlite_writer_critical_only", "1 si seules les écritures critiques utilisent la file.", sqlite.Writer.Options.CriticalOnly ? 1 : 0);
        writer.Gauge("encoding_sqlite_writer_pending", "Écritures ordinaires en attente.", state.Pending);
        writer.Gauge("encoding_sqlite_writer_urgent_pending", "Écritures urgentes en attente.", state.UrgentPending);
        writer.Gauge("encoding_sqlite_writer_active", "1 si un écrivain détient la file locale.", state.Active ? 1 : 0);
        writer.Counter("encoding_sqlite_writer_admitted_total", "Unités d’écriture admises.", state.Admitted);
        writer.Counter("encoding_sqlite_writer_rejected_total", "Refus pour saturation.", state.Rejected);
        writer.Counter("encoding_sqlite_writer_expired_total", "Demandes expirées avant admission.", state.Expired);
        writer.Counter("encoding_sqlite_writer_begin_retries_total", "Nouvelles tentatives de BEGIN après verrou transitoire.", state.Retries);
        writer.Counter("encoding_sqlite_writer_long_holds_total", "Détentions au-delà du seuil configuré.", state.LongHolds);
        writer.Counter("encoding_sqlite_writer_wait_seconds_total", "Temps cumulé d’attente des écritures admises.", state.WaitSeconds);
        writer.Counter("encoding_sqlite_writer_hold_seconds_total", "Temps cumulé de détention de l’écrivain.", state.HoldSeconds);
    }

    private async Task WriteClusterAsync(PrometheusWriter writer, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        writer.Gauge("encoding_database_up", "1 si la base locale répond.", 1);

        var nodes = await Rows(connection, """
            SELECT CASE WHEN last_seen_at >= clock_timestamp() - make_interval(secs => @offline) THEN 'online' ELSE 'offline' END, count(*)
            FROM cluster_nodes WHERE deleted_at IS NULL GROUP BY 1
            """, ct, ("offline", presence.OfflineAfter.TotalSeconds));
        foreach (var status in new[] { "online", "offline" })
            writer.Gauge("encoding_nodes", "Nœuds enrôlés non retirés, par présence.", nodes.GetValueOrDefault(status), ("status", status));

        var executions = await Rows(connection, "SELECT state, count(*) FROM executions GROUP BY state", ct);
        foreach (var state in MetricExecutionStates)
            writer.Gauge("encoding_executions", "Exécutions par état.", executions.GetValueOrDefault(state), ("state", state));

        writer.Gauge("encoding_executions_oldest_queued_age_seconds", "Ancienneté de la plus ancienne exécution en file.",
            await Scalar(connection, "SELECT COALESCE(EXTRACT(EPOCH FROM clock_timestamp() - min(queued_at)), 0) FROM executions WHERE state = 'queued'", ct));
        writer.Gauge("encoding_execution_leases_expired", "Exécutions en cours dont le bail a expiré et attendent leur clôture en erreur.",
            await Scalar(connection,
                "SELECT count(*) FROM execution_work w JOIN executions e ON e.id = w.execution_id WHERE e.state IN ('running','cancelling') AND (w.lease_until IS NULL OR w.lease_until <= clock_timestamp())",
                ct));
        writer.Gauge("encoding_media_reservations_active", "Médias actuellement réservés par un bail valide.",
            await Scalar(connection, "SELECT count(*) FROM media_reservations WHERE lease_until > clock_timestamp()", ct));
        writer.Gauge("encoding_disk_locks_active", "Disques actuellement verrouillés par une exécution au bail valide.",
            await Scalar(connection, "SELECT count(*) FROM disk_locks WHERE lease_until > clock_timestamp()", ct));
        writer.Gauge("encoding_artifacts_pending_purge", "Exécutions terminées dont les artefacts n’ont pas été purgés.",
            await Scalar(connection,
                "SELECT count(*) FROM execution_work w JOIN executions e ON e.id = w.execution_id WHERE w.artifacts_purged_at IS NULL AND e.state IN ('succeeded','failed','cancelled')", ct));

        var backups = await Rows(connection, "SELECT status, count(*) FROM flow_backup_jobs GROUP BY status", ct);
        foreach (var status in BackupStatuses)
            writer.Gauge("encoding_flow_backup_jobs", "Sauvegardes de versions de flow par statut.", backups.GetValueOrDefault(status), ("status", status));
        writer.Gauge("encoding_flow_backup_oldest_pending_age_seconds", "Ancienneté de la plus ancienne sauvegarde de flow en attente.",
            await Scalar(connection,
                "SELECT COALESCE(EXTRACT(EPOCH FROM clock_timestamp() - min(next_attempt_at)), 0) FROM flow_backup_jobs WHERE status = 'pending' AND next_attempt_at <= clock_timestamp()", ct));

        var migrations = await DatabaseMigrator.StatusAsync(dataSource, environment.ContentRootPath, ct);
        writer.Gauge("encoding_schema_migrations_applied", "Migrations enregistrées en base.", migrations.Applied);
        writer.Gauge("encoding_schema_migrations_pending", "Migrations de ce binaire non appliquées.", migrations.Pending.Count);
    }

    private static void WriteProcess(PrometheusWriter writer)
    {
        using var process = Process.GetCurrentProcess();
        writer.Gauge("process_start_time_seconds", "Début du processus (epoch Unix).", new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeSeconds());
        writer.Counter("process_cpu_seconds_total", "Temps CPU consommé.", process.TotalProcessorTime.TotalSeconds);
        writer.Gauge("process_resident_memory_bytes", "Mémoire résidente.", process.WorkingSet64);
        writer.Gauge("dotnet_gc_heap_bytes", "Tas géré actuel.", GC.GetTotalMemory(false));
        for (var generation = 0; generation <= GC.MaxGeneration; generation++)
            writer.Counter("dotnet_gc_collections_total", "Collections du ramasse-miettes par génération.", GC.CollectionCount(generation), ("generation",
                generation.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        writer.Gauge("dotnet_threadpool_threads", "Threads du pool.", ThreadPool.ThreadCount);
        writer.Gauge("dotnet_threadpool_queue_length", "Éléments en attente dans le pool.", ThreadPool.PendingWorkItemCount);
    }

    private static void WriteRetention(PrometheusWriter writer)
    {
        writer.Counter("encoding_artifact_retention_runs_total", "Passes de rétention des artefacts.", OperationsMetrics.RetentionRuns);
        writer.Counter("encoding_artifact_retention_executions_total", "Exécutions dont les artefacts ont été supprimés.", OperationsMetrics.RetentionExecutions);
        writer.Counter("encoding_artifact_retention_bytes_total", "Octets libérés par la rétention.", OperationsMetrics.RetentionBytes);
        writer.Counter("encoding_artifact_retention_temporary_files_total", "Fichiers temporaires orphelins supprimés.", OperationsMetrics.RetentionTemporaryFiles);
        writer.Counter("encoding_artifact_retention_errors_total", "Échecs de passes de rétention.", OperationsMetrics.RetentionErrors);
        writer.Gauge("encoding_artifact_retention_last_run_timestamp_seconds", "Dernière passe de rétention (epoch Unix, 0 si aucune).", OperationsMetrics.RetentionLastRunUnix);
    }

    private static void WriteHttp(PrometheusWriter writer)
    {
        foreach (var (key, stat) in OperationsMetrics.HttpRequests.OrderBy(pair => pair.Key.Route, StringComparer.Ordinal).ThenBy(pair => pair.Key.Method,
            StringComparer.Ordinal).ThenBy(pair => pair.Key.Status))
        {
            var labels = new[] { ("method", key.Method), ("route", key.Route), ("status", key.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
            writer.Counter("encoding_http_requests_total", "Requêtes HTTP traitées.", Interlocked.Read(ref stat.Count), labels);
            writer.Counter("encoding_http_request_duration_seconds_total", "Durée cumulée des requêtes HTTP.", Interlocked.Read(ref stat.TicksTotal) / (double)Stopwatch.Frequency, labels);
        }
    }

    private static async Task<Dictionary<string, long>> Rows(DbConnection connection, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = Sql.Command(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result[reader.GetString(0)] = reader.GetInt64(1);
        return result;
    }

    private static async Task<double> Scalar(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var command = Sql.Command(sql, connection);
        return Convert.ToDouble(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}
