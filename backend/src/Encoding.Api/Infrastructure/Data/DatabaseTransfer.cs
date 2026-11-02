using System.Data.Common;
using System.Globalization;

namespace Encoding.Api.Infrastructure.Data;

public sealed record DatabaseTransferResult(IReadOnlyDictionary<string, long> Rows)
{
    public long Total => Rows.Values.Sum();
}

/// <summary>Copies an SQLite database into a migrated, empty SQLite database without merging data.</summary>
public static class DatabaseTransfer
{
    // Capture invalidations are local to a source's writers. The target migration seeds its own
    // full capture, and copying business rows fires its triggers; never restore a foreign acknowledgement.
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
        { "schema_migrations", "cluster_sync_capture_tables", "cluster_sync_dirty_entities" };
    private static readonly HashSet<string> Replaced = new(StringComparer.Ordinal) { "cluster_identity" };

    public static async Task<DatabaseTransferResult> CopyAsync(DbDataSource source, DbDataSource target, CancellationToken ct)
    {
        EnsureSqlite(source);
        EnsureSqlite(target);

        var sourceTables = (await TablesAsync(source, ct)).Where(IsTransferTable).ToHashSet(StringComparer.Ordinal);
        var targetTables = (await TablesAsync(target, ct)).Where(IsTransferTable).ToHashSet(StringComparer.Ordinal);
        var missing = sourceTables.Except(targetTables).Order(StringComparer.Ordinal).ToArray();
        var extra = targetTables.Except(sourceTables).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0 || extra.Length > 0)
            throw new InvalidOperationException("Les bases n'ont pas le même schéma. Migrez-les à la même version avant le transfert.");

        var order = await DependencyOrderAsync(target, sourceTables, ct);
        foreach (var table in order)
        {
            var sourceColumns = await ColumnsAsync(source, table, ct);
            var targetColumns = await ColumnsAsync(target, table, ct);
            if (!sourceColumns.SequenceEqual(targetColumns))
                throw new InvalidOperationException($"Le schéma de la table {table} diffère entre la source et la cible.");
        }

        await using var read = await source.OpenConnectionAsync(ct);
        await using var write = await target.OpenConnectionAsync(ct);
        await using var transaction = await write.BeginTransactionAsync(ct);
        await ExecuteAsync(write, transaction, "PRAGMA defer_foreign_keys=ON", ct);
        foreach (var table in order)
            if (!Replaced.Contains(table) && await CountAsync(write, transaction, table, ct) > 0)
                throw new InvalidOperationException($"La cible n'est pas vide : la table {table} contient déjà des lignes.");
        foreach (var table in Replaced.Intersect(order))
            await ExecuteAsync(write, transaction, $"DELETE FROM {Quote(table)}", ct);

        var rows = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in order)
            rows[table] = await CopyTableAsync(read, write, transaction, table, ct);
        await transaction.CommitAsync(ct);
        return new(rows);
    }

    /// <summary>Counts application rows, excluding migration history and peer-local replication metadata.</summary>
    public static async Task<IReadOnlyDictionary<string, long>> CountsAsync(DbDataSource dataSource, CancellationToken ct)
    {
        EnsureSqlite(dataSource);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in (await TablesAsync(dataSource, ct)).Where(IsTransferTable).Order(StringComparer.Ordinal))
            counts[table] = await CountAsync(connection, null, table, ct);
        return counts;
    }

    private static async Task<long> CopyTableAsync(DbConnection read, DbConnection write, DbTransaction transaction, string table, CancellationToken ct)
    {
        var columns = await ColumnsAsync(write, table, ct, transaction);
        var quotedTable = Quote(table);
        var names = string.Join(", ", columns.Select(column => Quote(column.Name)));
        await using var select = Sql.Command($"SELECT {names} FROM {quotedTable}", read);
        await using var reader = await select.ExecuteReaderAsync(ct);
        var parameters = string.Join(", ", columns.Select((_, index) => $"@value_{index}"));
        var insertSql = $"INSERT INTO {quotedTable}({names}) VALUES({parameters})";
        long count = 0;
        while (await reader.ReadAsync(ct))
        {
            await using var insert = Sql.Command(insertSql, write, transaction);
            for (var index = 0; index < columns.Count; index++)
                insert.Parameters.AddWithValue("value_" + index, reader.IsDBNull(index) ? DBNull.Value : reader.GetValue(index));
            await insert.ExecuteNonQueryAsync(ct);
            count++;
        }
        return count;
    }

    private sealed record Column(string Name, string Type);

    private static async Task<List<Column>> ColumnsAsync(DbDataSource dataSource, string table, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await ColumnsAsync(connection, table, ct);
    }

    private static async Task<List<Column>> ColumnsAsync(DbConnection connection, string table, CancellationToken ct, DbTransaction? transaction = null)
    {
        ValidateIdentifier(table);
        await using var command = Sql.Command($"SELECT name, type FROM pragma_table_xinfo('{table}') WHERE hidden=0 ORDER BY cid", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var columns = new List<Column>();
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            ValidateIdentifier(name);
            columns.Add(new(name, reader.GetString(1)));
        }
        return columns;
    }

    private static async Task<List<string>> TablesAsync(DbDataSource dataSource, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var tables = new List<string>();
        while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0));
        return tables;
    }

    private static async Task<List<string>> DependencyOrderAsync(DbDataSource dataSource, HashSet<string> tables, CancellationToken ct)
    {
        var dependencies = tables.ToDictionary(table => table, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        foreach (var table in tables)
        {
            await using var command = Sql.Command($"SELECT DISTINCT \"table\" FROM pragma_foreign_key_list('{table}')", connection);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var parent = reader.GetString(0);
                if (parent != table && dependencies.TryGetValue(table, out var set) && tables.Contains(parent)) set.Add(parent);
            }
        }

        var ordered = new List<string>();
        var remaining = new SortedSet<string>(tables, StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(table => dependencies[table].All(parent => !remaining.Contains(parent))).ToArray();
            if (ready.Length == 0)
                throw new InvalidOperationException("Dépendance circulaire entre tables : " + string.Join(", ", remaining));
            foreach (var table in ready) { ordered.Add(table); remaining.Remove(table); }
        }
        return ordered;
    }

    private static async Task<long> CountAsync(DbConnection connection, DbTransaction? transaction, string table, CancellationToken ct)
    {
        await using var command = Sql.Command($"SELECT count(*) FROM {Quote(table)}", connection, transaction);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = Sql.Command(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static bool IsTransferTable(string table) => !Skipped.Contains(table)
        && !table.StartsWith("peer_replication_", StringComparison.Ordinal);

    private static void EnsureSqlite(DbDataSource dataSource)
    {
        if (!Sql.IsSqlite(dataSource)) throw new InvalidOperationException("Le transfert de base de données prend uniquement en charge SQLite.");
    }

    private static string Quote(string identifier)
    {
        ValidateIdentifier(identifier);
        return $"\"{identifier}\"";
    }

    private static void ValidateIdentifier(string identifier)
    {
        if (identifier.Length == 0 || identifier.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new InvalidDataException("Identifiant SQLite inattendu.");
    }
}
