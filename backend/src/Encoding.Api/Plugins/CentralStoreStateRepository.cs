using Encoding.Api.Infrastructure.Data;
using System.Data.Common;

namespace Encoding.Api.Plugins;

/// <summary>Révision globale de l'état Store du central (migration 0077) : une seule ligne, monotone.</summary>
public sealed record CentralStoreRevision(long Revision, DateTimeOffset UpdatedAt, bool Changed);

public sealed class CentralStoreStateRepository(DbDataSource dataSource)
{
    /// <summary>
    /// Aligne la révision sur l'empreinte de l'ensemble installé : +1 si l'empreinte diffère de celle enregistrée, sinon aucun effet.
    /// Une seule instruction (condition dans le WHERE) : deux appels concurrents avec la même empreinte n'incrémentent qu'une fois.
    /// </summary>
    public async Task<CentralStoreRevision> SyncAsync(string digest, CancellationToken ct)
    {
        await using var update = dataSource.CreateCommand($"""
            UPDATE central_store_state SET revision = revision + 1, state_digest = @digest, updated_at = {Sql.UtcNow}
            WHERE id = 1 AND state_digest <> @digest
            RETURNING revision, updated_at
            """);
        update.Parameters.AddWithValue("digest", SqlKind.Text, digest);
        await using (var reader = await update.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct)) return new(reader.GetInt64(0), ParseInstant(reader.GetString(1)), true);
        return await GetAsync(ct);
    }

    public async Task<CentralStoreRevision> GetAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT revision, updated_at FROM central_store_state WHERE id = 1");
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("La ligne central_store_state est absente : migration 0077 non appliquée.");
        return new(reader.GetInt64(0), ParseInstant(reader.GetString(1)), false);
    }

    private static DateTimeOffset ParseInstant(string value) =>
        DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
}
