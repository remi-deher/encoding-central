using Microsoft.Extensions.Logging;
using System.Data.Common;
using Encoding.Api.Infrastructure.Data;

namespace Encoding.Api.Infrastructure;

public sealed record ClusterProjectionDiagnostic(Guid EventId, string Table, string Cause,
    DateTimeOffset FirstFailedAt, DateTimeOffset LastFailedAt, long Attempts, string Action);

public sealed partial class ClusterEntityReconciliation
{
    public async Task<IReadOnlyList<ClusterProjectionDiagnostic>> ProjectionDiagnosticsAsync(CancellationToken ct)
    {
        var result = new List<ClusterProjectionDiagnostic>();
        await using var command = dataSource.CreateCommand("""
            SELECT d.event_id,d.entity_type,d.cause,d.first_failed_at,d.last_failed_at,d.attempts
            FROM cluster_projection_diagnostics d JOIN cluster_sync_pending_projections p ON p.event_id=d.event_id
            ORDER BY d.first_failed_at,d.event_id LIMIT 256
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var cause = reader.GetString(2);
            result.Add(new(reader.GetGuid(0), reader.GetString(1), cause,
                reader.GetFieldValue<DateTimeOffset>(3), reader.GetFieldValue<DateTimeOffset>(4), reader.GetInt64(5),
                cause switch {
                    "immutable_snapshot_conflict" => "Comparer les instantanés et résoudre explicitement le conflit ; aucun écrasement autorisé.",
                    "username_conflict" => "Renommer explicitement le compte entrant en conservant son identité et ses credentials.",
                    "constraint_conflict" => "Résoudre explicitement le conflit de contrainte de cette entité ; le journal est conservé.",
                    "dependency_missing" => "Attendre la projection des dépendances du journal.",
                    _ => "Vérifier la disponibilité du stockage et le diagnostic du nœud."
                }));
        }
        return result;
    }

    private async Task RecordProjectionFailureAsync(Guid eventId, string table, string cause, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var previous = Sql.Command("SELECT cause FROM cluster_projection_diagnostics WHERE event_id=@id", connection, transaction);
        previous.Parameters.AddWithValue("id", eventId);
        var changed = !string.Equals(await previous.ExecuteScalarAsync(ct) as string, cause, StringComparison.Ordinal);
        await using var command = Sql.Command("""
            INSERT INTO cluster_projection_diagnostics(event_id,entity_type,cause,first_failed_at,last_failed_at)
            VALUES(@id,@table,@cause,clock_timestamp(),clock_timestamp())
            ON CONFLICT(event_id) DO UPDATE SET cause=excluded.cause,last_failed_at=excluded.last_failed_at,attempts=cluster_projection_diagnostics.attempts+1
            """, connection, transaction);
        command.Parameters.AddWithValue("id", eventId);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("cause", cause);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        if (changed) logger.LogWarning("Projection bloquée : {Cause}, table {Table}, événement {EventId}. Voir le diagnostic de réconciliation.", cause, table, eventId);
    }
}
