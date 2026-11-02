using Encoding.Api.Infrastructure.Data;
using System.Data.Common;

namespace Encoding.Api.Plugins;

public enum TriggerRecordResult { Accepted, Duplicate, UnknownLibrary }

public sealed record IntegrationTriggerRequest(Guid Id, string PluginId, Guid LibraryId, string RelativePath, DateTimeOffset RequestedAt);

/// <summary>
/// Demandes de traitement reçues d’un service externe. La file reste locale au serveur qui l’a reçue, avec une réclamation récupérable.
/// </summary>
public sealed class IntegrationTriggerRepository(DbDataSource dataSource)
{
    public static readonly TimeSpan ClaimDuration = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public async Task<TriggerRecordResult> RecordAsync(string pluginId, Guid library, string relativePath, CancellationToken ct)
    {
        await using var exists = dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM libraries WHERE id=@library AND is_active)");
        exists.Parameters.AddWithValue("library", library);
        if (!(await exists.ScalarBoolAsync(ct))) return TriggerRecordResult.UnknownLibrary;
        await using var insert = dataSource.CreateCommand("""
            INSERT INTO integration_trigger_requests(id,plugin_id,library_id,relative_path) VALUES(@id,@plugin,@library,@path)
            ON CONFLICT DO NOTHING
            """);
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("plugin", pluginId);
        insert.Parameters.AddWithValue("library", library);
        insert.Parameters.AddWithValue("path", relativePath);
        return await insert.ExecuteNonQueryAsync(ct) == 1 ? TriggerRecordResult.Accepted : TriggerRecordResult.Duplicate;
    }

    /// <summary>Réclame des demandes ouvertes ; une réclamation expirée (serveur disparu) redevient disponible.</summary>
    public async Task<IReadOnlyList<IntegrationTriggerRequest>> ClaimAsync(int limit, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE integration_trigger_requests SET state='processing',claimed_until=clock_timestamp()+@claim
            WHERE id IN(SELECT id FROM integration_trigger_requests
                        WHERE state IN('pending','processing') AND (claimed_until IS NULL OR claimed_until<=clock_timestamp())
                        ORDER BY requested_at LIMIT @limit FOR UPDATE SKIP LOCKED)
            RETURNING id,plugin_id,library_id,relative_path,requested_at
            """);
        command.Parameters.AddWithValue("claim", ClaimDuration);
        command.Parameters.AddWithValue("limit", limit);
        var result = new List<IntegrationTriggerRequest>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)));
        return result;
    }

    /// <summary>Remet la demande en attente (la découverte n’est pas encore terminée) après un court délai.</summary>
    public async Task ReleaseAsync(Guid id, TimeSpan retryAfter, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE integration_trigger_requests SET state='pending',claimed_until=clock_timestamp()+@retry WHERE id=@id AND state='processing'");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("retry", retryAfter);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task CompleteAsync(Guid id, string state, string detail, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE integration_trigger_requests SET state=@state,detail=@detail,claimed_until=NULL,completed_at=clock_timestamp() WHERE id=@id AND state='processing'");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("detail", detail);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Vrai si une découverte terminée a démarré après la demande : le fichier importé y est alors visible ou absent.</summary>
    public async Task<bool> HasScanSinceAsync(Guid library, DateTimeOffset since, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT EXISTS(SELECT 1 FROM library_scans WHERE library_id=@library AND state='completed' AND created_at>=@since)");
        command.Parameters.AddWithValue("library", library);
        command.Parameters.AddWithValue("since", since);
        return (await command.ScalarBoolAsync(ct));
    }

    public async Task<Guid?> FindMediaAsync(Guid library, string relativePath, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT id FROM library_media WHERE library_id=@library AND relative_path=@path");
        command.Parameters.AddWithValue("library", library);
        command.Parameters.AddWithValue("path", relativePath);
        return await command.ScalarGuidAsync(ct);
    }
}
