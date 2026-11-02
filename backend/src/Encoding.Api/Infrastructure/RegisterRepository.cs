using System.Text.Json;
using Encoding.Api.Infrastructure.Data;

namespace Encoding.Api.Infrastructure;

/// <summary>Registre clé/valeur JSON local du central (activation des plugins et des outils, points de reprise IA chiffrés).</summary>
public sealed class RegisterRepository(System.Data.Common.DbDataSource dataSource)
{
    public async Task SetAsync(string key, string jsonValue, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256) throw new ArgumentException("Clé de registre invalide.", nameof(key));
        using var _ = JsonDocument.Parse(jsonValue);
        await using var command = dataSource.CreateCommand("""
            INSERT INTO registers(register_key, value, updated_at) VALUES (@key, @value, @updated_at)
            ON CONFLICT (register_key) DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at
            """);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("value", SqlKind.Json, jsonValue);
        command.Parameters.AddWithValue("updated_at", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(ct);
    }
}
