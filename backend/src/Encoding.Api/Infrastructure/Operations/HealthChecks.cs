using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using Encoding.Api.Executions;
using Encoding.Api.Nodes;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Encoding.Api.Infrastructure.Operations;

public sealed class DatabaseHealthCheck(DbDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = Sql.Command("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy("SQLite répond.");
        }
        catch (DbException)
        {
            return HealthCheckResult.Unhealthy("Base de données indisponible.");
        }
    }
}

public sealed class MigrationsHealthCheck(DbDataSource dataSource, IHostEnvironment environment) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await DatabaseMigrator.StatusAsync(dataSource, environment.ContentRootPath, cancellationToken);
            var data = new Dictionary<string, object> { ["applied"] = status.Applied, ["pending"] = status.Pending.Count, ["unknown"] = status.Unknown.Count };
            if (status.Pending.Count > 0) return HealthCheckResult.Unhealthy("Des migrations ne sont pas appliquées.", data: data);
            if (status.Unknown.Count > 0) return HealthCheckResult.Degraded("La base contient des migrations inconnues de ce binaire (nœud plus ancien que le schéma).", data: data);
            return HealthCheckResult.Healthy("Schéma à jour.", data);
        }
        catch (DbException)
        {
            return HealthCheckResult.Unhealthy("État des migrations illisible.");
        }
    }
}

/// <summary>Un volume partagé doit exister, accepter l’écriture et garder un espace libre minimal. Les chemins ne sont pas exposés.</summary>
public sealed class StorageHealthCheck(string directory, long minFreeBytes) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".health-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
            var data = new Dictionary<string, object> { ["freeBytes"] = free, ["minFreeBytes"] = minFreeBytes };
            return Task.FromResult(free < minFreeBytes
                ? HealthCheckResult.Degraded("Espace disque libre sous le seuil.", data: data)
                : HealthCheckResult.Healthy("Volume accessible en écriture.", data));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Volume inaccessible en écriture."));
        }
    }
}

public sealed class ClusterHealthCheck(DbDataSource dataSource, NodePresenceOptions presence) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("""
                SELECT
                  (SELECT count(*) FROM cluster_nodes WHERE deleted_at IS NULL AND 'worker' = ANY(roles) AND last_seen_at >= clock_timestamp() - make_interval(secs => @offline)),
                  (SELECT count(*) FROM execution_work w JOIN executions e ON e.id = w.execution_id
                    WHERE e.state IN ('running','cancelling') AND (w.lease_until IS NULL OR w.lease_until <= clock_timestamp() - interval '2 minutes'))
                """);
            command.Parameters.AddWithValue("offline", presence.OfflineAfter.TotalSeconds);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            long workers = reader.GetInt64(0), stalled = reader.GetInt64(1);
            var data = new Dictionary<string, object> { ["onlineWorkers"] = workers, ["stalledExecutions"] = stalled };
            if (stalled > 0) return HealthCheckResult.Degraded("Des exécutions ont un bail expiré depuis plus de 2 minutes.", data: data);
            if (workers == 0) return HealthCheckResult.Degraded("Aucun worker en ligne.", data: data);
            return HealthCheckResult.Healthy("Cluster opérationnel.", data);
        }
        catch (DbException)
        {
            return HealthCheckResult.Unhealthy("État du cluster illisible.");
        }
    }
}

public sealed class FlowBackupHealthCheck(DbDataSource dataSource, OperationsOptions options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("""
                SELECT count(*) FILTER (WHERE status = 'failed'),
                       count(*) FILTER (WHERE status = 'pending' AND next_attempt_at <= clock_timestamp() - make_interval(mins => @lag))
                FROM flow_backup_jobs
                """);
            command.Parameters.AddWithValue("lag", options.Health.BackupLagMinutes);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            long failed = reader.GetInt64(0), late = reader.GetInt64(1);
            var data = new Dictionary<string, object> { ["failed"] = failed, ["late"] = late };
            return failed + late > 0
                ? HealthCheckResult.Degraded("Des sauvegardes de flows sont en échec ou en retard.", data: data)
                : HealthCheckResult.Healthy("Sauvegardes de flows à jour.", data);
        }
        catch (DbException)
        {
            return HealthCheckResult.Unhealthy("File de sauvegardes illisible.");
        }
    }
}
