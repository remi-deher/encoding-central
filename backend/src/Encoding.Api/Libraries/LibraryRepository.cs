using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using Encoding.Contracts.Libraries;
using System.Text.Json;

namespace Encoding.Api.Libraries;

public sealed class LibraryRepository(DbDataSource dataSource)
{
    public async Task<IReadOnlyList<LibraryResponse>> ListAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT l.id, l.name, l.created_at,
                   count(a.node_id) FILTER (WHERE a.is_enabled AND a.mode <> 'unsupported')::integer,
                   count(a.node_id) FILTER (WHERE a.is_enabled AND a.mode = 'direct')::integer,
                   count(a.node_id) FILTER (WHERE a.is_enabled AND a.mode = 'transfer')::integer,
                   l.is_active,
                   (SELECT v.definition->>'name' FROM library_flow_assignments f JOIN flow_versions v ON v.flow_id=f.flow_id AND v.version=f.flow_version WHERE f.library_id=l.id),
                   (SELECT f.flow_version FROM library_flow_assignments f WHERE f.library_id=l.id)
            FROM libraries l
            LEFT JOIN library_node_accesses a ON a.library_id = l.id
            WHERE NOT l.is_deleted
            GROUP BY l.id
            ORDER BY l.name, l.id
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<LibraryResponse>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }

    public async Task<LibraryResponse?> CreateAsync(CreateLibraryRequest request, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO libraries (name) VALUES (@name)
            ON CONFLICT (name) DO NOTHING
            RETURNING id, name, created_at, 0::integer, 0::integer, 0::integer, is_active
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(sql, connection);
        command.Parameters.AddWithValue("name", request.Name.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<LibraryResponse?> UpdateAsync(Guid id, UpdateLibraryRequest request, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE libraries SET name = @name
            WHERE id = @id
            RETURNING id, name, created_at, 0::integer, 0::integer, 0::integer, is_active
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(sql, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", request.Name.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        await reader.CloseAsync();
        return await GetAsync(id, cancellationToken);
    }

    public async Task<LibraryResponse?> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken)
    {
        const string sql = "UPDATE libraries SET is_active = @active WHERE id = @id";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(sql, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("active", isActive);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return null;
        return await GetAsync(id, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var active = Sql.Command("""
            SELECT count(*) FROM execution_work w JOIN executions e ON e.id = w.execution_id
            WHERE w.library_id = @id AND e.state IN ('queued', 'running', 'cancelling', 'deferred')
            """, connection, transaction))
        {
            active.Parameters.AddWithValue("id", id);
            if (Convert.ToInt64(await active.ExecuteScalarAsync(cancellationToken)) > 0) throw new LibraryInUseException();
        }
        var deleted = await DeleteWithHistoryAsync(connection, transaction, id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    /// <summary>
    /// Removes a library while keeping its execution history: the executions stay (they carry the library name as
    /// text) and only their library-bound work and reservations are released, which would otherwise block the delete.
    /// </summary>
    internal static async Task<bool> DeleteWithHistoryAsync(DbConnection connection, DbTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        foreach (var sql in new[] { "DELETE FROM media_reservations WHERE library_id = @id", "DELETE FROM execution_work WHERE library_id = @id" })
        {
            await using var release = Sql.Command(sql, connection, transaction);
            release.Parameters.AddWithValue("id", id);
            await release.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = Sql.Command("DELETE FROM libraries WHERE id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private async Task<LibraryResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT l.id, l.name, l.created_at,
                   count(a.node_id) FILTER (WHERE a.is_enabled AND a.mode <> 'unsupported')::integer,
                   count(a.node_id) FILTER (WHERE a.is_enabled AND a.mode = 'direct')::integer,
                   count(a.node_id) FILTER (WHERE a.is_enabled AND a.mode = 'transfer')::integer,
                   l.is_active,
                   (SELECT v.definition->>'name' FROM library_flow_assignments f JOIN flow_versions v ON v.flow_id=f.flow_id AND v.version=f.flow_version WHERE f.library_id=l.id),
                   (SELECT f.flow_version FROM library_flow_assignments f WHERE f.library_id=l.id)
            FROM libraries l LEFT JOIN library_node_accesses a ON a.library_id = l.id
            WHERE l.id = @id AND NOT l.is_deleted GROUP BY l.id
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static LibraryResponse Read(DbDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1),
        new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc)),
        reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetBoolean(6), reader.FieldCount>7 && !reader.IsDBNull(7)?reader.GetString(7):null,
            reader.FieldCount>8 && !reader.IsDBNull(8)?reader.GetInt32(8):null);

    public async Task<IReadOnlyList<LibraryOverviewResponse>> OverviewAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT l.id, l.name, l.is_active, l.created_at,
                   (SELECT v.definition->>'name' FROM library_flow_assignments f JOIN flow_versions v ON v.flow_id=f.flow_id AND v.version=f.flow_version WHERE f.library_id=l.id),
                   (SELECT f.flow_version FROM library_flow_assignments f WHERE f.library_id=l.id),
                   (SELECT f.flow_id FROM library_flow_assignments f WHERE f.library_id=l.id),
                   count(DISTINCT a.node_id) FILTER (WHERE a.is_enabled AND a.mode <> 'unsupported')::integer,
                   count(DISTINCT a.node_id) FILTER (WHERE a.is_enabled AND a.mode = 'direct')::integer,
                   COALESCE(p.watch_enabled, true),
                   COALESCE(p.scan_interval_seconds, 3600),
                   (SELECT count(*) FROM library_media m WHERE m.library_id = l.id)::integer,
                   (SELECT count(*) FROM executions e WHERE e.library_name = l.name AND e.state = 'queued')::integer,
                   (SELECT count(*) FROM executions e WHERE e.library_name = l.name AND e.state = 'running')::integer,
                   (SELECT count(*) FROM executions e WHERE e.library_name = l.name AND e.state = 'completed')::integer,
                   (SELECT count(*) FROM executions e WHERE e.library_name = l.name AND e.state = 'failed')::integer
            FROM libraries l
            LEFT JOIN library_node_accesses a ON a.library_id = l.id
            LEFT JOIN library_processing_settings p ON p.library_id = l.id
            WHERE NOT l.is_deleted
            GROUP BY l.id, l.name, l.is_active, l.created_at, p.watch_enabled, p.scan_interval_seconds
            ORDER BY l.name, l.id
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<LibraryOverviewResponse>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LibraryOverviewResponse(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetGuid(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetBoolean(9),
                reader.GetInt32(10),
                reader.GetInt32(11),
                reader.GetInt32(12),
                reader.GetInt32(13),
                reader.GetInt32(14),
                reader.GetInt32(15)));
        }
        return result;
    }
}


/// <summary>Raised when a library still has queued or running executions and cannot be deleted yet.</summary>
public sealed class LibraryInUseException : Exception
{
    public LibraryInUseException() : base("Des traitements de cette bibliothèque sont en attente ou en cours.") { }
}
