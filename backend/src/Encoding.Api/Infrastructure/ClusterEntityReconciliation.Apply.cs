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
    /// <summary>Projette les événements reçus. Les événements qui attendent une FK sont conservés et retentés.</summary>
    public async Task<int> ApplyPendingAsync(CancellationToken ct)
    {
        var applied = 0;
        for (var pass = 0; pass < 4; pass++)
        {
            var pending = await ReadPendingAsync(ct);
            if (pending.Count == 0) break;
            var progress = 0;
            foreach (var change in pending.OrderBy(ProjectionRank).ThenBy(change => change.Change.OccurredAt).ThenBy(change => change.Change.Sequence))
            {
                ct.ThrowIfCancellationRequested();
                if (await ApplyOneAsync(change, ct)) { progress++; applied++; }
            }
            if (progress == 0) break;
        }
        return applied;
    }

    private async Task<IReadOnlyList<PendingChange>> ReadPendingAsync(CancellationToken ct)
    {
        var result = new List<PendingChange>();
        await using var command = dataSource.CreateCommand($"SELECT l.event_id,l.cluster_id,l.origin_id,l.sequence,l.event_type,l.payload,l.occurred_at FROM cluster_sync_pending_projections p JOIN cluster_change_log l ON l.event_id=p.event_id WHERE NOT EXISTS(SELECT 1 FROM cluster_projection_diagnostics d WHERE d.event_id=p.event_id AND d.cause='immutable_snapshot_conflict') ORDER BY l.occurred_at,l.origin_id,l.sequence LIMIT {ClusterReconciliationRepository.MaximumBatchSize * 8}");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var change = new ClusterChangeEvent(ReadGuid(reader, 0), ReadGuid(reader, 1), ReadGuid(reader, 2),
                Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture), reader.GetString(4), reader.GetString(5), ReadInstant(reader.GetValue(6)));
            result.Add(new PendingChange(change));
        }
        return result;
    }

    private async Task<bool> ApplyOneAsync(PendingChange pending, CancellationToken ct)
    {
        JsonObject? payload;
        try { payload = JsonNode.Parse(pending.Change.Payload) as JsonObject; }
        catch (JsonException) { return await DropPendingAsync(pending.Change.EventId, ct); }
        if (payload is null) return await DropPendingAsync(pending.Change.EventId, ct);

        string entityType;
        string key;
        string fingerprint;
        bool deleted;
        JsonObject? row = null;
        if (pending.Change.EventType == EntitySetType)
        {
            entityType = payload["table"]?.GetValue<string>() ?? "";
            row = payload["row"] as JsonObject;
            fingerprint = payload["fingerprint"]?.GetValue<string>() ?? "";
            deleted = false;
            if (!TryGetTable(entityType, out var table) || row is null
                || (entityType == "cluster_peer_admissions" && peerAdmissions is not null && !await peerAdmissions.VerifyIncomingGrantAsync(row, ct))
                || !TryUnprotectSensitive(pending.Change.ClusterId, entityType, row)) return await DropPendingAsync(pending.Change.EventId, ct);
            var incomingKey = payload["key"];
            key = MakeKey(row, table.KeyColumns);
            if (incomingKey is null || !JsonNode.DeepEquals(incomingKey, JsonNode.Parse(key))
                || string.IsNullOrWhiteSpace(fingerprint) || Fingerprint(row, table) != fingerprint)
                return await DropPendingAsync(pending.Change.EventId, ct);
        }
        else if (pending.Change.EventType == EntityDeleteType)
        {
            entityType = payload["table"]?.GetValue<string>() ?? "";
            key = payload["key"]?.GetValue<string>() ?? "";
            fingerprint = "deleted";
            deleted = true;
            if (!TryGetTable(entityType, out _) || string.IsNullOrWhiteSpace(key)) return await DropPendingAsync(pending.Change.EventId, ct);
        }
        else if (pending.Change.EventType == NodePresenceType)
        {
            entityType = "node-presence";
            var nodeId = payload["nodeId"]?.GetValue<string>() ?? "";
            var lastSeenAt = payload["lastSeenAt"]?.GetValue<string>() ?? "";
            if (!Guid.TryParse(nodeId, out _) || !DateTimeOffset.TryParse(lastSeenAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
                return await DropPendingAsync(pending.Change.EventId, ct);
            key = JsonSerializer.Serialize(nodeId);
            fingerprint = Fingerprint(lastSeenAt);
            deleted = false;
        }
        else return await DropPendingAsync(pending.Change.EventId, ct);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var sequenceLock = Sql.Command("SELECT origin_id FROM cluster_sync_identity WHERE singleton = true FOR UPDATE", connection, transaction))
            await sequenceLock.ExecuteScalarAsync(ct);
        if (entityType == "flow_draft_backups" && row is not null)
        {
            await using var existing = Sql.Command("SELECT definition FROM flow_draft_backups WHERE " + KeyPredicate(GetTable(entityType)), connection, transaction);
            existing.Parameters.AddWithValue("key", key);
            var definition = await existing.ExecuteScalarAsync(ct);
            if (definition is string text)
            {
                if (!JsonNode.DeepEquals(JsonNode.Parse(text), row["definition"]))
                {
                    await transaction.RollbackAsync(ct);
                    await RecordProjectionFailureAsync(pending.Change.EventId, entityType, "immutable_snapshot_conflict", ct);
                    return false;
                }
                // Keep the local representation fingerprint: a different JSON property order must
                // not be recaptured as a local edit and echoed back through the mesh.
                var stored = (JsonObject)row.DeepClone();
                stored["definition"] = JsonNode.Parse(text);
                fingerprint = Fingerprint(stored, GetTable(entityType));
            }
        }
        var current = await ReadEntityStateAsync(connection, transaction, entityType, key, ct);
        if (current is { } currentState && CompareVersion(pending.Change.OccurredAt, pending.Change.OriginId, pending.Change.Sequence,
                currentState.OccurredAt, currentState.OriginId, currentState.Sequence) <= 0)
        {
            await DeletePendingAsync(connection, transaction, pending.Change.EventId, ct);
            await transaction.CommitAsync(ct);
            return true;
        }

        try
        {
            if (pending.Change.EventType == EntitySetType)
            {
                if (entityType == ProcessingReceiptTable.Name)
                {
                    if (!await UpsertProcessingReceiptAsync(connection, transaction, row!, ct)) return false;
                }
                else
                {
                    await UpsertRowAsync(connection, transaction, GetTable(entityType), row!, ct);
                    await EnsureFlowBackupProjectionAsync(entityType, row!, connection, transaction, ct);
                }
            }
            else if (pending.Change.EventType == EntityDeleteType)
            {
                if (entityType == ProcessingReceiptTable.Name)
                    await DeleteProcessingReceiptAsync(connection, transaction, payload["key"]!.GetValue<string>(), ct);
                else await DeleteRowAsync(connection, transaction, GetTable(entityType), payload["key"]!.GetValue<string>(), ct);
            }
            else
            {
                var nodeId = payload["nodeId"]!.GetValue<string>();
                var lastSeenAt = payload["lastSeenAt"]!.GetValue<string>();
                await using var update = Sql.Command("UPDATE cluster_nodes SET last_seen_at = CASE WHEN last_seen_at < @last_seen_at THEN @last_seen_at ELSE last_seen_at END WHERE id = @id",
                    connection, transaction);
                update.Parameters.AddWithValue("last_seen_at", lastSeenAt);
                update.Parameters.AddWithValue("id", Guid.Parse(nodeId));
                if (await update.ExecuteNonQueryAsync(ct) == 0) return false;
            }
        }
        catch (DbException exception)
        {
            await transaction.RollbackAsync(ct);
            var cause = exception is Microsoft.Data.Sqlite.SqliteException sqlite
                ? sqlite.SqliteExtendedErrorCode switch {
                    787 => "dependency_missing",
                    2067 when entityType == "users" => "username_conflict",
                    2067 or 1555 or 1811 or 275 => "constraint_conflict",
                    _ => "storage_unavailable"
                } : "storage_unavailable";
            await RecordProjectionFailureAsync(pending.Change.EventId, entityType, cause, ct);
            return false;
        }

        await UpsertEntityStateAsync(connection, transaction, entityType, key, fingerprint, pending.Change.OccurredAt, pending.Change.OriginId, pending.Change.Sequence, deleted, ct);
        await DeletePendingAsync(connection, transaction, pending.Change.EventId, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private async Task<bool> DropPendingAsync(Guid eventId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM cluster_sync_pending_projections WHERE event_id=@id");
        command.Parameters.AddWithValue("id", eventId);
        await command.ExecuteNonQueryAsync(ct);
        return true;
    }

    private static int ProjectionRank(PendingChange pending)
    {
        try
        {
            if (pending.Change.EventType == NodePresenceType) return Tables.Length + 1;
            var tableName = JsonDocument.Parse(pending.Change.Payload).RootElement.GetProperty("table").GetString();
            var rank = Array.FindIndex(Tables, table => table.Name == tableName);
            if (tableName == "library_media") rank = Array.FindIndex(Tables, table => table.Name == "library_scans") + 1;
            if (tableName == ProcessingReceiptTable.Name) rank = Tables.Length + 2;
            return pending.Change.EventType == EntityDeleteType ? -rank : rank;
        }
        catch { return Tables.Length + 2; }
    }

    private JsonObject ReadRow(DbDataReader reader, EntityTable table)
    {
        var row = new JsonObject();
        for (var index = 0; index < table.Columns.Length; index++)
        {
            var value = reader.GetValue(index);
            var name = table.Columns[index];
            if (value is DBNull) { row[name] = null; continue; }
            if (table.JsonColumns.Contains(name, StringComparer.Ordinal))
            {
                var json = value switch
                {
                    JsonDocument document => document.RootElement.GetRawText(),
                    JsonElement element => element.GetRawText(),
                    string text => text,
                    _ => JsonSerializer.Serialize(value, JsonOptions)
                };
                try { row[name] = JsonNode.Parse(json); }
                catch (JsonException) { row[name] = JsonValue.Create(json); }
            }
            else if (table.ArrayColumns.Contains(name, StringComparer.Ordinal))
            {
                var items = value switch
                {
                    string[] array => array,
                    string json => JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [],
                    _ => []
                };
                row[name] = JsonSerializer.SerializeToNode(items, JsonOptions);
            }
            else if (table.BinaryColumns.Contains(name, StringComparer.Ordinal) && value is byte[] pluginCipher
                     && name == "encrypted_values" && (table.Name is "plugin_configurations" or "flow_plugin_configurations" or "ai_credentials"))
                row[name] = JsonNode.Parse(LocalProtector(table.Name).Unprotect(pluginCipher));
            else if (table.BinaryColumns.Contains(name, StringComparer.Ordinal) && value is byte[] bytes)
                row[name] = Convert.ToHexString(bytes).ToLowerInvariant();
            else if (BooleanColumns.Contains(name) || (name == "id" && (table.Name is "enrollment_window" or "first_link_acceptance")))
                row[name] = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            else
                row[name] = JsonSerializer.SerializeToNode(NormalizeValue(value), JsonOptions);
        }
        return row;
    }

    private static object? NormalizeValue(object value) => value switch
    {
        DateTimeOffset instant => instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTime instant => new DateTimeOffset(DateTime.SpecifyKind(instant, DateTimeKind.Utc)).ToString("O", CultureInfo.InvariantCulture),
        Guid id => id.ToString(),
        _ => value
    };

    private static string MakeKey(JsonObject row, string[] columns)
    {
        var key = new JsonObject();
        foreach (var column in columns) key[column] = row[column]?.DeepClone();
        return key.ToJsonString(JsonOptions);
    }

    private static string Fingerprint(JsonObject row, EntityTable table)
    {
        var included = table.FingerprintColumns ?? table.Columns;
        var stable = new JsonObject();
        foreach (var column in included) stable[column] = row[column]?.DeepClone();
        return Fingerprint(stable.ToJsonString(JsonOptions));
    }

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private string CreateSetPayload(EntitySnapshot snapshot, DateTimeOffset occurredAt, Guid clusterId)
    {
        var row = (JsonObject)snapshot.Row.DeepClone();
        ProtectSensitive(clusterId, snapshot.Table.Name, row);
        return new JsonObject
        {
            ["table"] = snapshot.Table.Name,
            ["key"] = JsonNode.Parse(snapshot.Key),
            ["fingerprint"] = snapshot.Fingerprint,
            ["occurredAt"] = occurredAt.ToString("O", CultureInfo.InvariantCulture),
            ["row"] = row
        }.ToJsonString(JsonOptions);
    }

    private void ProtectSensitive(Guid clusterId, string table, JsonObject row)
    {
        foreach (var column in SensitiveColumns(table))
        {
            if (!row.TryGetPropertyValue(column, out var value) || value is null || IsEncrypted(value)) continue;
            var raw = System.Text.Encoding.UTF8.GetBytes(value.ToJsonString(JsonOptions));
            row[column] = new JsonObject { ["encrypted"] = ClusterPayloadProtection.Protect(dataKeyStore.GetRequired(clusterId),raw,table+"."+column) };
        }
    }

    private bool TryUnprotectSensitive(Guid clusterId, string table, JsonObject row)
    {
        try
        {
            foreach (var column in SensitiveColumns(table))
            {
                if (!row.TryGetPropertyValue(column, out var value) || value is null || !IsEncrypted(value)) continue;
                row[column] = JsonNode.Parse(ClusterPayloadProtection.Unprotect(dataKeyStore.GetRequired(clusterId),value["encrypted"]!.GetValue<string>(),table+"."+column));
            }
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsEncrypted(JsonNode node) => node is JsonObject obj && obj.Count == 1 && obj.ContainsKey("encrypted");
}
