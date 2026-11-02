using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure;
using Encoding.Api.Executions;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Contracts.Nodes;
using Encoding.Core.Nodes;

namespace Encoding.Api.Nodes;

public enum PairingRequestResult { Created, Conflict, TooMany }

public sealed partial class NodeRepository(DbDataSource dataSource,
    ClusterPeerAdmissionRepository? peerAdmissions = null,ClusterSourceWriteBarrier? coordination=null,TaskOwnershipRepository? ownership=null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Enrôle un nœud. Une nouvelle identité n’est acceptée que fenêtre ouverte ; une identité déjà connue
    /// avec le même secret peut répéter l’appel (reprise après interruption).
    /// </summary>
    public async Task<NodeResponse?> EnrollAsync(NodeEnrollmentRequest request, bool allowNewNode, CancellationToken cancellationToken)
    {
        var tokenHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.NodeToken));
        const string sql = """
            INSERT INTO cluster_nodes (id, name, roles, max_concurrent_tasks, capabilities, inventory, last_seen_at, node_token_hash, display_name)
            SELECT @id, @name, @roles, @max_concurrent_tasks, @capabilities, @inventory, clock_timestamp(), @node_token_hash, @display_name
            WHERE @allow_new OR EXISTS (SELECT 1 FROM cluster_nodes WHERE id = @id AND deleted_at IS NULL AND node_token_hash = @node_token_hash)
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                roles = EXCLUDED.roles,
                max_concurrent_tasks = EXCLUDED.max_concurrent_tasks,
                capabilities = EXCLUDED.capabilities,
                inventory = EXCLUDED.inventory,
                last_seen_at = clock_timestamp(),
                display_name = COALESCE(cluster_nodes.display_name, EXCLUDED.display_name)
            WHERE cluster_nodes.deleted_at IS NULL AND (cluster_nodes.node_token_hash IS NULL OR cluster_nodes.node_token_hash = EXCLUDED.node_token_hash)
            RETURNING id, name, roles, max_concurrent_tasks, capabilities, last_seen_at, inventory, display_name, labels
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(sql, connection);
        AddNodeParameters(command, request);
        command.Parameters.AddWithValue("display_name", (object?)request.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("node_token_hash", SqlKind.Bytes, tokenHash);
        command.Parameters.AddWithValue("allow_new", allowNewNode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadResponse(reader, true) : null;
    }

    /// <summary>Authentifie un worker par son seul secret ; l’empreinte SHA-256 de ce secret aléatoire à 256 bits identifie le nœud.</summary>
    public async Task<bool> AuthenticateTokenAsync(string token, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM cluster_nodes WHERE node_token_hash = @hash AND deleted_at IS NULL)");
        command.Parameters.AddWithValue("hash", SqlKind.Bytes, HashSecret(token));
        return (await command.ScalarBoolAsync(cancellationToken));
    }

    public async Task<bool> AuthenticateTokenAsync(Guid nodeId, string token, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM cluster_nodes WHERE id = @id AND node_token_hash = @hash AND deleted_at IS NULL)");
        command.Parameters.AddWithValue("id", nodeId);
        command.Parameters.AddWithValue("hash", SqlKind.Bytes, HashSecret(token));
        return await command.ScalarBoolAsync(cancellationToken);
    }

    /// <summary>Vrai si cet identifiant a été retiré du cluster (suppression ou départ) : le nœud doit repartir d'une identité neuve (0017).</summary>
    public async Task<bool> IsRemovedAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM cluster_nodes WHERE id = @id AND deleted_at IS NOT NULL)");
        command.Parameters.AddWithValue("id", id);
        return await command.ScalarBoolAsync(cancellationToken);
    }

    public async Task<PairingRequestResult> CreatePairingRequestAsync(CreateNodeEnrollmentRequest request, int maxPending, CancellationToken cancellationToken)
    {
        await using var deleted=dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM cluster_nodes WHERE id=@id AND deleted_at IS NOT NULL)");
        deleted.Parameters.AddWithValue("id",request.Node.Id); if((await deleted.ScalarBoolAsync(cancellationToken))) return PairingRequestResult.Conflict;
        // Borne les demandes en attente pour qu’un appelant anonyme ne puisse pas saturer la liste d’approbation.
        await using var pending=dataSource.CreateCommand("SELECT count(*) FROM node_enrollment_requests WHERE expires_at > clock_timestamp() AND node_id <> @id");
        pending.Parameters.AddWithValue("id",request.Node.Id); if((long)(await pending.ExecuteScalarAsync(cancellationToken))! >= maxPending) return PairingRequestResult.TooMany;
        var pairingKeyHash = HashSecret(request.PairingKey);
        var nodeTokenHash = HashSecret(request.Node.NodeToken);
        await using (var existingNode = dataSource.CreateCommand("SELECT node_token_hash FROM cluster_nodes WHERE id=@id AND deleted_at IS NULL"))
        {
            existingNode.Parameters.AddWithValue("id",request.Node.Id);
            var existing = await existingNode.ExecuteScalarAsync(cancellationToken);
            if (existing is DBNull || existing is byte[] known && !CryptographicOperations.FixedTimeEquals(known,nodeTokenHash))
                return PairingRequestResult.Conflict;
        }

        const string sql = """
            INSERT INTO node_enrollment_requests
                (node_id, pairing_key_hash, node_token_hash, peer_id, peer_public_key, name, roles, max_concurrent_tasks, capabilities, inventory, display_name, expires_at)
            VALUES
                (@id, @pairing_key_hash, @node_token_hash, @peer_id, @peer_public_key, @name, @roles, @max_concurrent_tasks, @capabilities, @inventory, @display_name, clock_timestamp() + interval '10 minutes')
            ON CONFLICT (node_id) DO UPDATE SET
                pairing_key_hash = EXCLUDED.pairing_key_hash,
                node_token_hash = EXCLUDED.node_token_hash,
                peer_id = EXCLUDED.peer_id,
                peer_public_key = EXCLUDED.peer_public_key,
                name = EXCLUDED.name,
                roles = EXCLUDED.roles,
                max_concurrent_tasks = EXCLUDED.max_concurrent_tasks,
                capabilities = EXCLUDED.capabilities,
                inventory = EXCLUDED.inventory,
                display_name = COALESCE(EXCLUDED.display_name, node_enrollment_requests.display_name),
                created_at = clock_timestamp(),
                expires_at = EXCLUDED.expires_at
            WHERE node_enrollment_requests.node_token_hash = EXCLUDED.node_token_hash
               OR node_enrollment_requests.expires_at <= clock_timestamp()
            RETURNING node_id
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = Sql.Command(sql, connection, transaction);
        AddNodeParameters(command, request.Node.Id, new NodeHeartbeatRequest(
            request.Node.Name,
            request.Node.Roles,
            request.Node.MaxConcurrentTasks,
            request.Node.Capabilities,
            request.Node.Inventory));
        command.Parameters.AddWithValue("display_name", (object?)request.Node.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("pairing_key_hash", SqlKind.Bytes, pairingKeyHash);
        command.Parameters.AddWithValue("node_token_hash", SqlKind.Bytes, nodeTokenHash);
        command.Parameters.AddWithValue("peer_id", (object?)request.Node.ClusterPeerId ?? DBNull.Value);
        command.Parameters.AddWithValue("peer_public_key", (object?)request.Node.ClusterPeerPublicKey ?? DBNull.Value);
        if (await command.ExecuteScalarAsync(cancellationToken) is null) return PairingRequestResult.Conflict;
        await using (var clearUrl = Sql.Command("DELETE FROM node_peer_reconciliation_urls WHERE node_id=@id", connection, transaction))
        {
            clearUrl.Parameters.AddWithValue("id", request.Node.Id); await clearUrl.ExecuteNonQueryAsync(cancellationToken);
        }
        if (request.Node.ClusterPeerId is { } peer && ClusterReconciliationRepository.TryNormalizePeerUrl(request.Node.ClusterPeerReconciliationUrl) is { } url)
        {
            await using var saveUrl = Sql.Command("INSERT INTO node_peer_reconciliation_urls(node_id,peer_id,url) VALUES(@id,@peer,@url)", connection, transaction);
            saveUrl.Parameters.AddWithValue("id", request.Node.Id); saveUrl.Parameters.AddWithValue("peer", peer); saveUrl.Parameters.AddWithValue("url", url);
            await saveUrl.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return PairingRequestResult.Created;
    }

    public async Task<IReadOnlyList<PendingNodeEnrollmentResponse>> ListPendingPairingRequestsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT node_id, name, roles, max_concurrent_tasks, capabilities, created_at, expires_at, inventory, node_token_hash, peer_public_key, display_name
            FROM node_enrollment_requests
            WHERE expires_at > clock_timestamp() AND NOT EXISTS(SELECT 1 FROM cluster_nodes n WHERE n.id=node_enrollment_requests.node_id AND n.deleted_at IS NOT NULL)
            ORDER BY created_at, node_id
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var requests = new List<PendingNodeEnrollmentResponse>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var roles = reader.GetStringArray(2).Select(role => Enum.Parse<NodeRole>(role, true)).ToArray();
            var capabilities = JsonSerializer.Deserialize<IReadOnlyList<NodeToolCapability>>(reader.GetString(4), JsonOptions) ?? [];
            var displayName = reader.IsDBNull(10) ? null : reader.GetString(10);
            requests.Add(new PendingNodeEnrollmentResponse(
                reader.GetGuid(0), reader.GetString(1), roles, reader.GetInt32(3), capabilities,
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc)),
                ReadInventory(reader.GetString(7)),
                NodeVerificationCode.FromTokenHash(reader.GetGuid(0), reader.GetFieldValue<byte[]>(8), reader.IsDBNull(9) ? null : reader.GetString(9)),
                displayName));
        }
        return requests;
    }

    public async Task<NodeResponse?> ApprovePairingRequestAsync(string pairingKey, CancellationToken cancellationToken)
    {
        return await ApprovePairingRequestAsync(HashSecret(pairingKey), null, cancellationToken);
    }

    public async Task<NodeResponse?> ApprovePairingRequestAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        return await ApprovePairingRequestAsync(null, nodeId, cancellationToken);
    }

    private async Task<NodeResponse?> ApprovePairingRequestAsync(byte[]? pairingKeyHash, Guid? requestedNodeId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var requestPredicate = requestedNodeId.HasValue ? "node_id = @node_id" : "pairing_key_hash = @pairing_key_hash";
        var selectSql = $"""
            SELECT node_id, node_token_hash, name, roles, max_concurrent_tasks, capabilities, inventory, peer_id, peer_public_key, display_name
            FROM node_enrollment_requests
            WHERE {requestPredicate} AND expires_at > clock_timestamp()
            FOR UPDATE
            """;
        Guid nodeId;
        byte[] nodeTokenHash;
        Guid? peerId;
        string? peerPublicKey;
        string? displayName;
        NodeHeartbeatRequest heartbeat;
        await using (var selectCommand = Sql.Command(selectSql, connection, transaction))
        {
            if (requestedNodeId.HasValue) selectCommand.Parameters.AddWithValue("node_id", requestedNodeId.Value);
            else selectCommand.Parameters.AddWithValue("pairing_key_hash", SqlKind.Bytes, pairingKeyHash!);
            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
            nodeId = reader.GetGuid(0);
            nodeTokenHash = reader.GetFieldValue<byte[]>(1);
            heartbeat = new NodeHeartbeatRequest(
                reader.GetString(2),
                reader.GetStringArray(3).Select(role => Enum.Parse<NodeRole>(role, true)).ToArray(),
                reader.GetInt32(4),
                JsonSerializer.Deserialize<IReadOnlyList<NodeToolCapability>>(reader.GetString(5), JsonOptions) ?? [],
                ReadInventory(reader.GetString(6)));
            peerId = reader.IsDBNull(7) ? null : reader.GetGuid(7);
            peerPublicKey = reader.IsDBNull(8) ? null : reader.GetString(8);
            displayName = reader.IsDBNull(9) ? null : reader.GetString(9);
        }

        const string enrollSql = """
            INSERT INTO cluster_nodes (id, name, roles, max_concurrent_tasks, capabilities, inventory, last_seen_at, node_token_hash, display_name)
            VALUES (@id, @name, @roles, @max_concurrent_tasks, @capabilities, @inventory, clock_timestamp(), @node_token_hash, @display_name)
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                roles = EXCLUDED.roles,
                max_concurrent_tasks = EXCLUDED.max_concurrent_tasks,
                capabilities = EXCLUDED.capabilities,
                inventory = EXCLUDED.inventory,
                last_seen_at = clock_timestamp(),
                display_name = COALESCE(cluster_nodes.display_name, EXCLUDED.display_name)
            WHERE cluster_nodes.deleted_at IS NULL AND (cluster_nodes.node_token_hash IS NULL OR cluster_nodes.node_token_hash = EXCLUDED.node_token_hash)
            RETURNING id, name, roles, max_concurrent_tasks, capabilities, last_seen_at, inventory, display_name, labels
            """;
        NodeResponse? node;
        await using (var enrollCommand = Sql.Command(enrollSql, connection, transaction))
        {
            AddNodeParameters(enrollCommand, nodeId, heartbeat);
            enrollCommand.Parameters.AddWithValue("display_name", (object?)displayName ?? DBNull.Value);
            enrollCommand.Parameters.AddWithValue("node_token_hash", SqlKind.Bytes, nodeTokenHash);
            await using var reader = await enrollCommand.ExecuteReaderAsync(cancellationToken);
            node = await reader.ReadAsync(cancellationToken) ? ReadResponse(reader, true) : null;
        }
        if (node is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        if (peerId.HasValue && peerPublicKey is not null && peerAdmissions is not null)
            await peerAdmissions.CreateApprovalGrantAsync(connection, transaction, nodeId, peerId.Value, peerPublicKey, cancellationToken);

        if (peerId.HasValue && peerPublicKey is not null && peerAdmissions is not null)
        {
            await using var approveUrl = Sql.Command("UPDATE node_peer_reconciliation_urls SET approved=1 WHERE node_id=@id AND peer_id=@peer", connection, transaction);
            approveUrl.Parameters.AddWithValue("id",nodeId); approveUrl.Parameters.AddWithValue("peer",peerId.Value);
            await approveUrl.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteCommand = Sql.Command(
            "DELETE FROM node_enrollment_requests WHERE node_id = @id", connection, transaction))
        {
            deleteCommand.Parameters.AddWithValue("id", nodeId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return node;
    }

    public async Task<NodeResponse?> RecordHeartbeatAsync(Guid nodeId, string token, NodeHeartbeatRequest request, CancellationToken cancellationToken)
    {
        using var priority = Encoding.Api.Infrastructure.Data.Sqlite.SqliteWriter.Prioritize();
        var tokenHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        const string unchanged = """
            UPDATE cluster_nodes SET last_seen_at = clock_timestamp()
            WHERE id=@id AND deleted_at IS NULL AND node_token_hash=@node_token_hash
              AND name=@name AND roles=@roles AND max_concurrent_tasks=@max_concurrent_tasks
              AND capabilities=@capabilities AND inventory IS NOT DISTINCT FROM @inventory
              AND (desired_roles IS NULL OR NOT (desired_roles <@ @roles AND @roles <@ desired_roles))
            RETURNING id, name, roles, max_concurrent_tasks, capabilities, last_seen_at, true AS is_online,
                inventory, display_name, labels, desired_roles, protocol_version, session_nonce, incompatible_reason
            """;
        const string changed = """
            UPDATE cluster_nodes SET
                name = @name,
                roles = @roles,
                max_concurrent_tasks = @max_concurrent_tasks,
                capabilities = @capabilities,
                inventory = @inventory,
                desired_roles = CASE WHEN desired_roles <@ @roles AND @roles <@ desired_roles THEN NULL ELSE desired_roles END,
                last_seen_at = clock_timestamp()
            WHERE id = @id AND deleted_at IS NULL AND node_token_hash = @node_token_hash
            RETURNING id, name, roles, max_concurrent_tasks, capabilities, last_seen_at, true AS is_online, inventory, display_name, labels, desired_roles,
                protocol_version, session_nonce, incompatible_reason
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        async Task<(NodeResponse? Node, bool TrackSession)> UpdateAsync(string sql)
        {
            await using var command = Sql.Command(sql, connection);
            AddNodeParameters(command, nodeId, request);
            command.Parameters.AddWithValue("node_token_hash", SqlKind.Bytes, tokenHash);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return (null, false);
            int? protocol = reader.IsDBNull(11) ? null : reader.GetInt32(11);
            var nonce = reader.IsDBNull(12) ? null : reader.GetString(12);
            var reason = reader.IsDBNull(13) ? null : reader.GetString(13);
            var track = protocol != request.ProtocolVersion || reason != ClusterProtocol.IncompatibilityReason(request.ProtocolVersion)
                || request.SessionNonce is not null && nonce != request.SessionNonce;
            return (ReadResponse(reader, true), track);
        }
        var result = await UpdateAsync(unchanged);
        if (result.Node is null) result = await UpdateAsync(changed);
        var node = result.Node;
        if (node is null) return null;
        if (result.TrackSession) await TrackSessionAsync(connection, nodeId, request, cancellationToken);
        return (await WithLifecycleAsync([node], cancellationToken))[0];
    }

    /// <summary>
    /// Version de protocole et détection d'une identité utilisée par deux processus (0041). Un redémarrage change le nonce une fois ;
    /// deux processus qui partagent l'identité se relaient (a, b, a…) : le retour d'un nonce précédent récent met le nœud en quarantaine.
    /// </summary>
    private static async Task TrackSessionAsync(DbConnection connection, Guid nodeId, NodeHeartbeatRequest request, CancellationToken ct)
    {
        const string sql = """
            UPDATE cluster_nodes SET
                protocol_version = @protocol,
                incompatible_reason = @reason,
                quarantined = quarantined OR COALESCE(@nonce IS NOT NULL AND session_nonce IS NOT NULL AND session_nonce <> @nonce
                    AND previous_nonce = @nonce AND nonce_changed_at > clock_timestamp() - interval '10 minutes', false),
                previous_nonce = CASE WHEN @nonce IS NULL OR session_nonce IS NOT DISTINCT FROM @nonce THEN previous_nonce ELSE session_nonce END,
                nonce_changed_at = CASE WHEN @nonce IS NULL OR session_nonce IS NOT DISTINCT FROM @nonce THEN nonce_changed_at ELSE clock_timestamp() END,
                session_nonce = COALESCE(@nonce, session_nonce)
            WHERE id = @id
            """;
        await using var command = Sql.Command(sql, connection);
        command.Parameters.AddWithValue("id", nodeId);
        command.Parameters.AddWithValue("protocol", SqlKind.Integer, (object?)request.ProtocolVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("reason", SqlKind.Text, (object?)ClusterProtocol.IncompatibilityReason(request.ProtocolVersion) ?? DBNull.Value);
        command.Parameters.AddWithValue("nonce", SqlKind.Text, (object?)request.SessionNonce ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Ajoute l'état de cycle de vie (0041) aux nœuds lus : le plus restrictif l'emporte sur la présence.</summary>
    private async Task<IReadOnlyList<NodeResponse>> WithLifecycleAsync(IReadOnlyList<NodeResponse> nodes, CancellationToken ct)
    {
        if (nodes.Count == 0) return nodes;
        await using var command = dataSource.CreateCommand("SELECT id, draining, quarantined, incompatible_reason FROM cluster_nodes WHERE id = ANY(@ids)");
        command.Parameters.AddWithValue("ids", nodes.Select(node => node.Id).ToArray());
        var states = new Dictionary<Guid, (bool Draining, bool Quarantined, string? Incompatible)>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                states[reader.GetGuid(0)] = (reader.GetBoolean(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3));
        return nodes.Select(node =>
        {
            if (!states.TryGetValue(node.Id, out var state)) return node;
            var (lifecycle, reason) =
                state.Quarantined ? (NodeLifecycle.Quarantined, "Identité utilisée par deux processus à la fois : réinitialisez l'un des deux nœuds puis levez la quarantaine.")
                : state.Incompatible is not null ? (NodeLifecycle.Incompatible, state.Incompatible)
                : state.Draining ? (NodeLifecycle.Draining, "Vidange demandée : aucune nouvelle tâche, les tâches en cours se terminent.")
                : node.Status == NodePresenceStatus.Online ? (NodeLifecycle.Online, (string?)null)
                : (NodeLifecycle.Offline, "Aucun signal de présence récent.");
            return node with { Lifecycle = lifecycle, LifecycleReason = reason };
        }).ToArray();
    }

    /// <summary>Vidange : aucune nouvelle tâche pour ce nœud, les baux en cours se terminent normalement.</summary>
    public async Task<bool> SetDrainingAsync(Guid nodeId, bool draining, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("UPDATE cluster_nodes SET draining = @draining WHERE id = @id AND deleted_at IS NULL");
        command.Parameters.AddWithValue("id", nodeId);
        command.Parameters.AddWithValue("draining", draining);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Lève la quarantaine après que l'administrateur a tranché ; la détection repart de zéro.</summary>
    public async Task<bool> ClearQuarantineAsync(Guid nodeId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE cluster_nodes SET quarantined = false, session_nonce = NULL, previous_nonce = NULL, nonce_changed_at = NULL WHERE id = @id AND deleted_at IS NULL AND quarantined");
        command.Parameters.AddWithValue("id", nodeId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }
}
