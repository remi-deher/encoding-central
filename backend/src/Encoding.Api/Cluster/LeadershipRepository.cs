using System.Data.Common;
using System.Security.Cryptography;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Cluster;
using Encoding.Contracts.Nodes;

namespace Encoding.Api.Cluster;

/// <summary>
/// Accès aux données de gouvernance du cluster : état du maître, transferts de rôle et sauvegardes miroir.
/// </summary>
public sealed class LeadershipRepository(DbDataSource dataSource)
{
    public async Task<LeadershipStatusResponse> GetStatusAsync(TimeSpan offlineAfter, CancellationToken ct)
    {
        Guid clusterId = Guid.Empty;
        string? clusterName = null;
        string? masterUrl = null;
        int? backupIntervalMinutes = 60;

        await using var connection = await dataSource.OpenConnectionAsync(ct);

        await using (var cmd = Sql.Command("SELECT id, name, master_url, database_backup_interval_minutes FROM cluster_identity LIMIT 1", connection))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                clusterId = reader.GetGuid(0);
                clusterName = reader.IsDBNull(1) ? null : reader.GetString(1);
                masterUrl = reader.IsDBNull(2) ? null : reader.GetString(2);
                backupIntervalMinutes = reader.IsDBNull(3) ? null : reader.GetInt32(3);
            }
        }

        Guid? masterNodeId = null;
        string? masterNodeName = null;
        bool isDraining = false;

        await using (var cmd = Sql.Command("""
            SELECT id, display_name, name, draining
            FROM cluster_nodes
            WHERE deleted_at IS NULL AND 'server' = ANY(roles)
            ORDER BY last_seen_at DESC
            LIMIT 1
            """, connection))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                masterNodeId = reader.GetGuid(0);
                masterNodeName = reader.IsDBNull(1) ? reader.GetString(2) : reader.GetString(1);
                isDraining = reader.GetBoolean(3);
            }
        }

        DateTimeOffset? lastBackupAt = null;
        await using (var cmd = Sql.Command("SELECT created_at FROM cluster_database_backups ORDER BY created_at DESC LIMIT 1", connection))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                lastBackupAt = DateTimeOffset.Parse(reader.GetString(0));
            }
        }

        return new LeadershipStatusResponse(
            IsMaster: true,
            MasterNodeId: masterNodeId,
            MasterNodeName: masterNodeName,
            MasterUrl: masterUrl,
            IsDraining: isDraining,
            LastBackupAt: lastBackupAt,
            BackupIntervalMinutes: backupIntervalMinutes);
    }

    public async Task<LeadershipTransferRecord?> GetActiveTransferForNodeAsync(Guid targetNodeId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = Sql.Command("""
            SELECT id, target_node_id, state, snapshot_path, snapshot_sha256, snapshot_size_bytes, created_at, expires_at
            FROM cluster_leadership_transfers
            WHERE target_node_id = @target_node_id
              AND state IN ('pending', 'snapshot_ready')
              AND expires_at > clock_timestamp()
            ORDER BY created_at DESC
            LIMIT 1
            """, connection);
        cmd.Parameters.AddWithValue("target_node_id", targetNodeId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTransfer(reader) : null;
    }

    public async Task<LeadershipTransferRecord?> GetTransferByIdAsync(Guid transferId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = Sql.Command("""
            SELECT id, target_node_id, state, snapshot_path, snapshot_sha256, snapshot_size_bytes, created_at, expires_at
            FROM cluster_leadership_transfers
            WHERE id = @id
            """, connection);
        cmd.Parameters.AddWithValue("id", transferId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTransfer(reader) : null;
    }

    public async Task<Guid> CreateTransferAsync(Guid targetNodeId, TimeSpan timeout, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = Sql.Command("""
            INSERT INTO cluster_leadership_transfers (id, target_node_id, state, expires_at)
            VALUES (@id, @target_node_id, 'pending', clock_timestamp() + @timeout)
            """, connection);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("target_node_id", targetNodeId);
        cmd.Parameters.AddWithValue("timeout", timeout);
        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task UpdateTransferSnapshotAsync(Guid transferId, string snapshotPath, string sha256, long sizeBytes, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = Sql.Command("""
            UPDATE cluster_leadership_transfers
            SET state = 'snapshot_ready',
                snapshot_path = @snapshot_path,
                snapshot_sha256 = @snapshot_sha256,
                snapshot_size_bytes = @snapshot_size_bytes
            WHERE id = @id
            """, connection);
        cmd.Parameters.AddWithValue("id", transferId);
        cmd.Parameters.AddWithValue("snapshot_path", snapshotPath);
        cmd.Parameters.AddWithValue("snapshot_sha256", sha256);
        cmd.Parameters.AddWithValue("snapshot_size_bytes", sizeBytes);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CompleteTransferAsync(Guid transferId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = Sql.Command("""
            UPDATE cluster_leadership_transfers
            SET state = 'completed', completed_at = clock_timestamp()
            WHERE id = @id
            """, connection);
        cmd.Parameters.AddWithValue("id", transferId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CancelTransferAsync(Guid transferId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = Sql.Command("UPDATE cluster_leadership_transfers SET state = 'cancelled' WHERE id = @id", connection);
        cmd.Parameters.AddWithValue("id", transferId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveBackupAsync(string digest, long sizeBytes, string filePath, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = Sql.Command("""
            INSERT INTO cluster_database_backups (id, digest, size_bytes, file_path)
            VALUES (@id, @digest, @size_bytes, @file_path)
            """, connection);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("digest", digest);
        cmd.Parameters.AddWithValue("size_bytes", sizeBytes);
        cmd.Parameters.AddWithValue("file_path", filePath);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<DatabaseBackupRecord?> GetLatestBackupAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = Sql.Command("""
            SELECT id, digest, size_bytes, file_path, created_at
            FROM cluster_database_backups
            ORDER BY created_at DESC
            LIMIT 1
            """, connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new DatabaseBackupRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)));
        }
        return null;
    }

    public async Task<IReadOnlyList<string>> PruneOldBackupsAsync(int keepCount, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var oldPaths = new List<string>();
        await using (var cmd = Sql.Command("""
            SELECT file_path FROM cluster_database_backups
            ORDER BY created_at DESC
            LIMIT -1 OFFSET @keep
            """, connection))
        {
            cmd.Parameters.AddWithValue("keep", keepCount);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) oldPaths.Add(reader.GetString(0));
        }

        if (oldPaths.Count > 0)
        {
            await using var del = Sql.Command("""
                DELETE FROM cluster_database_backups
                WHERE id NOT IN (
                    SELECT id FROM cluster_database_backups ORDER BY created_at DESC LIMIT @keep
                )
                """, connection);
            del.Parameters.AddWithValue("keep", keepCount);
            await del.ExecuteNonQueryAsync(ct);
        }

        return oldPaths;
    }

    public async Task SetBackupIntervalMinutesAsync(int minutes, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = Sql.Command("UPDATE cluster_identity SET database_backup_interval_minutes = @minutes", connection);
        cmd.Parameters.AddWithValue("minutes", minutes);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int?> GetBackupIntervalMinutesAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = Sql.Command("SELECT database_backup_interval_minutes FROM cluster_identity LIMIT 1", connection);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    public async Task SetMasterUrlAsync(string masterUrl, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = Sql.Command("UPDATE cluster_identity SET master_url = @url", connection);
        cmd.Parameters.AddWithValue("url", masterUrl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetMasterUrlAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = Sql.Command("SELECT master_url FROM cluster_identity LIMIT 1", connection);
        var val = await cmd.ExecuteScalarAsync(ct);
        return val is null or DBNull ? null : Convert.ToString(val);
    }

    private static LeadershipTransferRecord ReadTransfer(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5),
            DateTimeOffset.Parse(reader.GetString(6)),
            DateTimeOffset.Parse(reader.GetString(7)));
}

public sealed record LeadershipTransferRecord(
    Guid Id,
    Guid TargetNodeId,
    string State,
    string? SnapshotPath,
    string? SnapshotSha256,
    long? SnapshotSizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed record DatabaseBackupRecord(
    Guid Id,
    string Digest,
    long SizeBytes,
    string FilePath,
    DateTimeOffset CreatedAt);
