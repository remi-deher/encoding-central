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

/// <summary>
/// Réconcilie les données durables du cluster sous forme d'entités LWW. Les files d'exécution, baux,
/// réservations, sessions et secrets DP locaux restent en dehors de cette liste.
/// </summary>
public sealed partial class ClusterEntityReconciliation(
    DbDataSource dataSource,
    ClusterDataKeyStore dataKeyStore,
    IDataProtectionProvider protection,
    ILogger<ClusterEntityReconciliation> logger,
    ClusterPeerAdmissionRepository? peerAdmissions = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector pluginProtector = protection.CreateProtector("Encoding.PluginConfiguration.v1");
    private readonly IDataProtector aiCredentialProtector = protection.CreateProtector(AiCredentialStore.Purpose);
    private IDataProtector LocalProtector(string table) => table == "ai_credentials" ? aiCredentialProtector : pluginProtector;
    private static readonly HashSet<string> BooleanColumns = new(StringComparer.Ordinal)
    {
        "singleton", "idempotent", "is_active", "draining", "quarantined", "can_read", "can_write", "is_enabled",
        "watch_enabled", "schedule_enabled", "enabled", "has_more", "approved"
    };
    private static readonly EntityTable[] Tables =
    [
        new("cluster_nodes", ["id", "name", "roles", "max_concurrent_tasks", "capabilities", "last_seen_at", "created_at", "deleted_at", "display_name",
            "labels", "inventory", "desired_roles", "draining", "quarantined", "incompatible_reason", "protocol_version", "node_token_hash"], ["id"],
            ["capabilities", "labels", "inventory"], ["roles", "desired_roles"], ["node_token_hash"], ["id", "name", "roles", "max_concurrent_tasks",
            "capabilities", "created_at", "deleted_at", "display_name", "labels", "inventory", "desired_roles", "draining", "quarantined",
            "incompatible_reason", "protocol_version", "node_token_hash"]),
        new("node_enrollment_requests", ["node_id", "pairing_key_hash", "node_token_hash", "peer_id", "peer_public_key", "name", "roles",
            "max_concurrent_tasks", "capabilities", "created_at", "expires_at", "inventory", "display_name"], ["node_id"], ["capabilities", "inventory"],
            ["roles"], ["pairing_key_hash", "node_token_hash"]),
        new("node_peer_reconciliation_urls", ["node_id", "peer_id", "url", "approved"], ["node_id"]),
        new("cluster_peer_admissions", ["cluster_id", "peer_id", "node_id", "public_key", "approved_by_peer_id", "wrapped_data_key", "key_fingerprint",
            "issued_at", "revoked_at", "signature"], ["cluster_id", "peer_id"], immutable: false, detectDeletes: false),
        new("node_groups", ["id", "name", "description", "mode", "criteria", "labels", "created_at", "updated_at"], ["id"], ["criteria", "labels"]),
        new("node_directory_browse_requests", ["id", "node_id", "path", "state", "entries", "has_more", "error", "created_at", "updated_at", "expires_at", "claim_until"], ["id"], ["entries"]),
        new("libraries", ["id", "name", "created_at", "is_active", "flow_assignment_revision"], ["id"]),
        new("flows", ["id", "current_version", "updated_at", "deleted_at"], ["id"]),
        new("flow_scripts", ["id", "draft_revision", "draft_name", "draft_language", "draft_description", "draft_code", "published_version", "created_at", "updated_at"], ["id"]),
        new("encoding_profiles", ["id", "draft_revision", "draft_name", "draft_description", "draft_targets", "published_version", "created_at", "updated_at"], ["id"], ["draft_targets"]),
        new("users", ["id", "username", "password_hash", "role", "is_active", "failed_attempts", "locked_until", "created_at", "updated_at"], ["id"]),
        new("api_tokens", ["id", "name", "token_hash", "token_prefix", "role", "is_active", "created_at", "expires_at", "last_used_at", "revoked_at", "scopes"], ["id"], binaryColumns: ["token_hash"]),
        new("ai_credentials", ["provider", "revision", "encrypted_values", "updated_at"], ["provider"], binaryColumns: ["encrypted_values"]),
        new("plugin_configurations", ["plugin_id", "revision", "encrypted_values", "updated_at"], ["plugin_id"], binaryColumns: ["encrypted_values"]),
        new("flow_plugin_configurations", ["flow_id", "plugin_id", "revision", "encrypted_values", "updated_at"], ["flow_id", "plugin_id"], binaryColumns: ["encrypted_values"]),
        new("enrollment_window", ["id", "open_until"], ["id"]),
        new("first_link_acceptance", ["id", "expires_at", "enabled_by"], ["id"]),
        new("node_processing_settings", ["node_id", "max_concurrent_tasks", "revision", "schedule_enabled", "schedule_timezone", "schedule_slots"], ["node_id"]),
        new("node_group_members", ["group_id", "node_id"], ["group_id", "node_id"]),
        new("flow_versions", ["flow_id", "version", "definition", "created_at"], ["flow_id", "version"], ["definition"], immutable: true),
        // The backup file and its retry status are server-local. Replicate only the outbox identity;
        // each server will create and verify its own snapshot from the replicated definition.
        new("flow_backup_jobs", ["flow_id", "version"], ["flow_id", "version"], immutable: true, detectDeletes: false),
        new("flow_drafts", ["flow_id", "revision", "definition", "updated_at"], ["flow_id"], ["definition"]),
        // Historical draft definitions are cluster data; status/check timestamps remain local.
        new("flow_draft_backups", ["flow_id", "revision", "definition"], ["flow_id", "revision"], ["definition"], immutable: true, detectDeletes: false),
        new("flow_script_versions", ["script_id", "version", "name", "language", "description", "code", "created_at"], ["script_id", "version"], immutable: true),
        new("encoding_profile_versions", ["profile_id", "version", "name", "description", "targets", "created_at"], ["profile_id", "version"], ["targets"], immutable: true),
        new("flow_resources", ["id", "kind", "name", "description", "created_at", "updated_at"], ["id"]),
        new("flow_resource_versions", ["resource_id", "version", "files", "total_bytes", "created_at"], ["resource_id", "version"], ["files"], immutable: true),
        new("flow_templates", ["id", "name", "description", "source_flow_id", "source_version", "source_flow_name", "media_type", "definition", "created_at",
            "configuration", "revision"], ["id"], ["definition", "configuration"]),
        new("flow_component_packs", ["id", "name", "description", "source_flow_id", "source_version", "source_flow_name", "media_type", "input_node_id",
            "definition", "outputs", "created_at"], ["id"], ["definition", "outputs"]),
        new("library_flow_assignments", ["library_id", "flow_id", "flow_version", "revision"], ["library_id"]),
        new("library_node_accesses", ["library_id", "node_id", "mode", "local_path", "can_read", "can_write", "transfer_source_node_id", "updated_at", "is_enabled"], ["library_id", "node_id"]),
        new("library_node_processing_limits", ["library_id", "node_id", "max_concurrent_tasks"], ["library_id", "node_id"]),
        new("library_processing_settings", ["library_id", "watch_enabled", "scan_interval_seconds", "max_concurrent_tasks", "revision"], ["library_id"]),
        new("library_replacement_policies", ["library_id", "enabled", "grace_period_hours", "revision"], ["library_id"]),
        new("executions", ["id", "media", "library_name", "flow_name", "flow_version", "flow_definition", "state", "node_id", "progress", "detail", "attempt",
            "retry_of", "revision", "queued_at", "started_at", "finished_at", "updated_at"], ["id"], ["flow_definition"], orderBy: ["queued_at", "id"],
            detectDeletes: false, deltaColumn: "updated_at"),
        new("execution_events", ["id", "execution_id", "revision", "state", "detail", "created_at"], ["id"], immutable: true, detectDeletes: false, deltaColumn: "created_at"),
        new("execution_steps", ["execution_id", "node_id", "invocation_id", "started_at", "completed_at", "completed_revision", "idempotent", "checkpoint",
            "completed_epoch", "completed_worker_node_id", "command_logs"], ["execution_id", "node_id"], ["checkpoint", "command_logs"]),
        new("execution_artifacts", ["execution_id", "sha256", "byte_length"], ["execution_id", "sha256"], immutable: true),
        new("execution_inputs", ["execution_id", "input_path", "sha256"], ["execution_id", "input_path"], immutable: true),
        new("library_scans", ["id", "library_id", "media_type", "state", "node_id", "created_at", "completed_at", "detail"], ["id"],
            filter: "state = 'completed' AND id = (SELECT latest.id FROM library_scans latest WHERE latest.library_id=library_scans.library_id AND latest.state='completed' ORDER BY latest.completed_at DESC,latest.id DESC LIMIT 1)",
            immutable: true, detectDeletes: false),
        new("media_replacement_marks", ["library_id", "relative_path", "byte_length", "modified_at", "execution_id"], ["library_id", "relative_path", "byte_length", "modified_at"], immutable: true)
    ];
    private static readonly EntityTable MediaTable = new("library_media", ["id", "library_id", "relative_path", "byte_length", "modified_at", "scan_id",
        "stable_since"], ["library_id", "relative_path"], fingerprintColumns: ["library_id", "relative_path", "byte_length", "modified_at", "stable_since"],
        preserveColumnsOnUpdate: ["id", "scan_id"]);
    // Receipts use the media path as their cross-node identity. SQLite nodes may assign different
    // library_media IDs when they scan the same file independently.
    private static readonly EntityTable ProcessingReceiptTable = new("library_processing_receipts",
        ["library_id", "relative_path", "flow_id", "flow_version", "byte_length", "modified_at", "execution_id"],
        ["library_id", "relative_path", "flow_id", "flow_version", "byte_length", "modified_at"],
        fingerprintColumns: ["library_id", "relative_path", "flow_id", "flow_version", "byte_length", "modified_at", "execution_id"],
        detectDeletes: false);

    public const int MaximumCaptureBatchSize = 64;

    private const string EntitySetType = "entity.set";
    private const string EntityDeleteType = "entity.delete";
    private const string NodePresenceType = "node.presence";

    /// <summary>Capture les changements locaux depuis le dernier passage et les ajoute au journal avant le prochain échange.</summary>
    public async Task<int> CaptureLocalChangesAsync(CancellationToken ct)
    {
        var snapshots = new List<EntitySnapshot>();
        var presence = new List<NodePresenceSnapshot>();
        var changedScans = new List<(string LibraryId, string ScanId)>();
        var preparedMedia = new Dictionary<(string LibraryId, string ScanId), List<EntitySnapshot>>();
        DateTimeOffset? receiptMaxCursor = null;
        Dictionary<string, CaptureSelection> selections;
        await using (var readConnection = await dataSource.OpenConnectionAsync(ct))
        {
            await using var readTransaction = await readConnection.BeginTransactionAsync(System.Data.IsolationLevel.Snapshot, ct);
            selections = await ReadCaptureSelectionsAsync(readConnection, readTransaction, ct);
            foreach (var table in Tables.Where(table => selections.ContainsKey(table.Name)))
            {
                var selection = selections[table.Name];
                var predicates = new List<string>();
                if (table.Filter is not null) predicates.Add(table.Filter);
                if (!selection.FullScan) predicates.Add(SelectedKeysPredicate(table));
                var where = predicates.Count == 0 ? "" : " WHERE " + string.Join(" AND ", predicates);
                await using var command = Sql.Command($"SELECT {string.Join(",", table.Columns)} FROM {table.Name}{where}{(table.OrderBy.Length == 0 ? "" : " ORDER BY " + string.Join(",", table.OrderBy))}",
                    readConnection);
                BindSelectedKeys(command, selection);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var row = ReadRow(reader, table);
                    var key = MakeKey(row, table.KeyColumns);
                    DateTimeOffset? cursorValue = null;
                    if (table.DeltaColumn is not null)
                    {
                        var ordinal = Array.IndexOf(table.Columns, table.DeltaColumn);
                        if (ordinal >= 0 && reader.GetValue(ordinal) is not DBNull) cursorValue = ReadInstant(reader.GetValue(ordinal));
                    }
                    snapshots.Add(new(table, key, row, Fingerprint(row, table), cursorValue));
                    if (table.Name == "cluster_nodes")
                        presence.Add(new(key, row["last_seen_at"]?.GetValue<string>() ?? "", row["id"]?.GetValue<string>() ?? ""));
                }
            }

            // A receipt refers to library_media.id, which is local to a scan on SQLite. Carry its
            // stable path instead and resolve the destination's local media ID when projecting it.
            if (selections.TryGetValue(ProcessingReceiptTable.Name, out var receiptSelection))
            {
                var receiptFilter = receiptSelection.FullScan ? "" : " WHERE " + SelectedKeysPredicate(ProcessingReceiptTable, "r");
                await using var receipts = Sql.Command($"""
                    SELECT r.library_id,m.relative_path,r.flow_id,r.flow_version,r.byte_length,r.modified_at,r.execution_id,e.updated_at
                    FROM library_processing_receipts r
                    JOIN library_media m ON m.id=r.file_id
                    JOIN executions e ON e.id=r.execution_id
                    {receiptFilter}
                    ORDER BY e.updated_at,r.library_id,m.relative_path
                    """, readConnection);
                BindSelectedKeys(receipts, receiptSelection);
                await using (var reader = await receipts.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        var row = ReadRow(reader, ProcessingReceiptTable);
                        var key = MakeKey(row, ProcessingReceiptTable.KeyColumns);
                        var cursor = ReadInstant(reader.GetValue(7));
                        if (receiptMaxCursor is null || cursor > receiptMaxCursor) receiptMaxCursor = cursor;
                        snapshots.Add(new(ProcessingReceiptTable, key, row, Fingerprint(row, ProcessingReceiptTable), cursor));
                    }
                }
            }

            // Inventory hashing is done before writer admission. A completed scan is checkpointed
            // only after all its media events have committed, so cancellation between batches is resumable.
            foreach (var scan in snapshots.Where(item => item.Table.Name == "library_scans"))
            {
                var libraryId = scan.Row["library_id"]!.GetValue<string>();
                var scanId = scan.Row["id"]!.GetValue<string>();
                if (await ReadTableCursorAsync(readConnection, MediaInventoryCursor(scanId), ct) is not null) continue;
                var mediaRows = new List<EntitySnapshot>();
                await using var media = Sql.Command($"SELECT {string.Join(",", MediaTable.Columns)} FROM library_media WHERE library_id=@library AND scan_id=@scan ORDER BY relative_path",
                    readConnection);
                media.Parameters.AddWithValue("library", Guid.Parse(libraryId));
                media.Parameters.AddWithValue("scan", Guid.Parse(scanId));
                await using var reader = await media.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var row = ReadRow(reader, MediaTable);
                    mediaRows.Add(new(MediaTable, MakeKey(row, MediaTable.KeyColumns), row, Fingerprint(row, MediaTable), null));
                }
                preparedMedia[(libraryId, scanId)] = mediaRows;
            }

        }

        logger.LogDebug(new EventId(9010, "CaptureSelectionRead"), "Capture locale : {Tables} tables modifiées, {Rows} lignes préparées.", selections.Count, snapshots.Count);
        if (selections.Count == 0) return 0;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var capture = await CaptureWriter.CreateAsync(this, connection, ct);
        var local = capture.Identity;
        var captured = 0;
        foreach (var table in Tables.Where(table => selections.ContainsKey(table.Name)))
        {
            var selection = selections[table.Name];
            var tableGroup = snapshots.Where(item => item.Table.Name == table.Name).ToArray();
            var existing = await ReadEntityStatesAsync(connection, capture.Transaction, table.Name, ct, selection.FullScan ? null : selection.Keys);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var snapshot in tableGroup)
            {
                seen.Add(snapshot.Key);
                if (table.Name == "library_scans")
                {
                    var scanId = snapshot.Row["id"]!.GetValue<string>();
                    if (await ReadTableCursorAsync(connection, MediaInventoryCursor(scanId), ct) is null)
                        changedScans.Add((snapshot.Row["library_id"]!.GetValue<string>(), scanId));
                }
                if (existing.TryGetValue(snapshot.Key, out var state) && !state.IsDeleted && state.Fingerprint == snapshot.Fingerprint) continue;
                if (!await CanCaptureAsync(snapshot, connection, capture.Transaction, ct)) continue;
                var occurredAt = DateTimeOffset.UtcNow;
                var sequence = ++capture.NextSequence;
                var payload = CreateSetPayload(snapshot, occurredAt, local.ClusterId);
                await InsertLocalEventAsync(connection, capture.Transaction, local.ClusterId, local.OriginId, sequence, EntitySetType, payload, occurredAt, ct);
                await UpsertEntityStateAsync(connection, capture.Transaction, table.Name, snapshot.Key, snapshot.Fingerprint, occurredAt, local.OriginId, sequence, false, ct);
                captured++;
                await capture.AdvanceAsync(ct);
            }
            foreach (var (key, state) in existing)
            {
                if (!table.DetectDeletes || state.IsDeleted || seen.Contains(key)) continue;
                if (!await CanDeleteAsync(table, key, connection, capture.Transaction, ct)) continue;
                var occurredAt = DateTimeOffset.UtcNow;
                var sequence = ++capture.NextSequence;
                var payload = JsonSerializer.Serialize(new EntityDeleteEnvelope(table.Name, key), JsonOptions);
                await InsertLocalEventAsync(connection, capture.Transaction, local.ClusterId, local.OriginId, sequence, EntityDeleteType, payload, occurredAt, ct);
                await UpsertEntityStateAsync(connection, capture.Transaction, table.Name, key, "deleted", occurredAt, local.OriginId, sequence, true, ct);
                captured++;
                await capture.AdvanceAsync(ct);
            }
            if (table.DeltaColumn is not null && tableGroup.Select(item => item.CursorValue).Where(value => value.HasValue).Max() is { } maxCursor)
                await UpsertTableCursorAsync(connection, capture.Transaction, table.Name, maxCursor, ct);
        }

        var receiptStates = selections.TryGetValue(ProcessingReceiptTable.Name, out var receiptWork)
            ? await ReadEntityStatesAsync(connection, capture.Transaction, ProcessingReceiptTable.Name, ct, receiptWork.FullScan ? null : receiptWork.Keys)
            : new Dictionary<string, EntityState>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots.Where(item => item.Table.Name == ProcessingReceiptTable.Name))
        {
            if (receiptStates.TryGetValue(snapshot.Key, out var state) && !state.IsDeleted && state.Fingerprint == snapshot.Fingerprint) continue;
            if (!await CanCaptureAsync(snapshot, connection, capture.Transaction, ct)) continue;
            var occurredAt = DateTimeOffset.UtcNow;
            var sequence = ++capture.NextSequence;
            await InsertLocalEventAsync(connection, capture.Transaction, local.ClusterId, local.OriginId, sequence, EntitySetType, CreateSetPayload(snapshot,
                occurredAt, local.ClusterId), occurredAt, ct);
            await UpsertEntityStateAsync(connection, capture.Transaction, ProcessingReceiptTable.Name, snapshot.Key, snapshot.Fingerprint, occurredAt, local.OriginId, sequence, false, ct);
            captured++;
            await capture.AdvanceAsync(ct);
        }
        if (receiptMaxCursor is { } receiptCursorValue)
            await UpsertTableCursorAsync(connection, capture.Transaction, ProcessingReceiptTable.Name, receiptCursorValue, ct);

        var mediaStates = changedScans.Count == 0 ? new Dictionary<string, EntityState>(StringComparer.Ordinal) : await ReadEntityStatesAsync(connection, capture.Transaction, MediaTable.Name, ct);
        foreach (var (libraryId, scanId) in changedScans)
        {
            if (!preparedMedia.TryGetValue((libraryId, scanId), out var mediaSnapshots)) continue;
            var seenMedia = mediaSnapshots.Select(snapshot => snapshot.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var snapshot in mediaSnapshots)
            {
                if (mediaStates.TryGetValue(snapshot.Key, out var mediaState) && !mediaState.IsDeleted && mediaState.Fingerprint == snapshot.Fingerprint) continue;
                if (!await CanCaptureAsync(snapshot, connection, capture.Transaction, ct)) continue;
                var occurredAt = DateTimeOffset.UtcNow;
                var sequence = ++capture.NextSequence;
                var payload = CreateSetPayload(snapshot, occurredAt, local.ClusterId);
                await InsertLocalEventAsync(connection, capture.Transaction, local.ClusterId, local.OriginId, sequence, EntitySetType, payload, occurredAt, ct);
                await UpsertEntityStateAsync(connection, capture.Transaction, MediaTable.Name, snapshot.Key, snapshot.Fingerprint, occurredAt, local.OriginId, sequence, false, ct);
                captured++;
                await capture.AdvanceAsync(ct);
            }
            foreach (var (key, state) in mediaStates)
            {
                if (state.IsDeleted || seenMedia.Contains(key) || !KeyBelongsToLibrary(key, libraryId)) continue;
                if (!await CanDeleteAsync(MediaTable, key, connection, capture.Transaction, ct)) continue;
                var occurredAt = DateTimeOffset.UtcNow;
                var sequence = ++capture.NextSequence;
                var payload = JsonSerializer.Serialize(new EntityDeleteEnvelope(MediaTable.Name, key), JsonOptions);
                await InsertLocalEventAsync(connection, capture.Transaction, local.ClusterId, local.OriginId, sequence, EntityDeleteType, payload, occurredAt, ct);
                await UpsertEntityStateAsync(connection, capture.Transaction, MediaTable.Name, key, "deleted", occurredAt, local.OriginId, sequence, true, ct);
                captured++;
                await capture.AdvanceAsync(ct);
            }
            await UpsertTableCursorAsync(connection, capture.Transaction, MediaInventoryCursor(scanId), DateTimeOffset.UtcNow, ct);

        }

        foreach (var snapshot in presence)
        {
            if (string.IsNullOrWhiteSpace(snapshot.LastSeenAt) || !DateTimeOffset.TryParse(snapshot.LastSeenAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var seenAt)) continue;
            var key = JsonSerializer.Serialize(snapshot.NodeId);
            var fingerprint = Fingerprint(snapshot.LastSeenAt);
            var existing = await ReadEntityStateAsync(connection, capture.Transaction, "node-presence", key, ct);
            if (existing is { IsDeleted: false } state && state.Fingerprint == fingerprint) continue;
            await using (var currentPresence = Sql.Command("SELECT last_seen_at FROM cluster_nodes WHERE id=@id", connection, capture.Transaction))
            {
                currentPresence.Parameters.AddWithValue("id", Guid.Parse(snapshot.NodeId));
                var currentSeen = await currentPresence.ExecuteScalarAsync(ct);
                if (currentSeen is null or DBNull || ReadInstant(currentSeen) != seenAt.ToUniversalTime()) continue;
            }
            var sequence = ++capture.NextSequence;
            var occurredAt = seenAt.ToUniversalTime();
            var payload = JsonSerializer.Serialize(new NodePresenceEnvelope(snapshot.NodeId, snapshot.LastSeenAt), JsonOptions);
            await InsertLocalEventAsync(connection, capture.Transaction, local.ClusterId, local.OriginId, sequence, NodePresenceType, payload, occurredAt, ct);
            await UpsertEntityStateAsync(connection, capture.Transaction, "node-presence", key, fingerprint, occurredAt, local.OriginId, sequence, false, ct);
            captured++;
            await capture.AdvanceAsync(ct);
        }

        await AcknowledgeCaptureAsync(connection, capture.Transaction, selections.Values, ct);
        await capture.CommitAsync(ct);
        if (captured > 0) logger.LogInformation("{Count} changements locaux ajoutés au journal de réconciliation.", captured);
        return captured;
    }

    private static string MediaInventoryCursor(string scanId) => "scan-inventory/" + scanId;

    private async Task<bool> CanCaptureAsync(EntitySnapshot snapshot, DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        var current = await ReadEntityStateAsync(connection, transaction, snapshot.Table.Name, snapshot.Key, ct);
        if (current is { IsDeleted: false } state && state.Fingerprint == snapshot.Fingerprint) return false;
        var table = snapshot.Table;
        var select = table.Name == ProcessingReceiptTable.Name
            ? "SELECT r.library_id,m.relative_path,r.flow_id,r.flow_version,r.byte_length,r.modified_at,r.execution_id FROM library_processing_receipts r JOIN library_media m ON m.id=r.file_id"
            : $"SELECT {string.Join(",", table.Columns)} FROM {table.Name}";
        var predicate = table.Name == ProcessingReceiptTable.Name
            ? string.Join(" AND ", table.KeyColumns.Select(column => (column == "relative_path" ? "m." : "r.") + column + "=json_extract(@key, '$." + column + "')"))
            : KeyPredicate(table);
        await using var command = Sql.Command(select + " WHERE " + predicate, connection, transaction);
        command.Parameters.AddWithValue("key", snapshot.Key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) && Fingerprint(ReadRow(reader, table), table) == snapshot.Fingerprint;
    }

    private async Task<bool> CanDeleteAsync(EntityTable table, string key, DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        var state = await ReadEntityStateAsync(connection, transaction, table.Name, key, ct);
        if (state is null or { IsDeleted: true }) return false;
        await using var exists = Sql.Command($"SELECT EXISTS(SELECT 1 FROM {table.Name} WHERE {KeyPredicate(table)})", connection, transaction);
        exists.Parameters.AddWithValue("key", key);
        return !await exists.ScalarBoolAsync(ct);
    }

    private static string KeyPredicate(EntityTable table) => string.Join(" AND ", table.KeyColumns.Select(column => column + "=json_extract(@key, '$." + column + "')"));

    private void CaptureBatchCommitted(int count) => logger.LogDebug(new EventId(9009, "CaptureBatchCommitted"), "Lot de capture validé : {Count} événements.", count);

    /// <summary>Each committed batch owns sequence allocation, events, entity states and finished cursors atomically.</summary>
    private sealed class CaptureWriter(ClusterEntityReconciliation owner, DbConnection connection) : IAsyncDisposable
    {
        private DbTransaction? transaction;
        private int count;
        public LocalIdentity Identity { get; private set; } = null!;
        public long NextSequence { get; set; }
        public DbTransaction Transaction => transaction ?? throw new InvalidOperationException("Lot de capture fermé.");

        public static async Task<CaptureWriter> CreateAsync(ClusterEntityReconciliation owner, DbConnection connection, CancellationToken ct)
        {
            var writer = new CaptureWriter(owner, connection);
            try { await writer.BeginAsync(ct); return writer; }
            catch { await writer.DisposeAsync(); throw; }
        }

        private async Task BeginAsync(CancellationToken ct)
        {
            transaction = await connection.BeginTransactionAsync(ct);
            var identity = await owner.ReadLocalIdentityAsync(connection, transaction, ct);
            if (Identity is not null && (identity.ClusterId != Identity.ClusterId || identity.OriginId != Identity.OriginId))
                throw new InvalidOperationException("L’identité du cluster a changé pendant la capture ; le prochain passage reprendra la capture.");
            Identity = identity;
            NextSequence = identity.NextSequence;
        }

        public async Task AdvanceAsync(CancellationToken ct)
        {
            if (++count < MaximumCaptureBatchSize) return;
            await CommitAsync(ct);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            await BeginAsync(ct);
        }

        public async Task CommitAsync(CancellationToken ct)
        {
            if (transaction is null) return;
            var finishing = transaction;
            transaction = null;
            try
            {
                if (NextSequence != Identity.NextSequence)
                {
                    await using var update = Sql.Command("UPDATE cluster_sync_identity SET next_sequence=@sequence WHERE singleton=true", connection, finishing);
                    update.Parameters.AddWithValue("sequence", NextSequence);
                    await update.ExecuteNonQueryAsync(ct);
                }
                await finishing.CommitAsync(ct);
            }
            finally { await finishing.DisposeAsync(); }
            owner.CaptureBatchCommitted(count);
            count = 0;
        }

        public async ValueTask DisposeAsync()
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }
}
