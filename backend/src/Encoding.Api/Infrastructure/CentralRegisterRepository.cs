using System.Data.Common;
using System.Text.Json;
using Encoding.Api.Infrastructure.Data;

namespace Encoding.Api.Infrastructure;

/// <summary>
/// Registres clé/valeur JSON du central (activation des plugins et des outils, sessions de l'assistant IA chiffrées).
/// La table <c>cluster_sync_registers</c> garde son nom historique : elle n'est plus répliquée vers d'autres serveurs
/// ([ADR 0036](../../../../docs/architecture/adr/0036-separation-stricte-central-worker.md)).
/// </summary>
public sealed class CentralRegisterRepository(DbDataSource dataSource)
{
    public async Task SetAsync(string key, string jsonValue, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256) throw new ArgumentException("Clé de registre invalide.", nameof(key));
        using var _ = JsonDocument.Parse(jsonValue);
        await using var command = dataSource.CreateCommand("""
            INSERT INTO cluster_sync_registers(register_key, value, occurred_at, origin_id, sequence)
            VALUES (@key, @value, @occurred_at, @origin_id, 0)
            ON CONFLICT (register_key) DO UPDATE SET value = EXCLUDED.value, occurred_at = EXCLUDED.occurred_at
            """);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("value", SqlKind.Json, jsonValue);
        command.Parameters.AddWithValue("occurred_at", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("origin_id", Guid.Empty);
        await command.ExecuteNonQueryAsync(ct);
    }
}
