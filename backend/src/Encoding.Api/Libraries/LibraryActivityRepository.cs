using System.Data.Common;
using Encoding.Api.Executions;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Libraries;

namespace Encoding.Api.Libraries;

public sealed class LibraryActivityRepository(DbDataSource dataSource, ExecutionStreamBroadcaster? broadcaster = null)
{
    private long _cachedCountsVersion = -1;
    private LibraryActivityCounts? _cachedGlobalCounts;

    private const string ClassifiedSql = """
        WITH inventory AS (
            SELECT m.id,m.library_id,l.name AS library,m.relative_path,m.byte_length,m.modified_at,
                   l.is_active AS library_active,v.definition->>'name' AS flow,a.flow_version,
                   e.id AS execution_id,e.state AS execution_state,e.detail,e.node_id,n.name AS node,
                   e.progress,e.updated_at,o.state AS ownership_state,q.reason AS quarantine_reason,
                   q.winner_id AS conflicting_execution_id
            FROM library_media m
            JOIN libraries l ON l.id=m.library_id AND NOT l.is_deleted
            LEFT JOIN library_flow_assignments a ON a.library_id=l.id
            LEFT JOIN flow_versions v ON v.flow_id=a.flow_id AND v.version=a.flow_version
            LEFT JOIN executions e ON e.id=COALESCE(
                (SELECT e2.id FROM task_ownership o2
                 JOIN executions e2 ON e2.id=o2.execution_id
                 WHERE o2.library_id=m.library_id AND o2.media_key=upper(m.relative_path)
                   AND o2.state!='quarantined'
                   AND e2.state='queued' AND e2.flow_name=v.definition->>'name'
                   AND e2.flow_version=a.flow_version
                 ORDER BY e2.updated_at DESC,e2.id DESC LIMIT 1),
                (SELECT e2.id FROM execution_work w2 JOIN executions e2 ON e2.id=w2.execution_id
                 WHERE w2.library_id=m.library_id AND w2.media_key=upper(m.relative_path)
                   AND NOT EXISTS(SELECT 1 FROM task_ownership conflict WHERE conflict.execution_id=e2.id AND conflict.state='quarantined')
                   AND e2.flow_name=v.definition->>'name' AND e2.flow_version=a.flow_version
                 ORDER BY e2.updated_at DESC,e2.id DESC LIMIT 1),
                (SELECT e2.id FROM task_ownership o2 JOIN executions e2 ON e2.id=o2.execution_id
                 WHERE o2.library_id=m.library_id AND o2.media_key=upper(m.relative_path)
                   AND NOT EXISTS(SELECT 1 FROM task_ownership conflict WHERE conflict.execution_id=e2.id AND conflict.state='quarantined')
                   AND e2.flow_name=v.definition->>'name' AND e2.flow_version=a.flow_version
                 ORDER BY e2.updated_at DESC,e2.id DESC LIMIT 1),
                (SELECT q2.execution_id FROM task_quarantines q2
                 JOIN task_ownership o2 ON o2.execution_id=q2.execution_id
                 JOIN executions e2 ON e2.id=q2.execution_id
                 WHERE o2.library_id=m.library_id AND o2.media_key=upper(m.relative_path)
                   AND o2.state='quarantined' AND e2.flow_name=v.definition->>'name'
                   AND e2.flow_version=a.flow_version
                 ORDER BY q2.created_at DESC,e2.updated_at DESC LIMIT 1),
                (SELECT r.execution_id FROM library_processing_receipts r
                 WHERE r.library_id=m.library_id AND r.file_id=m.id AND r.flow_id=a.flow_id
                   AND r.flow_version=a.flow_version AND r.byte_length=m.byte_length
                   AND r.modified_at=m.modified_at LIMIT 1),
                NULL)
            LEFT JOIN cluster_nodes n ON n.id=e.node_id
            LEFT JOIN task_ownership o ON o.execution_id=e.id
            LEFT JOIN task_quarantines q ON q.execution_id=e.id
        ), classified AS (
            SELECT *,CASE
                WHEN execution_state IN ('succeeded','deferred') THEN 'done'
                WHEN ownership_state='quarantined' OR execution_state = 'failed' THEN 'failed'
                WHEN execution_state = 'cancelled' THEN 'cancelled'
                WHEN execution_state IN ('running','cancelling') THEN 'active'
                WHEN NOT library_active THEN 'disabled'
                ELSE 'untreated'
            END AS status
            FROM inventory
        )
        """;

    public async Task<LibraryActivityResponse> GetAsync(
        string? status,
        Guid? libraryId,
        string? flow,
        Guid? nodeId,
        string? search,
        int? limit,
        int? offset,
        CancellationToken ct)
    {
        var effectiveLimit = Math.Clamp(limit is null or <= 0 ? 50 : limit.Value, 1, 200);
        var effectiveOffset = Math.Max(0, offset ?? 0);
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim().ToLowerInvariant();
        var normalizedStatus = status?.Trim().ToLowerInvariant();
        var normalizedFlow = string.IsNullOrWhiteSpace(flow) ? null : flow.Trim();

        var filters = new List<string>();
        if (libraryId is not null) filters.Add("library_id=@libraryId");
        if (normalizedFlow is not null) filters.Add("flow=@flow");
        if (nodeId is not null) filters.Add("node_id=@nodeId");
        if (normalizedSearch is not null)
            filters.Add("(lower(relative_path) LIKE @search OR lower(library) LIKE @search OR lower(COALESCE(flow,'')) LIKE @search OR lower(COALESCE(node,'')) LIKE @search)");
        var filterSql = filters.Count > 0 ? " WHERE " + string.Join(" AND ", filters) : string.Empty;
        var resultFilters = new List<string>(filters);
        if (!string.IsNullOrEmpty(normalizedStatus)) resultFilters.Add("status=@status");
        var resultFilterSql = resultFilters.Count > 0 ? " WHERE " + string.Join(" AND ", resultFilters) : string.Empty;

        LibraryActivityCounts counts;
        var currentBroadcasterVersion = broadcaster?.CurrentVersion ?? -1;
        var canUseGlobalCache = filters.Count == 0 && broadcaster is not null && _cachedGlobalCounts is not null && _cachedCountsVersion == currentBroadcasterVersion;

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Snapshot, ct);

        if (canUseGlobalCache)
        {
            counts = _cachedGlobalCounts!;
        }
        else
        {
            await using var countsCommand = Sql.Command($"""
                {ClassifiedSql}
                SELECT
                    COALESCE(SUM(CASE WHEN status='untreated' THEN 1 ELSE 0 END),0),
                    COALESCE(SUM(CASE WHEN status='active' THEN 1 ELSE 0 END),0),
                    COALESCE(SUM(CASE WHEN status='done' THEN 1 ELSE 0 END),0),
                    COALESCE(SUM(CASE WHEN status='failed' THEN 1 ELSE 0 END),0),
                    COALESCE(SUM(CASE WHEN status='disabled' THEN 1 ELSE 0 END),0),
                    COALESCE(SUM(CASE WHEN status='cancelled' THEN 1 ELSE 0 END),0)
                FROM classified{filterSql}
                """, connection, transaction);
            BindFilters(countsCommand);
            await using (var reader = await countsCommand.ExecuteReaderAsync(ct))
            {
                await reader.ReadAsync(ct);
                counts = new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5));
            }

            if (filters.Count == 0 && broadcaster is not null)
            {
                _cachedGlobalCounts = counts;
                _cachedCountsVersion = currentBroadcasterVersion;
            }
        }

        var total = normalizedStatus switch
        {
            "untreated" => counts.Untreated,
            "active" => counts.Active,
            "done" => counts.Done,
            "failed" => counts.Failed,
            "cancelled" => counts.Cancelled,
            "disabled" => counts.Disabled,
            _ => counts.Untreated + counts.Active + counts.Done + counts.Failed + counts.Disabled + counts.Cancelled,
        };

        await using var filesCommand = Sql.Command($"""
            {ClassifiedSql}
            SELECT id,library_id,library,relative_path,byte_length,modified_at,library_active,flow,flow_version,
                   status,execution_id,execution_state,detail,node_id,node,progress,updated_at,
                   ownership_state,quarantine_reason,conflicting_execution_id
            FROM classified{resultFilterSql}
            ORDER BY COALESCE(updated_at,modified_at) DESC,relative_path,id
            LIMIT @limit OFFSET @offset
            """, connection, transaction);
        BindFilters(filesCommand);
        if (!string.IsNullOrEmpty(normalizedStatus)) filesCommand.Parameters.AddWithValue("status", normalizedStatus);
        filesCommand.Parameters.AddWithValue("limit", effectiveLimit);
        filesCommand.Parameters.AddWithValue("offset", effectiveOffset);

        var files = new List<LibraryActivityFile>();
        await using (var reader = await filesCommand.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                files.Add(new(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt64(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetBoolean(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetGuid(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetGuid(13), reader.IsDBNull(14) ? null : reader.GetString(14),
                    reader.IsDBNull(15) ? null : reader.GetDecimal(15),
                    reader.IsDBNull(16) ? null : reader.GetFieldValue<DateTimeOffset>(16),
                    reader.IsDBNull(17) ? null : reader.GetString(17),
                    reader.IsDBNull(18) ? null : reader.GetString(18),
                    reader.IsDBNull(19) ? null : reader.GetGuid(19)));
        }

        await transaction.CommitAsync(ct);
        return new(files, counts, total, effectiveLimit, effectiveOffset, DateTimeOffset.UtcNow);

        void BindFilters(DbCommand command)
        {
            if (libraryId is not null) command.Parameters.AddWithValue("libraryId", libraryId.Value);
            if (normalizedFlow is not null) command.Parameters.AddWithValue("flow", normalizedFlow);
            if (nodeId is not null) command.Parameters.AddWithValue("nodeId", nodeId.Value);
            if (normalizedSearch is not null) command.Parameters.AddWithValue("search", $"%{normalizedSearch}%");
        }
    }
}
