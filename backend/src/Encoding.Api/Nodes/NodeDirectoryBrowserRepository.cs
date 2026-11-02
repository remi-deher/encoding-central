using System.Data.Common;
using System.Text.Json;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Nodes;

namespace Encoding.Api.Nodes;

public sealed class NodeDirectoryBrowserRepository(DbDataSource dataSource)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Guid?> CreateAsync(Guid nodeId, string? path, CancellationToken ct)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? string.Empty : path;
        if (normalized.Length > 4096 || normalized.IndexOf('\0') >= 0) return null;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var node = Sql.Command("SELECT EXISTS(SELECT 1 FROM cluster_nodes WHERE id=@id AND deleted_at IS NULL)", connection, transaction))
        {
            node.Parameters.AddWithValue("id", nodeId);
            if (!Convert.ToBoolean(await node.ExecuteScalarAsync(ct))) return null;
        }
        await using (var prune = Sql.Command("DELETE FROM node_directory_browse_requests WHERE expires_at < @now", connection, transaction))
        {
            prune.Parameters.AddWithValue("now", DateTimeOffset.UtcNow.AddMinutes(-5));
            await prune.ExecuteNonQueryAsync(ct);
        }
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var insert = Sql.Command("INSERT INTO node_directory_browse_requests(id,node_id,path,state,entries,error,created_at,updated_at,expires_at,claim_until) VALUES(@id,@node,@path,'queued','[]',NULL,@now,@now,@expires,NULL)",
            connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("node", nodeId);
            insert.Parameters.AddWithValue("path", normalized);
            insert.Parameters.AddWithValue("now", now);
            insert.Parameters.AddWithValue("expires", now.AddMinutes(2));
            await insert.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return id;
    }

    public async Task<NodeDirectoryBrowserResponse?> GetAsync(Guid id, Guid nodeId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT path,state,entries,has_more,error FROM node_directory_browse_requests WHERE id=@id AND node_id=@node AND expires_at>@now");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("node", nodeId);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(id, nodeId, reader.GetString(0), reader.GetString(1),
            JsonSerializer.Deserialize<IReadOnlyList<NodeDirectoryBrowserEntry>>(reader.GetString(2), Json) ?? [],
            reader.GetBoolean(3), reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    public async Task<NodeDirectoryBrowseClaim?> ClaimAsync(Guid nodeId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        Guid? id = null;
        string? path = null;
        await using (var select = dataSource.CreateCommand("SELECT id,path FROM node_directory_browse_requests WHERE node_id=@node AND expires_at>@now AND (state='queued' OR (state='claimed' AND claim_until<@now)) ORDER BY created_at,id LIMIT 1"))
        {
            select.Parameters.AddWithValue("node", nodeId);
            select.Parameters.AddWithValue("now", now);
            await using var reader = await select.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) { id = reader.GetGuid(0); path = reader.GetString(1); }
        }
        if (id is null) return null;

        // La plupart des sondages ne trouvent aucune demande : ne prennent alors aucun verrou d’écriture SQLite.
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var update = Sql.Command("UPDATE node_directory_browse_requests SET state='claimed',claim_until=@until,updated_at=@now WHERE id=@id AND node_id=@node AND expires_at>@now AND (state='queued' OR (state='claimed' AND claim_until<@now))",
            connection, transaction))
        {
            update.Parameters.AddWithValue("until", now.AddSeconds(30));
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("id", id.Value);
            update.Parameters.AddWithValue("node", nodeId);
            if (await update.ExecuteNonQueryAsync(ct) != 1) return null;
        }
        await transaction.CommitAsync(ct);
        return new(id.Value, path!);
    }

    public async Task<bool> CompleteAsync(Guid id, Guid nodeId, NodeDirectoryBrowseCompletion completion, CancellationToken ct)
    {
        var entries = completion.Entries ?? [];
        if (entries.Count > 200 || entries.Any(entry => entry is null || entry.Name.Length is 0 or > 255 || entry.Path.Length is 0 or > 4096)
            || (completion.Error?.Length ?? 0) > 500)
            return false;
        await using var command = dataSource.CreateCommand("UPDATE node_directory_browse_requests SET state=@state,entries=@entries,has_more=@has_more,error=@error,claim_until=NULL,updated_at=@now WHERE id=@id AND node_id=@node AND state='claimed' AND expires_at>@now");
        command.Parameters.AddWithValue("state", completion.Error is null ? "completed" : "failed");
        command.Parameters.AddWithValue("entries", JsonSerializer.Serialize(entries, Json));
        command.Parameters.AddWithValue("has_more", completion.HasMore);
        command.Parameters.AddWithValue("error", (object?)completion.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("node", nodeId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }
}
