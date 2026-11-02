using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Encoding.Api.Infrastructure.Data;

namespace Encoding.Api.Infrastructure;

/// <summary>Un événement immuable du journal de réconciliation d'un cluster.</summary>
public sealed record ClusterChangeEvent(Guid EventId, Guid ClusterId, Guid OriginId, long Sequence, string EventType, string Payload, DateTimeOffset OccurredAt);

/// <summary>
/// Journal append-only répliqué entre serveurs. Chaque origine séquence ses propres événements ; les doublons sont éliminés
/// par la contrainte (origine, séquence), ce qui permet de relayer un événement sans créer de nouvelle version.
/// </summary>
public sealed class ClusterReconciliationRepository(DbDataSource dataSource)
{
    public const int MaximumBatchSize = 32;
    public const int MaximumPayloadCharacters = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Écrit un registre partagé LWW et son événement dans la même transaction.</summary>
    public async Task SetRegisterAsync(string key, string jsonValue, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256) throw new ArgumentException("Clé de registre invalide.", nameof(key));
        using var valueDocument = JsonDocument.Parse(jsonValue);
        await EnsureIdentityAsync(ct);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await SetRegisterInTransactionAsync(key,jsonValue,connection,transaction,ct);
        await transaction.CommitAsync(ct);
    }

    internal static async Task SetRegisterInTransactionAsync(string key,string jsonValue,DbConnection connection,DbTransaction transaction,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(key) || key.Length>256) throw new ArgumentException("Registre invalide.");
        await using(var identity=Sql.Command("INSERT INTO cluster_sync_identity(singleton,origin_id,next_sequence) VALUES(true,@origin,0) ON CONFLICT(singleton) DO NOTHING",connection,transaction))
        {identity.Parameters.AddWithValue("origin",Guid.NewGuid());await identity.ExecuteNonQueryAsync(ct);}
        Guid clusterId;
        Guid originId;
        long sequence;
        await using (var identity = Sql.Command("SELECT c.id, s.origin_id, s.next_sequence FROM cluster_identity c CROSS JOIN cluster_sync_identity s WHERE c.singleton = true AND s.singleton = true FOR UPDATE",
            connection, transaction))
        {
            await using var reader = await identity.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("L’identité de réconciliation locale est absente.");
            clusterId = ReadGuid(reader, 0);
            originId = ReadGuid(reader, 1);
            sequence = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture) + 1;
        }

        var occurredAt = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new RegisterChange(key, jsonValue), JsonOptions);
        await using (var updateSequence = Sql.Command("UPDATE cluster_sync_identity SET next_sequence = @sequence WHERE singleton = true", connection, transaction))
        {
            updateSequence.Parameters.AddWithValue("sequence", sequence);
            await updateSequence.ExecuteNonQueryAsync(ct);
        }
        await InsertEventAsync(connection, transaction, new(eventId, clusterId, originId, sequence, "register.set", payload, occurredAt), ct);
        await UpsertRegisterAsync(connection, transaction, key, jsonValue, occurredAt, originId, sequence, ct);

        if (key == "cluster.name")
        {
            using var nameDocument = JsonDocument.Parse(jsonValue);
            if (nameDocument.RootElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nameDocument.RootElement.GetString()))
                throw new ArgumentException("Le nom du cluster doit être une chaîne non vide.", nameof(jsonValue));
            await using var updateName = Sql.Command("UPDATE cluster_identity SET name = @name, established = true WHERE singleton = true", connection, transaction);
            updateName.Parameters.AddWithValue("name", nameDocument.RootElement.GetString()!);
            await updateName.ExecuteNonQueryAsync(ct);
        }

    }

    /// <summary>Ajoute les événements distants une seule fois et applique les registres connus selon l'ordre LWW.</summary>
    public async Task ApplyIncomingAsync(IReadOnlyList<ClusterChangeEvent> changes, CancellationToken ct)
    {
        if (changes.Count > MaximumBatchSize) throw new ArgumentException($"Un lot ne peut pas dépasser {MaximumBatchSize} événements.", nameof(changes));
        await EnsureIdentityAsync(ct);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        Guid clusterId;
        await using (var identity = Sql.Command("SELECT id FROM cluster_identity WHERE singleton = true", connection, transaction))
        {
            var value = await identity.ExecuteScalarAsync(ct);
            if (value is null or DBNull) throw new InvalidOperationException("L’identité du cluster est absente.");
            clusterId = value is Guid id ? id : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
        }
        foreach (var change in changes)
        {
            Validate(change);
            if (change.ClusterId != clusterId) throw new InvalidOperationException("L’événement reçu appartient à un autre cluster.");
            if (!await InsertEventAsync(connection, transaction, change, ct)) continue;
            if (change.EventType is "entity.set" or "entity.delete" or "node.presence")
            {
                await using var pending = Sql.Command("INSERT INTO cluster_sync_pending_projections(event_id) VALUES(@event_id) ON CONFLICT DO NOTHING", connection, transaction);
                pending.Parameters.AddWithValue("event_id", change.EventId);
                await pending.ExecuteNonQueryAsync(ct);
                continue;
            }
            if (!string.Equals(change.EventType, "register.set", StringComparison.Ordinal)) continue;

            var parsed = JsonSerializer.Deserialize<RegisterChange>(change.Payload, JsonOptions);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Key) || parsed.Key.Length > 256)
                throw new InvalidOperationException("Événement register.set mal formé.");
            using var value = JsonDocument.Parse(parsed.Value);
            var existing = await ReadRegisterVersionAsync(connection, transaction, parsed.Key, ct);
            if (existing is { } current && CompareVersion(change.OccurredAt, change.OriginId, change.Sequence, current.OccurredAt, current.OriginId, current.Sequence) <= 0) continue;
            await UpsertRegisterAsync(connection, transaction, parsed.Key, parsed.Value, change.OccurredAt, change.OriginId, change.Sequence, ct);
            if (parsed.Key == "cluster.name")
            {
                if (value.RootElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.RootElement.GetString()))
                    throw new InvalidOperationException("Événement de nom de cluster mal formé.");
                await using var update = Sql.Command("UPDATE cluster_identity SET name = @name, established = true WHERE singleton = true", connection, transaction);
                update.Parameters.AddWithValue("name", value.RootElement.GetString()!);
                await update.ExecuteNonQueryAsync(ct);
            }
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, long>> GetVectorAsync(CancellationToken ct)
    {
        await EnsureIdentityAsync(ct);
        var vector = new Dictionary<Guid, long>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using (var identity = Sql.Command("SELECT origin_id, next_sequence FROM cluster_sync_identity WHERE singleton = true", connection))
        await using (var reader = await identity.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct)) vector[ReadGuid(reader, 0)] = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);

        Guid clusterId;
        await using (var identity = Sql.Command("SELECT id FROM cluster_identity WHERE singleton = true", connection))
        {
            var value = await identity.ExecuteScalarAsync(ct);
            if (value is null or DBNull) throw new InvalidOperationException("L’identité du cluster est absente.");
            clusterId = value is Guid id ? id : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
        }
        await using (var command = Sql.Command("SELECT origin_id, MAX(sequence) FROM cluster_change_log WHERE cluster_id = @cluster_id GROUP BY origin_id", connection))
        {
            command.Parameters.AddWithValue("cluster_id", clusterId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var origin = ReadGuid(reader, 0);
                var sequence = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
                if (!vector.TryGetValue(origin, out var known) || sequence > known) vector[origin] = sequence;
            }
        }
        return vector;
    }

    public async Task<IReadOnlyList<ClusterChangeEvent>> GetEventsAfterAsync(Guid clusterId, IReadOnlyDictionary<Guid, long> vector, int limit, CancellationToken ct)
    {
        if (vector.Count > 4096) throw new ArgumentException("Le vecteur contient trop d’origines.", nameof(vector));
        if (limit is < 1 or > MaximumBatchSize) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var clauses = new List<string>();
        var command = Sql.Command("", connection);
        command.Parameters.AddWithValue("cluster_id", clusterId);
        if (vector.Count > 0)
        {
            var originNames = new List<string>();
            var index = 0;
            foreach (var (origin, sequence) in vector.OrderBy(pair => pair.Key))
            {
                var originParameter = "origin_" + index.ToString(CultureInfo.InvariantCulture);
                var sequenceParameter = "sequence_" + index.ToString(CultureInfo.InvariantCulture);
                originNames.Add("@" + originParameter);
                clauses.Add($"(origin_id = @{originParameter} AND sequence > @{sequenceParameter})");
                command.Parameters.AddWithValue(originParameter, origin);
                command.Parameters.AddWithValue(sequenceParameter, sequence);
                index++;
            }
            clauses.Insert(0, $"origin_id NOT IN ({string.Join(',', originNames)})");
        }
        var filter = clauses.Count == 0 ? string.Empty : " AND (" + string.Join(" OR ", clauses) + ")";
        command.CommandText = $"SELECT event_id, cluster_id, origin_id, sequence, event_type, payload, occurred_at FROM cluster_change_log WHERE cluster_id = @cluster_id{filter} ORDER BY origin_id, sequence LIMIT @limit";
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ClusterChangeEvent>();
        while (await reader.ReadAsync(ct))
            result.Add(new(ReadGuid(reader, 0), ReadGuid(reader, 1), ReadGuid(reader, 2), Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                reader.GetString(4), reader.GetString(5), ReadDateTimeOffset(reader.GetValue(6))));
        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, long>> GetPeerVectorAsync(string peerUrl, CancellationToken ct)
    {
        var vector = new Dictionary<Guid, long>();
        await using var command = dataSource.CreateCommand("SELECT origin_id, sequence FROM cluster_sync_peer_vectors WHERE peer_url = @peer_url");
        command.Parameters.AddWithValue("peer_url", NormalizePeerUrl(peerUrl));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) vector[ReadGuid(reader, 0)] = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
        return vector;
    }

    /// <summary>Mémorise le vecteur confirmé par le pair après application du lot envoyé ; les curseurs ne reculent jamais.</summary>
    public async Task SavePeerVectorAsync(string peerUrl, IReadOnlyDictionary<Guid, long> vector, CancellationToken ct)
    {
        var normalizedUrl = NormalizePeerUrl(peerUrl);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var (origin, sequence) in vector)
        {
            if (origin == Guid.Empty || sequence < 0) throw new ArgumentException("Vecteur de pair invalide.", nameof(vector));
            await using var command = Sql.Command("""
                INSERT INTO cluster_sync_peer_vectors(peer_url, origin_id, sequence, updated_at)
                VALUES (@peer_url, @origin_id, @sequence, clock_timestamp())
                ON CONFLICT (peer_url, origin_id) DO UPDATE SET sequence = EXCLUDED.sequence, updated_at = EXCLUDED.updated_at
                WHERE cluster_sync_peer_vectors.sequence < EXCLUDED.sequence
                """, connection, transaction);
            command.Parameters.AddWithValue("peer_url", normalizedUrl);
            command.Parameters.AddWithValue("origin_id", origin);
            command.Parameters.AddWithValue("sequence", sequence);
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Purge les anciennes versions IA seulement quand chaque pair connu a confirmé la séquence du tombstone.
    /// Les tombstones et les versions LWW courantes sont conservés pour empêcher toute résurrection.
    /// </summary>
    public async Task<int> PruneAssistantHistoryAsync(IEnumerable<string> peerUrls, DateTimeOffset tombstonesBefore, CancellationToken ct)
    {
        var vectors = new Dictionary<string, IReadOnlyDictionary<Guid, long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in peerUrls.Select(TryNormalizePeerUrl).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            vectors[url] = await GetPeerVectorAsync(url, ct);

        await EnsureIdentityAsync(ct);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        Guid clusterId;
        await using (var identity = Sql.Command("SELECT id FROM cluster_identity WHERE singleton = true", connection, transaction))
        {
            var value = await identity.ExecuteScalarAsync(ct);
            if (value is null or DBNull) throw new InvalidOperationException("L’identité du cluster est absente.");
            clusterId = value is Guid id ? id : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
        }

        var tombstones = new List<(string Key, DateTimeOffset Time, Guid Origin, long Sequence)>();
        await using (var command = Sql.Command("SELECT register_key,occurred_at,origin_id,sequence FROM cluster_sync_registers WHERE value='null' AND (register_key LIKE 'ai/assistant/%' OR register_key LIKE 'ai/session/%')",
            connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) tombstones.Add((reader.GetString(0), ReadDateTimeOffset(reader.GetValue(1)), ReadGuid(reader, 2),
                Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture)));

        var purged = 0;
        foreach (var tombstone in tombstones)
        {
            if (tombstone.Time >= tombstonesBefore) continue;
            var acknowledgedByAll = vectors.Values.All(vector => vector.TryGetValue(tombstone.Origin, out var sequence) && sequence >= tombstone.Sequence);
            if (!acknowledgedByAll) continue;

            var oldEvents = new List<(Guid Id, DateTimeOffset Time, Guid Origin, long Sequence)>();
            await using (var command = Sql.Command("SELECT event_id,occurred_at,origin_id,sequence,payload FROM cluster_change_log WHERE cluster_id=@cluster AND event_type='register.set' AND json_extract(payload,'$.key')=@key",
                connection, transaction))
            {
                command.Parameters.AddWithValue("cluster", clusterId);
                command.Parameters.AddWithValue("key", tombstone.Key);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var payload = JsonSerializer.Deserialize<RegisterChange>(reader.GetString(4), JsonOptions);
                    if (payload is not null && payload.Value == "null") continue;
                    var time = ReadDateTimeOffset(reader.GetValue(1));
                    var origin = ReadGuid(reader, 2);
                    var sequence = Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture);
                    if (CompareVersion(time, origin, sequence, tombstone.Time, tombstone.Origin, tombstone.Sequence) < 0)
                        oldEvents.Add((ReadGuid(reader, 0), time, origin, sequence));
                }
            }

            foreach (var oldEvent in oldEvents)
            {
                await using var delete = Sql.Command("DELETE FROM cluster_change_log WHERE event_id=@id", connection, transaction);
                delete.Parameters.AddWithValue("id", oldEvent.Id);
                purged += await delete.ExecuteNonQueryAsync(ct);
            }
        }

        await transaction.CommitAsync(ct);
        return purged;
    }

    public static string? TryNormalizePeerUrl(string? peerUrl)
    {
        if (string.IsNullOrWhiteSpace(peerUrl)) return null;
        if (!Uri.TryCreate(peerUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return null;
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/").AbsoluteUri;
    }

    public async Task AddPeerEndpointAsync(string peerUrl, string source, CancellationToken ct)
    {
        var normalized = TryNormalizePeerUrl(peerUrl);
        if (normalized is null) return;
        await using var command = dataSource.CreateCommand("""
            INSERT INTO cluster_sync_peer_endpoints (peer_url, source, added_at, last_seen_at)
            VALUES (@peer_url, @source, (strftime('%Y-%m-%d %H:%M:%f','now') || '000Z'), (strftime('%Y-%m-%d %H:%M:%f','now') || '000Z'))
            ON CONFLICT (peer_url) DO UPDATE SET last_seen_at = (strftime('%Y-%m-%d %H:%M:%f','now') || '000Z')
            """);
        command.Parameters.AddWithValue("peer_url", normalized);
        command.Parameters.AddWithValue("source", source);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RemovePeerEndpointsBySourceAsync(string source, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM cluster_sync_peer_endpoints WHERE source = @source");
        command.Parameters.AddWithValue("source", source);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetPeerEndpointsAsync(CancellationToken ct)
    {
        var result = new List<string>();
        try
        {
            await using var command = dataSource.CreateCommand("SELECT peer_url FROM cluster_sync_peer_endpoints UNION SELECT u.url FROM node_peer_reconciliation_urls u JOIN cluster_peer_admissions a ON a.node_id=u.node_id AND a.peer_id=u.peer_id JOIN cluster_identity i ON i.id=a.cluster_id WHERE a.revoked_at IS NULL AND u.approved=1");
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) result.Add(reader.GetString(0));
        }
        catch (DbException) { }
        return result;
    }

    public async Task EnsureIdentityAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("INSERT INTO cluster_sync_identity(singleton, origin_id, next_sequence) VALUES (true, @origin, 0) ON CONFLICT (singleton) DO NOTHING");
        command.Parameters.AddWithValue("origin", Guid.NewGuid());
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> InsertEventAsync(DbConnection connection, DbTransaction transaction, ClusterChangeEvent change, CancellationToken ct)
    {
        await using var command = Sql.Command("""
            INSERT INTO cluster_change_log(event_id, cluster_id, origin_id, sequence, event_type, payload, occurred_at)
            VALUES (@event_id, @cluster_id, @origin_id, @sequence, @event_type, @payload, @occurred_at)
            ON CONFLICT DO NOTHING
            """, connection, transaction);
        command.Parameters.AddWithValue("event_id", change.EventId);
        command.Parameters.AddWithValue("cluster_id", change.ClusterId);
        command.Parameters.AddWithValue("origin_id", change.OriginId);
        command.Parameters.AddWithValue("sequence", change.Sequence);
        command.Parameters.AddWithValue("event_type", change.EventType);
        command.Parameters.AddWithValue("payload", SqlKind.Json, change.Payload);
        command.Parameters.AddWithValue("occurred_at", change.OccurredAt);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    private async Task<(DateTimeOffset OccurredAt, Guid OriginId, long Sequence)?> ReadRegisterVersionAsync(DbConnection connection, DbTransaction transaction, string key, CancellationToken ct)
    {
        await using var command = Sql.Command("SELECT occurred_at, origin_id, sequence FROM cluster_sync_registers WHERE register_key = @key", connection, transaction);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (ReadDateTimeOffset(reader.GetValue(0)), ReadGuid(reader, 1), Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture)) : null;
    }

    private static async Task UpsertRegisterAsync(DbConnection connection, DbTransaction transaction, string key, string jsonValue, DateTimeOffset occurredAt,
        Guid originId, long sequence, CancellationToken ct)
    {
        await using var command = Sql.Command("""
            INSERT INTO cluster_sync_registers(register_key, value, occurred_at, origin_id, sequence)
            VALUES (@key, @value, @occurred_at, @origin_id, @sequence)
            ON CONFLICT (register_key) DO UPDATE SET value = EXCLUDED.value, occurred_at = EXCLUDED.occurred_at,
                origin_id = EXCLUDED.origin_id, sequence = EXCLUDED.sequence
            """, connection, transaction);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("value", SqlKind.Json, jsonValue);
        command.Parameters.AddWithValue("occurred_at", occurredAt);
        command.Parameters.AddWithValue("origin_id", originId);
        command.Parameters.AddWithValue("sequence", sequence);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void Validate(ClusterChangeEvent change)
    {
        if (change.EventId == Guid.Empty || change.OriginId == Guid.Empty || change.Sequence < 1
            || string.IsNullOrWhiteSpace(change.EventType) || change.EventType.Length > 128
            || string.IsNullOrWhiteSpace(change.Payload) || change.Payload.Length > MaximumPayloadCharacters)
            throw new InvalidOperationException("Événement de réconciliation invalide.");
        using var _ = JsonDocument.Parse(change.Payload);
    }

    private static int CompareVersion(DateTimeOffset leftTime, Guid leftOrigin, long leftSequence, DateTimeOffset rightTime, Guid rightOrigin, long rightSequence)
    {
        var time = leftTime.CompareTo(rightTime);
        if (time != 0) return time;
        if (leftOrigin == rightOrigin) return leftSequence.CompareTo(rightSequence);
        return string.CompareOrdinal(leftOrigin.ToString("N"), rightOrigin.ToString("N"));
    }

    private static Guid ReadGuid(DbDataReader reader, int ordinal) => reader.GetValue(ordinal) is Guid value
        ? value
        : Guid.Parse(Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)!);

    private static DateTimeOffset ReadDateTimeOffset(object value) => value switch
    {
        DateTimeOffset instant => instant.ToUniversalTime(),
        DateTime instant => new DateTimeOffset(DateTime.SpecifyKind(instant, DateTimeKind.Utc)),
        _ => DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
    };

    private static string NormalizePeerUrl(string peerUrl)
    {
        if (!Uri.TryCreate(peerUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("Adresse de pair invalide.", nameof(peerUrl));
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/").AbsoluteUri;
    }

    private sealed record RegisterChange(string Key, string Value);
}
