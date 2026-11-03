using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure;
using Encoding.Api.Executions;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Contracts.Workers;
using Encoding.Core.Workers;

namespace Encoding.Api.Workers;

public enum PairingRequestResult { Created, Conflict, TooMany }

public sealed partial class WorkerRepository(DbDataSource dataSource)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Enrôle un worker. Une nouvelle identité n’est acceptée que fenêtre ouverte ; une identité déjà connue
    /// avec le même secret peut répéter l’appel (reprise après interruption).
    /// </summary>
    public async Task<WorkerResponse?> EnrollAsync(WorkerEnrollmentRequest request, bool allowNewNode, CancellationToken cancellationToken)
    {
        var tokenHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.WorkerToken));
        const string sql = """
            INSERT INTO workers (id, name, roles, max_concurrent_tasks, capabilities, inventory, last_seen_at, worker_token_hash, display_name)
            SELECT @id, @name, @roles, @max_concurrent_tasks, @capabilities, @inventory, (strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z'), @worker_token_hash, @display_name
            WHERE @allow_new OR EXISTS (SELECT 1 FROM workers WHERE id = @id AND deleted_at IS NULL AND worker_token_hash = @worker_token_hash)
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                roles = EXCLUDED.roles,
                max_concurrent_tasks = EXCLUDED.max_concurrent_tasks,
                capabilities = EXCLUDED.capabilities,
                inventory = EXCLUDED.inventory,
                last_seen_at = (strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z'),
                display_name = COALESCE(workers.display_name, EXCLUDED.display_name)
            WHERE workers.deleted_at IS NULL AND (workers.worker_token_hash IS NULL OR workers.worker_token_hash = EXCLUDED.worker_token_hash)
            RETURNING id, name, roles, max_concurrent_tasks, capabilities, last_seen_at, inventory, display_name, labels
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(sql, connection);
        AddWorkerParameters(command, request);
        command.Parameters.AddWithValue("display_name", (object?)request.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("worker_token_hash", SqlKind.Bytes, tokenHash);
        command.Parameters.AddWithValue("allow_new", allowNewNode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadResponse(reader, true) : null;
    }

    public async Task DeletePairingRequestAsync(Guid workerId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM worker_enrollment_requests WHERE worker_id = @id");
        command.Parameters.AddWithValue("id", workerId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Authentifie un worker par son seul secret ; l’empreinte SHA-256 de ce secret aléatoire à 256 bits identifie le worker.</summary>
    public async Task<bool> AuthenticateTokenAsync(string token, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM workers WHERE worker_token_hash = @hash AND deleted_at IS NULL)");
        command.Parameters.AddWithValue("hash", SqlKind.Bytes, HashSecret(token));
        return (await command.ScalarBoolAsync(cancellationToken));
    }

    public async Task<bool> AuthenticateTokenAsync(Guid workerId, string token, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM workers WHERE id = @id AND worker_token_hash = @hash AND deleted_at IS NULL)");
        command.Parameters.AddWithValue("id", workerId);
        command.Parameters.AddWithValue("hash", SqlKind.Bytes, HashSecret(token));
        return await command.ScalarBoolAsync(cancellationToken);
    }

    /// <summary>Vrai si cet identifiant a été retiré du central (suppression ou départ) : le worker doit repartir d'une identité neuve (0017).</summary>
    public async Task<bool> IsRemovedAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM workers WHERE id = @id AND deleted_at IS NOT NULL)");
        command.Parameters.AddWithValue("id", id);
        return await command.ScalarBoolAsync(cancellationToken);
    }

    public async Task<PairingRequestResult> CreatePairingRequestAsync(CreateWorkerEnrollmentRequest request, int maxPending, CancellationToken cancellationToken)
    {
        await using var deleted=dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM workers WHERE id=@id AND deleted_at IS NOT NULL)");
        deleted.Parameters.AddWithValue("id",request.Worker.Id); if((await deleted.ScalarBoolAsync(cancellationToken))) return PairingRequestResult.Conflict;
        // Borne les demandes en attente pour qu’un appelant anonyme ne puisse pas saturer la liste d’approbation.
        await using var pending=dataSource.CreateCommand("SELECT count(*) FROM worker_enrollment_requests WHERE expires_at > (strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z') AND worker_id <> @id");
        pending.Parameters.AddWithValue("id",request.Worker.Id); if((long)(await pending.ExecuteScalarAsync(cancellationToken))! >= maxPending) return PairingRequestResult.TooMany;
        var pairingKeyHash = HashSecret(request.PairingKey);
        var nodeTokenHash = HashSecret(request.Worker.WorkerToken);
        await using (var existingNode = dataSource.CreateCommand("SELECT worker_token_hash FROM workers WHERE id=@id AND deleted_at IS NULL"))
        {
            existingNode.Parameters.AddWithValue("id",request.Worker.Id);
            var existing = await existingNode.ExecuteScalarAsync(cancellationToken);
            if (existing is DBNull || existing is byte[] known && !CryptographicOperations.FixedTimeEquals(known,nodeTokenHash))
                return PairingRequestResult.Conflict;
        }

        const string sql = """
            INSERT INTO worker_enrollment_requests
                (worker_id, pairing_key_hash, worker_token_hash, name, roles, max_concurrent_tasks, capabilities, inventory, display_name, expires_at)
            VALUES
                (@id, @pairing_key_hash, @worker_token_hash, @name, @roles, @max_concurrent_tasks, @capabilities, @inventory, @display_name, (strftime('%Y-%m-%d %H:%M:%f', 'now', '+10 minutes') || '000Z'))
            ON CONFLICT (worker_id) DO UPDATE SET
                pairing_key_hash = EXCLUDED.pairing_key_hash,
                worker_token_hash = EXCLUDED.worker_token_hash,
                name = EXCLUDED.name,
                roles = EXCLUDED.roles,
                max_concurrent_tasks = EXCLUDED.max_concurrent_tasks,
                capabilities = EXCLUDED.capabilities,
                inventory = EXCLUDED.inventory,
                display_name = COALESCE(EXCLUDED.display_name, worker_enrollment_requests.display_name),
                created_at = (strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z'),
                expires_at = EXCLUDED.expires_at
            WHERE worker_enrollment_requests.worker_token_hash = EXCLUDED.worker_token_hash
               OR worker_enrollment_requests.expires_at <= (strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z')
            RETURNING worker_id
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = Sql.Command(sql, connection, transaction);
        AddWorkerParameters(command, request.Worker.Id, new WorkerHeartbeatRequest(
            request.Worker.Name,
            request.Worker.Roles,
            request.Worker.MaxConcurrentTasks,
            request.Worker.Capabilities,
            request.Worker.Inventory));
        command.Parameters.AddWithValue("display_name", (object?)request.Worker.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("pairing_key_hash", SqlKind.Bytes, pairingKeyHash);
        command.Parameters.AddWithValue("worker_token_hash", SqlKind.Bytes, nodeTokenHash);
        if (await command.ExecuteScalarAsync(cancellationToken) is null) return PairingRequestResult.Conflict;
        await transaction.CommitAsync(cancellationToken);
        return PairingRequestResult.Created;
    }

    public async Task<IReadOnlyList<PendingWorkerEnrollmentResponse>> ListPendingPairingRequestsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT worker_id, name, roles, max_concurrent_tasks, capabilities, created_at, expires_at, inventory, worker_token_hash, display_name
            FROM worker_enrollment_requests
            WHERE expires_at > (strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z') AND NOT EXISTS(SELECT 1 FROM workers n WHERE n.id=worker_enrollment_requests.worker_id AND n.deleted_at IS NOT NULL)
            ORDER BY created_at, worker_id
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var requests = new List<PendingWorkerEnrollmentResponse>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var roles = reader.GetStringArray(2).Select(role => Enum.Parse<WorkerRole>(role, true)).ToArray();
            var capabilities = JsonSerializer.Deserialize<IReadOnlyList<WorkerToolCapability>>(reader.GetString(4), JsonOptions) ?? [];
            var displayName = reader.IsDBNull(9) ? null : reader.GetString(9);
            requests.Add(new PendingWorkerEnrollmentResponse(
                reader.GetGuid(0), reader.GetString(1), roles, reader.GetInt32(3), capabilities,
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc)),
                ReadInventory(reader.GetString(7)),
                WorkerVerificationCode.FromTokenHash(reader.GetGuid(0), reader.GetFieldValue<byte[]>(8)),
                displayName));
        }
        return requests;
    }

    public async Task<WorkerResponse?> ApprovePairingRequestAsync(string pairingKey, CancellationToken cancellationToken)
    {
        return await ApprovePairingRequestAsync(HashSecret(pairingKey), null, cancellationToken);
    }

    public async Task<WorkerResponse?> ApprovePairingRequestAsync(Guid workerId, CancellationToken cancellationToken)
    {
        return await ApprovePairingRequestAsync(null, workerId, cancellationToken);
    }

    private async Task<WorkerResponse?> ApprovePairingRequestAsync(byte[]? pairingKeyHash, Guid? requestedWorkerId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var requestPredicate = requestedWorkerId.HasValue ? "worker_id = @worker_id" : "pairing_key_hash = @pairing_key_hash";
        var selectSql = $"""
            SELECT worker_id, worker_token_hash, name, roles, max_concurrent_tasks, capabilities, inventory, display_name
            FROM worker_enrollment_requests
            WHERE {requestPredicate} AND expires_at > (strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z')
            """;
        Guid workerId;
        byte[] nodeTokenHash;
        string? displayName;
        WorkerHeartbeatRequest heartbeat;
        await using (var selectCommand = Sql.Command(selectSql, connection, transaction))
        {
            if (requestedWorkerId.HasValue) selectCommand.Parameters.AddWithValue("worker_id", requestedWorkerId.Value);
            else selectCommand.Parameters.AddWithValue("pairing_key_hash", SqlKind.Bytes, pairingKeyHash!);
            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
            workerId = reader.GetGuid(0);
            nodeTokenHash = reader.GetFieldValue<byte[]>(1);
            heartbeat = new WorkerHeartbeatRequest(
                reader.GetString(2),
                reader.GetStringArray(3).Select(role => Enum.Parse<WorkerRole>(role, true)).ToArray(),
                reader.GetInt32(4),
                JsonSerializer.Deserialize<IReadOnlyList<WorkerToolCapability>>(reader.GetString(5), JsonOptions) ?? [],
                ReadInventory(reader.GetString(6)));
            displayName = reader.IsDBNull(7) ? null : reader.GetString(7);
        }

        const string enrollSql = """
            INSERT INTO workers (id, name, roles, max_concurrent_tasks, capabilities, inventory, last_seen_at, worker_token_hash, display_name)
            VALUES (@id, @name, @roles, @max_concurrent_tasks, @capabilities, @inventory, (strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z'), @worker_token_hash, @display_name)
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                roles = EXCLUDED.roles,
                max_concurrent_tasks = EXCLUDED.max_concurrent_tasks,
                capabilities = EXCLUDED.capabilities,
                inventory = EXCLUDED.inventory,
                last_seen_at = (strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z'),
                display_name = COALESCE(workers.display_name, EXCLUDED.display_name)
            WHERE workers.deleted_at IS NULL AND (workers.worker_token_hash IS NULL OR workers.worker_token_hash = EXCLUDED.worker_token_hash)
            RETURNING id, name, roles, max_concurrent_tasks, capabilities, last_seen_at, inventory, display_name, labels
            """;
        WorkerResponse? worker;
        await using (var enrollCommand = Sql.Command(enrollSql, connection, transaction))
        {
            AddWorkerParameters(enrollCommand, workerId, heartbeat);
            enrollCommand.Parameters.AddWithValue("display_name", (object?)displayName ?? DBNull.Value);
            enrollCommand.Parameters.AddWithValue("worker_token_hash", SqlKind.Bytes, nodeTokenHash);
            await using var reader = await enrollCommand.ExecuteReaderAsync(cancellationToken);
            worker = await reader.ReadAsync(cancellationToken) ? ReadResponse(reader, true) : null;
        }
        if (worker is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await using (var deleteCommand = Sql.Command(
            "DELETE FROM worker_enrollment_requests WHERE worker_id = @id", connection, transaction))
        {
            deleteCommand.Parameters.AddWithValue("id", workerId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return worker;
    }

    public async Task<WorkerResponse?> RecordHeartbeatAsync(Guid workerId, string token, WorkerHeartbeatRequest request, CancellationToken cancellationToken)
    {
        using var priority = Encoding.Api.Infrastructure.Data.Sqlite.SqliteWriter.Prioritize();
        var tokenHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        const string unchanged = """
            UPDATE workers SET last_seen_at = (strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z')
            WHERE id=@id AND deleted_at IS NULL AND worker_token_hash=@worker_token_hash
              AND name=@name AND roles=@roles AND max_concurrent_tasks=@max_concurrent_tasks
              AND capabilities=@capabilities AND inventory IS NOT DISTINCT FROM @inventory
            RETURNING id, name, roles, max_concurrent_tasks, capabilities, last_seen_at, true AS is_online,
                inventory, display_name, labels, app_version, session_nonce, incompatible_reason
            """;
        const string changed = """
            UPDATE workers SET
                name = @name,
                roles = @roles,
                max_concurrent_tasks = @max_concurrent_tasks,
                capabilities = @capabilities,
                inventory = @inventory,
                last_seen_at = (strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z')
            WHERE id = @id AND deleted_at IS NULL AND worker_token_hash = @worker_token_hash
            RETURNING id, name, roles, max_concurrent_tasks, capabilities, last_seen_at, true AS is_online, inventory, display_name, labels,
                app_version, session_nonce, incompatible_reason
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        async Task<(WorkerResponse? Node, bool TrackSession)> UpdateAsync(string sql)
        {
            await using var command = Sql.Command(sql, connection);
            AddWorkerParameters(command, workerId, request);
            command.Parameters.AddWithValue("worker_token_hash", SqlKind.Bytes, tokenHash);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return (null, false);
            var version = reader.IsDBNull(10) ? null : reader.GetString(10);
            var nonce = reader.IsDBNull(11) ? null : reader.GetString(11);
            var reason = reader.IsDBNull(12) ? null : reader.GetString(12);
            var track = version != request.Version || reason != AppVersion.MismatchReason(request.Version)
                || request.SessionNonce is not null && nonce != request.SessionNonce;
            return (ReadResponse(reader, true), track);
        }
        var result = await UpdateAsync(unchanged);
        if (result.Node is null) result = await UpdateAsync(changed);
        var worker = result.Node;
        if (worker is null) return null;
        if (result.TrackSession) await TrackSessionAsync(connection, workerId, request, cancellationToken);
        return (await WithLifecycleAsync([worker], cancellationToken))[0];
    }

    /// <summary>
    /// Version de l'application et détection d'une identité utilisée par deux processus (0041). Un redémarrage change le nonce une fois ;
    /// deux processus qui partagent l'identité se relaient (a, b, a…) : le retour d'un nonce précédent récent met le worker en quarantaine.
    /// </summary>
    private static async Task TrackSessionAsync(DbConnection connection, Guid workerId, WorkerHeartbeatRequest request, CancellationToken ct)
    {
        const string sql = """
            UPDATE workers SET
                app_version = @version,
                incompatible_reason = @reason,
                quarantined = quarantined OR COALESCE(@nonce IS NOT NULL AND session_nonce IS NOT NULL AND session_nonce <> @nonce
                    AND previous_nonce = @nonce AND nonce_changed_at > (strftime('%Y-%m-%d %H:%M:%f', 'now', '-10 minutes') || '000Z'), false),
                previous_nonce = CASE WHEN @nonce IS NULL OR session_nonce IS NOT DISTINCT FROM @nonce THEN previous_nonce ELSE session_nonce END,
                nonce_changed_at = CASE WHEN @nonce IS NULL OR session_nonce IS NOT DISTINCT FROM @nonce THEN nonce_changed_at ELSE (strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z') END,
                session_nonce = COALESCE(@nonce, session_nonce)
            WHERE id = @id
            """;
        await using var command = Sql.Command(sql, connection);
        command.Parameters.AddWithValue("id", workerId);
        command.Parameters.AddWithValue("version", SqlKind.Text, (object?)request.Version ?? DBNull.Value);
        command.Parameters.AddWithValue("reason", SqlKind.Text, (object?)AppVersion.MismatchReason(request.Version) ?? DBNull.Value);
        command.Parameters.AddWithValue("nonce", SqlKind.Text, (object?)request.SessionNonce ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Ajoute l'état de cycle de vie (0041) aux workers lus : le plus restrictif l'emporte sur la présence.</summary>
    private async Task<IReadOnlyList<WorkerResponse>> WithLifecycleAsync(IReadOnlyList<WorkerResponse> workers, CancellationToken ct)
    {
        if (workers.Count == 0) return workers;
        await using var command = dataSource.CreateCommand("SELECT id, draining, quarantined, incompatible_reason FROM workers WHERE id IN (SELECT value FROM json_each(@ids))");
        command.Parameters.AddWithValue("ids", workers.Select(worker => worker.Id).ToArray());
        var states = new Dictionary<Guid, (bool Draining, bool Quarantined, string? Incompatible)>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                states[reader.GetGuid(0)] = (reader.GetBoolean(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3));
        return workers.Select(worker =>
        {
            if (!states.TryGetValue(worker.Id, out var state)) return worker;
            var (lifecycle, reason) =
                state.Quarantined ? (WorkerLifecycle.Quarantined, "Identité utilisée par deux processus à la fois : réinitialisez l'un des deux workers puis levez la quarantaine.")
                : state.Incompatible is not null ? (WorkerLifecycle.Incompatible, state.Incompatible)
                : state.Draining ? (WorkerLifecycle.Draining, "Vidange demandée : aucune nouvelle tâche, les tâches en cours se terminent.")
                : worker.Status == WorkerPresenceStatus.Online ? (WorkerLifecycle.Online, (string?)null)
                : (WorkerLifecycle.Offline, "Aucun signal de présence récent.");
            return worker with { Lifecycle = lifecycle, LifecycleReason = reason };
        }).ToArray();
    }

    /// <summary>Vidange : aucune nouvelle tâche pour ce worker, les baux en cours se terminent normalement.</summary>
    public async Task<bool> SetDrainingAsync(Guid workerId, bool draining, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("UPDATE workers SET draining = @draining WHERE id = @id AND deleted_at IS NULL");
        command.Parameters.AddWithValue("id", workerId);
        command.Parameters.AddWithValue("draining", draining);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Lève la quarantaine après que l'administrateur a tranché ; la détection repart de zéro.</summary>
    public async Task<bool> ClearQuarantineAsync(Guid workerId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE workers SET quarantined = false, session_nonce = NULL, previous_nonce = NULL, nonce_changed_at = NULL WHERE id = @id AND deleted_at IS NULL AND quarantined");
        command.Parameters.AddWithValue("id", workerId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }
}
