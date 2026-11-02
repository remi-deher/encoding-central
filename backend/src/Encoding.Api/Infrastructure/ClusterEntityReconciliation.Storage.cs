using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Tools;
using Encoding.Contracts.Cluster;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Encoding.Api.Infrastructure;

public sealed partial class ClusterEntityReconciliation
{
    private async Task UpsertRowAsync(DbConnection connection, DbTransaction transaction, EntityTable table, JsonObject row, CancellationToken ct)
    {
        if(table.Name is "executions" or "execution_steps" or "execution_events")
        {
            var id=row[table.Name=="executions"?"id":"execution_id"]?.GetValue<string>();
            if(id is not null)
            {
                await using var owned=Sql.Command("SELECT EXISTS(SELECT 1 FROM execution_work w JOIN task_ownership o ON o.execution_id=w.execution_id JOIN task_server_identity s ON s.peer_id=o.owner_peer_id WHERE w.execution_id=@id AND o.state IN ('active','frozen'))",
                    connection,transaction);
                owned.Parameters.AddWithValue("id",Guid.Parse(id));
                if(await owned.ScalarBoolAsync(ct))
                {
                    // Late replicated audit for an already imported completed invocation is immutable.
                    // It may fill missing command logs, never replace current checkpoints or runtime state.
                    if(table.Name=="execution_steps" && row["completed_at"] is not null && row["checkpoint"] is not null && row["command_logs"] is not null)
                    {
                        await using var audit=Sql.Command("UPDATE execution_steps SET command_logs=@logs WHERE execution_id=@id AND node_id=@node AND invocation_id=@invocation AND completed_at IS NOT NULL AND checkpoint=@checkpoint AND length(command_logs)<length(@logs)",
                            connection,transaction);
                        audit.Parameters.AddWithValue("id",Guid.Parse(id));audit.Parameters.AddWithValue("node",row["node_id"]!.GetValue<string>());
                            audit.Parameters.AddWithValue("invocation",Guid.Parse(row["invocation_id"]!.GetValue<string>()));
                            audit.Parameters.AddWithValue("checkpoint",SqlKind.Json,row["checkpoint"]!.ToJsonString(JsonOptions));
                            audit.Parameters.AddWithValue("logs",SqlKind.Json,row["command_logs"]!.ToJsonString(JsonOptions));
                            await audit.ExecuteNonQueryAsync(ct);
                    }
                    return;
                }
            }
        }
        var localRow = PrepareRowForLocalStorage(table, row);
        var expressions = table.Columns.Select(column => table.BinaryColumns.Contains(column, StringComparer.Ordinal) ? "@binary_" + column : "json_extract(@row, '$." + column + "')").ToArray();
        var updates = table.Columns.Where(column => !table.KeyColumns.Contains(column, StringComparer.Ordinal)
            && !table.PreserveColumnsOnUpdate.Contains(column, StringComparer.Ordinal)).Select(column => column == "last_seen_at"
            && table.Name == "cluster_nodes"
            ? "last_seen_at = CASE WHEN cluster_nodes.last_seen_at < excluded.last_seen_at THEN excluded.last_seen_at ELSE cluster_nodes.last_seen_at END"
            : $"{column}=excluded.{column}");
        var onConflict = table.Immutable ? "DO NOTHING" : "DO UPDATE SET " + string.Join(",", updates);
        var sql = "INSERT INTO " + table.Name + " (" + string.Join(",", table.Columns) + ") SELECT "
            + string.Join(",", expressions) + " WHERE true ON CONFLICT (" + string.Join(",", table.KeyColumns) + ") " + onConflict;
        await using var command = Sql.Command(sql, connection, transaction);
        command.Parameters.AddWithValue("row", JsonKind(localRow));
        foreach (var column in table.BinaryColumns)
            command.Parameters.AddWithValue("binary_" + column, SqlKind.Bytes, ReadHex(localRow[column]));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> UpsertProcessingReceiptAsync(DbConnection connection, DbTransaction transaction, JsonObject row, CancellationToken ct)
    {
        var libraryId = Guid.Parse(row["library_id"]!.GetValue<string>());
        var path = row["relative_path"]!.GetValue<string>();
        object? mediaId;
        await using (var media = Sql.Command("SELECT id FROM library_media WHERE library_id=@library AND relative_path=@path", connection, transaction))
        {
            media.Parameters.AddWithValue("library", libraryId);
            media.Parameters.AddWithValue("path", path);
            mediaId = await media.ExecuteScalarAsync(ct);
        }
        if (mediaId is null or DBNull) return false;

        await using var receipt = Sql.Command("""
            INSERT INTO library_processing_receipts(library_id,file_id,flow_id,flow_version,byte_length,modified_at,execution_id)
            VALUES(@library,@file,@flow,@version,@bytes,@modified,@execution)
            ON CONFLICT(library_id,file_id,flow_id,flow_version,byte_length,modified_at) DO UPDATE SET execution_id=EXCLUDED.execution_id
            """, connection, transaction);
        receipt.Parameters.AddWithValue("library", libraryId);
        receipt.Parameters.AddWithValue("file", mediaId);
        receipt.Parameters.AddWithValue("flow", Guid.Parse(row["flow_id"]!.GetValue<string>()));
        receipt.Parameters.AddWithValue("version", row["flow_version"]!.GetValue<int>());
        receipt.Parameters.AddWithValue("bytes", row["byte_length"]!.GetValue<long>());
        receipt.Parameters.AddWithValue("modified", ReadInstant(row["modified_at"]!.GetValue<string>()));
        receipt.Parameters.AddWithValue("execution", Guid.Parse(row["execution_id"]!.GetValue<string>()));
        await receipt.ExecuteNonQueryAsync(ct);
        return true;
    }

    private static async Task EnsureFlowBackupProjectionAsync(string entityType, JsonObject row, DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        if (entityType == "flow_versions")
        {
            await using var backup = Sql.Command("INSERT INTO flow_backup_jobs(flow_id,version) VALUES(@flow,@version) ON CONFLICT DO NOTHING", connection, transaction);
            backup.Parameters.AddWithValue("flow", Guid.Parse(row["flow_id"]!.GetValue<string>()));
            backup.Parameters.AddWithValue("version", row["version"]!.GetValue<int>());
            await backup.ExecuteNonQueryAsync(ct);
        }
        else if (entityType == "flow_drafts")
        {
            await using var backup = Sql.Command("INSERT INTO flow_draft_backups(flow_id,revision,definition) VALUES(@flow,@revision,@definition) ON CONFLICT DO NOTHING", connection, transaction);
            backup.Parameters.AddWithValue("flow", Guid.Parse(row["flow_id"]!.GetValue<string>()));
            backup.Parameters.AddWithValue("revision", row["revision"]!.GetValue<int>());
            backup.Parameters.AddWithValue("definition", SqlKind.Json, row["definition"]!.ToJsonString(JsonOptions));
            await backup.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task DeleteProcessingReceiptAsync(DbConnection connection, DbTransaction transaction, string keyJson, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(keyJson);
        var key = document.RootElement;
        var libraryId = Guid.Parse(key.GetProperty("library_id").GetString()!);
        await using var command = Sql.Command("""
            DELETE FROM library_processing_receipts
            WHERE library_id=@library
              AND file_id=(SELECT id FROM library_media WHERE library_id=@library AND relative_path=@path)
              AND flow_id=@flow AND flow_version=@version AND byte_length=@bytes AND modified_at=@modified
            """, connection, transaction);
        command.Parameters.AddWithValue("library", libraryId);
        command.Parameters.AddWithValue("path", key.GetProperty("relative_path").GetString()!);
        command.Parameters.AddWithValue("flow", Guid.Parse(key.GetProperty("flow_id").GetString()!));
        command.Parameters.AddWithValue("version", key.GetProperty("flow_version").GetInt32());
        command.Parameters.AddWithValue("bytes", key.GetProperty("byte_length").GetInt64());
        command.Parameters.AddWithValue("modified", ReadInstant(key.GetProperty("modified_at").GetString()!));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string JsonKind(JsonObject row) => row.ToJsonString(JsonOptions);

    private static async Task<DateTimeOffset?> ReadTableCursorAsync(DbConnection connection, string table, CancellationToken ct)
    {
        await using var command = Sql.Command("SELECT cursor_value FROM cluster_sync_table_cursors WHERE entity_type=@type", connection);
        command.Parameters.AddWithValue("type", table);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : ReadInstant(value);
    }

    private static async Task UpsertTableCursorAsync(DbConnection connection, DbTransaction transaction, string table, DateTimeOffset cursor, CancellationToken ct)
    {
        await using var command = Sql.Command("INSERT INTO cluster_sync_table_cursors(entity_type,cursor_value) VALUES(@type,@cursor) ON CONFLICT(entity_type) DO UPDATE SET cursor_value=EXCLUDED.cursor_value WHERE cluster_sync_table_cursors.cursor_value < EXCLUDED.cursor_value",
            connection, transaction);
        command.Parameters.AddWithValue("type", table);
        command.Parameters.AddWithValue("cursor", cursor);
        await command.ExecuteNonQueryAsync(ct);
    }

    private JsonObject PrepareRowForLocalStorage(EntityTable table, JsonObject row)
    {
        var local = (JsonObject)row.DeepClone();
        if ((table.Name is "plugin_configurations" or "flow_plugin_configurations" or "ai_credentials") && local["encrypted_values"] is { } values)
        {
            var plaintext = System.Text.Encoding.UTF8.GetBytes(values.ToJsonString(JsonOptions));
            local["encrypted_values"] = Convert.ToHexString(LocalProtector(table.Name).Protect(plaintext)).ToLowerInvariant();
        }
        return local;
    }

    private static byte[]? ReadHex(JsonNode? value)
    {
        if (value is null) return null;
        var text = value.GetValue<string>();
        return string.IsNullOrEmpty(text) ? [] : Convert.FromHexString(text);
    }

    private async Task DeleteRowAsync(DbConnection connection, DbTransaction transaction, EntityTable table, string keyJson, CancellationToken ct)
    {
        if (table.Name == "libraries")
        {
            // The owner already checked for active work; release local work rows that would block the delete.
            await Encoding.Api.Libraries.LibraryRepository.DeleteWithHistoryAsync(connection, transaction, Guid.Parse(JsonNode.Parse(keyJson)!["id"]!.GetValue<string>()), ct);
            return;
        }
        // Schema metadata only (EntityTable), never caller-supplied identifiers.
        var sql = "DELETE FROM " + table.Name + " WHERE " + string.Join(" AND ",
            table.KeyColumns.Select(column => column + " = json_extract(@key, '$." + column + "')"));
        await using var command = Sql.Command(sql, connection, transaction);
        command.Parameters.AddWithValue("key", keyJson);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<LocalIdentity> ReadLocalIdentityAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        await using var command = Sql.Command("SELECT c.id,s.origin_id,s.next_sequence FROM cluster_identity c CROSS JOIN cluster_sync_identity s WHERE c.singleton=true AND s.singleton=true FOR UPDATE",
            connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Identité du cluster ou du journal absente.");
        return new(ReadGuid(reader, 0), ReadGuid(reader, 1), Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture));
    }

    private async Task InsertLocalEventAsync(DbConnection connection, DbTransaction transaction, Guid clusterId, Guid originId, long sequence, string eventType,
        string payload, DateTimeOffset occurredAt, CancellationToken ct)
    {
        await using var command = Sql.Command("INSERT INTO cluster_change_log(event_id,cluster_id,origin_id,sequence,event_type,payload,occurred_at) VALUES(@id,@cluster,@origin,@sequence,@type,@payload,@time)",
            connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("cluster", clusterId);
        command.Parameters.AddWithValue("origin", originId);
        command.Parameters.AddWithValue("sequence", sequence);
        command.Parameters.AddWithValue("type", eventType);
        command.Parameters.AddWithValue("payload", SqlKind.Json, payload);
        command.Parameters.AddWithValue("time", occurredAt);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<Dictionary<string, EntityState>> ReadEntityStatesAsync(DbConnection connection, DbTransaction transaction, string type,
        CancellationToken ct, IReadOnlyCollection<string>? keys = null)
    {
        var states = new Dictionary<string, EntityState>(StringComparer.Ordinal);
        const string EntityKeyInKeysFilter = " AND entity_key IN(SELECT value FROM json_each(@keys))";
        var filter = keys is null ? "" : EntityKeyInKeysFilter;
        await using var command = Sql.Command("SELECT entity_key,fingerprint,occurred_at,origin_id,sequence,is_deleted FROM cluster_sync_entities WHERE entity_type=@type" + filter,
            connection, transaction);
        command.Parameters.AddWithValue("type", type);
        if (keys is not null) command.Parameters.AddWithValue("keys", JsonSerializer.Serialize(keys, JsonOptions));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) states[reader.GetString(0)] = new(reader.GetString(1), ReadInstant(reader.GetValue(2)), ReadGuid(reader, 3),
            Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture), Convert.ToBoolean(reader.GetValue(5), CultureInfo.InvariantCulture));
        return states;
    }

    private async Task<EntityState?> ReadEntityStateAsync(DbConnection connection, DbTransaction transaction, string type, string key, CancellationToken ct)
    {
        await using var command = Sql.Command("SELECT fingerprint,occurred_at,origin_id,sequence,is_deleted FROM cluster_sync_entities WHERE entity_type=@type AND entity_key=@key",
            connection, transaction);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetString(0), ReadInstant(reader.GetValue(1)), ReadGuid(reader, 2), Convert.ToInt64(reader.GetValue(3),
            CultureInfo.InvariantCulture), Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture)) : null;
    }

    private async Task UpsertEntityStateAsync(DbConnection connection, DbTransaction transaction, string type, string key, string fingerprint,
        DateTimeOffset occurredAt, Guid originId, long sequence, bool deleted, CancellationToken ct)
    {
        await using var command = Sql.Command("INSERT INTO cluster_sync_entities(entity_type,entity_key,fingerprint,occurred_at,origin_id,sequence,is_deleted) VALUES(@type,@key,@fingerprint,@time,@origin,@sequence,@deleted) ON CONFLICT(entity_type,entity_key) DO UPDATE SET fingerprint=EXCLUDED.fingerprint,occurred_at=EXCLUDED.occurred_at,origin_id=EXCLUDED.origin_id,sequence=EXCLUDED.sequence,is_deleted=EXCLUDED.is_deleted",
            connection, transaction);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        command.Parameters.AddWithValue("time", occurredAt);
        command.Parameters.AddWithValue("origin", originId);
        command.Parameters.AddWithValue("sequence", sequence);
        command.Parameters.AddWithValue("deleted", deleted);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeletePendingAsync(DbConnection connection, DbTransaction transaction, Guid eventId, CancellationToken ct)
    {
        await using var command = Sql.Command("DELETE FROM cluster_sync_pending_projections WHERE event_id=@id", connection, transaction);
        command.Parameters.AddWithValue("id", eventId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static bool TryGetTable(string name, out EntityTable table)
    {
        table = name == MediaTable.Name ? MediaTable
            : name == ProcessingReceiptTable.Name ? ProcessingReceiptTable
            : Tables.FirstOrDefault(candidate => candidate.Name == name)!;
        return table is not null;
    }

    private static EntityTable GetTable(string name) => TryGetTable(name, out var table) ? table : throw new InvalidOperationException("Type d'entité non pris en charge.");

    private static bool KeyBelongsToLibrary(string key, string libraryId)
    {
        try { return string.Equals(JsonDocument.Parse(key).RootElement.GetProperty("library_id").GetString(), libraryId, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static IEnumerable<string> SensitiveColumns(string table) => table switch
    {
        "cluster_nodes" => ["node_token_hash"],
        "node_enrollment_requests" => ["pairing_key_hash", "node_token_hash"],
        "users" => ["password_hash"],
        "plugin_configurations" or "flow_plugin_configurations" or "ai_credentials" => ["encrypted_values"],
        _ => []
    };

    private static int CompareVersion(DateTimeOffset leftTime, Guid leftOrigin, long leftSequence, DateTimeOffset rightTime, Guid rightOrigin, long rightSequence)
    {
        var time = leftTime.CompareTo(rightTime);
        if (time != 0) return time;
        if (leftOrigin == rightOrigin) return leftSequence.CompareTo(rightSequence);
        return string.CompareOrdinal(leftOrigin.ToString("N"), rightOrigin.ToString("N"));
    }

    private static Guid ReadGuid(DbDataReader reader,
        int ordinal) => reader.GetValue(ordinal) is Guid value ? value : Guid.Parse(Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)!);
    private static DateTimeOffset ReadInstant(object value) => value switch
    {
        DateTimeOffset instant => instant.ToUniversalTime(),
        DateTime instant => new DateTimeOffset(DateTime.SpecifyKind(instant, DateTimeKind.Utc)),
        _ => DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
    };

    private sealed class EntityTable
    {
        public EntityTable(string name, string[] columns, string[] keyColumns, string[]? jsonColumns = null, string[]? arrayColumns = null,
            string[]? binaryColumns = null, string[]? fingerprintColumns = null, string[]? orderBy = null, string? filter = null, bool immutable = false,
            bool detectDeletes = true, string[]? preserveColumnsOnUpdate = null, string? deltaColumn = null)
        {
            Name = name; Columns = columns; KeyColumns = keyColumns; JsonColumns = jsonColumns ?? []; ArrayColumns = arrayColumns ?? [];
            BinaryColumns = binaryColumns ?? []; FingerprintColumns = fingerprintColumns; OrderBy = orderBy ?? []; Filter = filter; Immutable = immutable;
            DetectDeletes = detectDeletes; PreserveColumnsOnUpdate = preserveColumnsOnUpdate ?? []; DeltaColumn = deltaColumn;
        }
        public string Name { get; }
        public string[] Columns { get; }
        public string[] KeyColumns { get; }
        public string[] JsonColumns { get; }
        public string[] ArrayColumns { get; }
        public string[] BinaryColumns { get; }
        public string[]? FingerprintColumns { get; }
        public string[] OrderBy { get; }
        public string? Filter { get; }
        public bool Immutable { get; }
        public bool DetectDeletes { get; }
        public string[] PreserveColumnsOnUpdate { get; }
        public string? DeltaColumn { get; }
    }

    private sealed record EntitySnapshot(EntityTable Table, string Key, JsonObject Row, string Fingerprint, DateTimeOffset? CursorValue);
    private sealed record NodePresenceSnapshot(string Key, string LastSeenAt, string NodeId);
    private sealed record EntitySetEnvelope(string Table, JsonNode Key, string Fingerprint, DateTimeOffset OccurredAt, JsonObject Row);
    private sealed record EntityDeleteEnvelope(string Table, string Key);
    private sealed record NodePresenceEnvelope(string NodeId, string LastSeenAt);
    private sealed record EntityState(string Fingerprint, DateTimeOffset OccurredAt, Guid OriginId, long Sequence, bool IsDeleted);
    private sealed record LocalIdentity(Guid ClusterId, Guid OriginId, long NextSequence);
    private sealed record PendingChange(ClusterChangeEvent Change);
}
