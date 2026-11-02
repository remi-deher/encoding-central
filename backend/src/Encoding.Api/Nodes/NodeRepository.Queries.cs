using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure;
using Encoding.Api.Executions;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Contracts.Nodes;
using Encoding.Core.Nodes;

namespace Encoding.Api.Nodes;

public sealed partial class NodeRepository
{
    public async Task<IReadOnlyList<NodeResponse>> ListAsync(TimeSpan offlineAfter, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, name, roles, max_concurrent_tasks, capabilities, last_seen_at,
                   last_seen_at >= clock_timestamp() - @offline_after AS is_online, inventory, display_name, labels, desired_roles
            FROM cluster_nodes WHERE deleted_at IS NULL
            ORDER BY name, id
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(sql, connection);
        command.Parameters.AddWithValue("offline_after", offlineAfter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var nodes = new List<NodeResponse>();
        while (await reader.ReadAsync(cancellationToken)) nodes.Add(ReadResponse(reader, reader.GetBoolean(6)));
        await reader.DisposeAsync();
        return await WithLifecycleAsync(nodes, cancellationToken);
    }

    public async Task<NodeResponse?> GetAsync(Guid nodeId, TimeSpan offlineAfter, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, name, roles, max_concurrent_tasks, capabilities, last_seen_at,
                   last_seen_at >= clock_timestamp() - @offline_after AS is_online, inventory, display_name, labels, desired_roles
            FROM cluster_nodes WHERE id = @id AND deleted_at IS NULL
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(sql, connection);
        command.Parameters.AddWithValue("id", nodeId);
        command.Parameters.AddWithValue("offline_after", offlineAfter);
        NodeResponse? node;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            node = await reader.ReadAsync(cancellationToken) ? ReadResponse(reader, reader.GetBoolean(6)) : null;
        return node is null ? null : (await WithLifecycleAsync([node], cancellationToken))[0];
    }

    public Task<NodeResponse?> FindByIdAsync(Guid nodeId, CancellationToken cancellationToken) =>
        GetAsync(nodeId, TimeSpan.FromDays(365), cancellationToken);



    /// <summary>Donne un nom convivial à un nœud qui n'en a pas encore ; ne remplace jamais un nom choisi par un administrateur.</summary>
    public async Task SetDisplayNameIfUnsetAsync(Guid nodeId, string displayName, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("UPDATE cluster_nodes SET display_name = @name WHERE id = @id AND deleted_at IS NULL AND (display_name IS NULL OR trim(display_name) = '')");
        command.Parameters.AddWithValue("id", nodeId);
        command.Parameters.AddWithValue("name", displayName);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Attribue le nom d'affichage au seul nœud actif sans nom lorsque l'identifiant du nœud local n'est pas encore en mémoire
    /// (par exemple après un redémarrage du serveur alors que l'agent local était déjà enrôlé).
    /// </summary>
    public async Task<bool> SetFallbackLocalDisplayNameAsync(string displayName, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE cluster_nodes SET display_name = @name
            WHERE id = (
                SELECT id FROM cluster_nodes
                WHERE deleted_at IS NULL AND (display_name IS NULL OR trim(display_name) = '')
                ORDER BY last_seen_at DESC
                LIMIT 1
            )
            AND (SELECT count(*) FROM cluster_nodes WHERE deleted_at IS NULL) = 1
            """);
        command.Parameters.AddWithValue("name", displayName);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    private async Task<bool> CanCoordinateAsync(CancellationToken ct)
    {
        if(coordination is null || !await coordination.IsClusteredAsync(ct)) return true;
        if(!await coordination.VerifyAsync(ct)) return false;
        if(ownership is not null) await ownership.PrepareClaimsAsync(ct);
        return await coordination.CheckAsync(ct) is null;
    }
    public enum ModeRequestResult { Accepted, NotFound, LastServer, CoordinationUnavailable }

    /// <summary>
    /// Enregistre le mode voulu d'un nœud ; le nœud l'applique à son prochain signal de présence. Refuse de retirer la fonction
    /// serveur du dernier nœud serveur en ligne (les transactions SQLite sérialisent les changements de mode).
    /// </summary>
    public async Task<ModeRequestResult> RequestModeAsync(Guid nodeId, bool server, bool worker, TimeSpan offlineAfter, CancellationToken ct,bool persist=true)
    {
        await using(var exists=dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM cluster_nodes WHERE id=@id AND deleted_at IS NULL)")) {exists.Parameters.AddWithValue("id",
            nodeId);if(!await exists.ScalarBoolAsync(ct)) return ModeRequestResult.NotFound;}
        if(!await CanCoordinateAsync(ct)) return ModeRequestResult.CoordinationUnavailable;
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        string[] roles;
        await using (var select = Sql.Command("SELECT roles FROM cluster_nodes WHERE id=@id AND deleted_at IS NULL FOR UPDATE", c, tx))
        {
            select.Parameters.AddWithValue("id", nodeId);
            if (Sql.ToStringArray(await select.ExecuteScalarAsync(ct)) is not { } current) return ModeRequestResult.NotFound;
            roles = current;
        }

        if (roles.Contains("server") && !server)
        {
            await using(var queue=Sql.Command("SELECT EXISTS(SELECT 1 FROM task_ownership o JOIN executions e ON e.id=o.execution_id WHERE o.state IN ('active','frozen','received') AND e.state IN ('queued','running','cancelling'))",
                c,tx)) if(await queue.ScalarBoolAsync(ct)) return ModeRequestResult.CoordinationUnavailable;
            await using var others = Sql.Command("""
                SELECT count(*) FROM cluster_nodes
                WHERE id <> @id AND deleted_at IS NULL AND 'server' = ANY(roles) AND 'server' = ANY(COALESCE(desired_roles, roles))
                  AND last_seen_at >= clock_timestamp() - @offline_after
                """, c, tx);
            others.Parameters.AddWithValue("id", nodeId);
            others.Parameters.AddWithValue("offline_after", offlineAfter);
            if ((long)(await others.ExecuteScalarAsync(ct))! == 0) return ModeRequestResult.LastServer;
        }
        if(!persist) return ModeRequestResult.Accepted;
        var desired = new[] { server ? "server" : null, worker ? "worker" : null }.Where(role => role is not null).ToArray();
        await using (var update = Sql.Command("""
            UPDATE cluster_nodes SET desired_roles = CASE WHEN @desired <@ roles AND roles <@ @desired THEN NULL ELSE @desired END
            WHERE id = @id
            """, c, tx))
        {
            update.Parameters.AddWithValue("id", nodeId);
            update.Parameters.AddWithValue("desired", SqlKind.TextArray, desired);
            await update.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return ModeRequestResult.Accepted;
    }

    /// <summary>Départ volontaire d’un worker (0039) : même retrait que la suppression par un administrateur, mais authentifié par le jeton du nœud lui-même.</summary>
    public async Task<string> LeaveAsync(Guid id, string token, CancellationToken ct)
    {
        await using var owner = dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM cluster_nodes WHERE id = @id AND node_token_hash = @hash AND deleted_at IS NULL)");
        owner.Parameters.AddWithValue("id", id);
        owner.Parameters.AddWithValue("hash", SqlKind.Bytes, HashSecret(token));
        return (await owner.ScalarBoolAsync(ct)) ? await DeleteAsync(id, ct) : "unauthorized";
    }

    public async Task<string> DeleteAsync(Guid id,CancellationToken ct)
    {
        bool server;
        await using(var read=dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM cluster_nodes WHERE id=@id AND 'server'=ANY(roles))")) {read.Parameters.AddWithValue("id",
            id);server=await read.ScalarBoolAsync(ct);}
        if(server && !await CanCoordinateAsync(ct)) return "coordination";
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        await using var node=Sql.Command("SELECT id FROM cluster_nodes WHERE id=@id AND deleted_at IS NULL FOR UPDATE",c,tx);
        node.Parameters.AddWithValue("id",id); if(await node.ExecuteScalarAsync(ct) is null) return "missing";
        if(!server) {await using var changed=Sql.Command("SELECT EXISTS(SELECT 1 FROM cluster_nodes WHERE id=@id AND 'server'=ANY(roles))",c,tx);
            changed.Parameters.AddWithValue("id",id);if(await changed.ScalarBoolAsync(ct)) return "coordination";}
        await using var active=Sql.Command("""
          SELECT EXISTS(SELECT 1 FROM execution_work w JOIN executions e ON e.id=w.execution_id
            WHERE (w.worker_node_id=@id OR w.source_node_id=@id) AND (w.lease_until>clock_timestamp() OR w.handoff_pending) AND e.state IN ('running','cancelling'))
          OR EXISTS(SELECT 1 FROM library_scans WHERE node_id=@id AND state='running' AND lease_until>clock_timestamp())
          """,c,tx);
        active.Parameters.AddWithValue("id",id); if((await active.ScalarBoolAsync(ct))) return "busy";
        await using(var queues=Sql.Command("SELECT EXISTS(SELECT 1 FROM task_ownership o JOIN executions e ON e.id=o.execution_id WHERE o.state IN ('active','frozen','received') AND e.state IN ('queued','running','cancelling')) AND EXISTS(SELECT 1 FROM cluster_nodes WHERE id=@id AND 'server'=ANY(roles))",
            c,tx))
        {queues.Parameters.AddWithValue("id",id);if(await queues.ScalarBoolAsync(ct)) return "busy";}
        if (peerAdmissions is not null) await peerAdmissions.RevokeNodeAdmissionAsync(c, tx, id, ct);
        await using var remove=Sql.Command("""
          UPDATE cluster_nodes SET deleted_at=clock_timestamp(),node_token_hash=NULL WHERE id=@id;
          DELETE FROM node_group_members WHERE node_id=@id;
          DELETE FROM library_node_accesses WHERE node_id=@id;
          UPDATE library_node_accesses SET is_enabled=false WHERE transfer_source_node_id=@id;
          DELETE FROM node_enrollment_requests WHERE node_id=@id;
          """,c,tx);
        remove.Parameters.AddWithValue("id",id); await remove.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct); return "deleted";
    }

    /// <summary>Tâches actives (bail valide) par nœud worker, en une requête : alimente la charge des cartes de nœuds.</summary>
    public async Task<IReadOnlyDictionary<Guid,int>> ActiveTaskCountsAsync(CancellationToken ct)
    {
        await using var cmd=dataSource.CreateCommand("""
          SELECT w.worker_node_id, count(*)::integer
          FROM execution_work w JOIN executions e ON e.id=w.execution_id
          WHERE w.worker_node_id IS NOT NULL AND w.lease_until>clock_timestamp() AND e.state IN ('running','cancelling')
          GROUP BY w.worker_node_id
          """);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        var counts=new Dictionary<Guid,int>();
        while(await r.ReadAsync(ct)) counts[r.GetGuid(0)]=r.GetInt32(1);
        return counts;
    }

    public async Task<NodeActivityResponse> ActivityAsync(Guid id,CancellationToken ct)
    {
        await using var cmd=dataSource.CreateCommand("""
          SELECT
           (SELECT count(*)::integer FROM execution_work w JOIN executions e ON e.id=w.execution_id WHERE w.worker_node_id=@id AND w.lease_until>clock_timestamp() AND e.state IN ('running','cancelling')),
           (SELECT count(*)::integer FROM executions WHERE node_id=@id AND state='succeeded'),
           (SELECT count(*)::integer FROM executions WHERE node_id=@id AND state='failed'),
           (SELECT count(*)::integer FROM executions WHERE node_id=@id AND state='cancelled'),
           (SELECT max(updated_at) FROM executions WHERE node_id=@id),
           (SELECT count(*)::integer FROM execution_work w JOIN executions e ON e.id=w.execution_id WHERE w.source_node_id=@id AND w.lease_until>clock_timestamp() AND e.state IN ('running','cancelling'))
          """);
        cmd.Parameters.AddWithValue("id",id); await using var r=await cmd.ExecuteReaderAsync(ct); await r.ReadAsync(ct);
        return new(r.GetInt32(0),r.GetInt32(1),r.GetInt32(2),r.GetInt32(3),r.IsDBNull(4)?(DateTimeOffset?)null:r.GetFieldValue<DateTimeOffset>(4),r.GetInt32(5));
    }

    public async Task<string?> GetActiveDisplayNameAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT display_name
            FROM cluster_nodes
            WHERE deleted_at IS NULL AND display_name IS NOT NULL AND trim(display_name) <> ''
            ORDER BY last_seen_at DESC
            LIMIT 1
            """);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is string { Length: > 0 } s ? s : null;
    }

    private static void AddNodeParameters(DbCommand command, NodeEnrollmentRequest request)
    {
        AddNodeParameters(command, request.Id, new NodeHeartbeatRequest(request.Name, request.Roles, request.MaxConcurrentTasks, request.Capabilities, request.Inventory));
    }

    private static void AddNodeParameters(DbCommand command, Guid nodeId, NodeHeartbeatRequest request)
    {
        command.Parameters.AddWithValue("id", nodeId);
        command.Parameters.AddWithValue("name", request.Name.Trim());
        command.Parameters.AddWithValue("roles", request.Roles.Select(role => role.ToString().ToLowerInvariant()).ToArray());
        command.Parameters.AddWithValue("max_concurrent_tasks", request.MaxConcurrentTasks);
        command.Parameters.AddWithValue("capabilities", SqlKind.Json, JsonSerializer.Serialize(request.Capabilities, JsonOptions));
        command.Parameters.AddWithValue("inventory", SqlKind.Json, JsonSerializer.Serialize(request.Inventory ?? new NodeInventory("unknown", "unknown", 0, 0), JsonOptions));
    }

    internal static NodeResponse ReadResponse(DbDataReader reader, bool isOnline)
    {
        var lastSeenAt = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc));
        var roles = reader.GetStringArray(2).Select(role => Enum.Parse<NodeRole>(role, true)).ToArray();
        var capabilities = JsonSerializer.Deserialize<IReadOnlyList<NodeToolCapability>>(reader.GetString(4), JsonOptions) ?? [];
        var inventoryOrdinal = reader.FieldCount >= 10 ? 7 : 6;
        var inventory = ReadInventory(reader.GetString(inventoryOrdinal));
        var displayName = reader.FieldCount > inventoryOrdinal + 1 && !reader.IsDBNull(inventoryOrdinal + 1) ? reader.GetString(inventoryOrdinal + 1) : null;
        var labels = reader.FieldCount > inventoryOrdinal + 2
            ? JsonSerializer.Deserialize<Dictionary<string, string?>>(reader.GetString(inventoryOrdinal + 2), JsonOptions) ?? []
            : new Dictionary<string, string?>();
        var desiredOrdinal = inventoryOrdinal + 3;
        var desired = reader.FieldCount > desiredOrdinal && !reader.IsDBNull(desiredOrdinal)
            ? reader.GetStringArray(desiredOrdinal).Select(role => Enum.Parse<NodeRole>(role, true)).ToArray()
            : null;
        return new NodeResponse(reader.GetGuid(0), reader.GetString(1), roles, reader.GetInt32(3), capabilities,
            isOnline ? NodePresenceStatus.Online : NodePresenceStatus.Offline, lastSeenAt, inventory, displayName, labels, desired);
    }

    private static byte[] HashSecret(string value) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));

    private static NodeInventory? ReadInventory(string json) => json == "{}" ? null : JsonSerializer.Deserialize<NodeInventory>(json, JsonOptions);
}
