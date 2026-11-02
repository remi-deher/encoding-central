using System.Data.Common;
using System.Security.Cryptography;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Auth;

namespace Encoding.Api.Auth;

public sealed record ApiTokenRecord(Guid Id, string Name, string Role, IReadOnlyList<string> Scopes);

public sealed class ApiTokenRepository(DbDataSource dataSource)
{
    public const string ClusterDefaultTokenName = "Cluster Default Token";

    public async Task<(ApiTokenResponse Summary, string Plaintext)> CreateTokenAsync(
        string name,
        string role,
        DateTimeOffset? expiresAt,
        CancellationToken ct,
        IReadOnlyList<string>? scopes = null)
    {
        scopes ??= [];
        var id = Guid.NewGuid();
        var plaintext = "enc_pat_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var prefix = plaintext[..16] + "...";
        var hash = AuthService.TokenHash(plaintext);
        var normalizedRole = string.Equals(role, "viewer", StringComparison.OrdinalIgnoreCase) ? "viewer" : "admin";

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = Sql.Command($"""
            INSERT INTO api_tokens (id, name, token_hash, token_prefix, role, is_active, created_at, expires_at, scopes)
            VALUES (@id, @name, @hash, @prefix, @role, true, {Sql.UtcNow}, @expires, @scopes)
            """, connection);

        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", name.Trim());
        command.Parameters.AddWithValue("hash", SqlKind.Bytes, hash);
        command.Parameters.AddWithValue("prefix", prefix);
        command.Parameters.AddWithValue("role", normalizedRole);
        command.Parameters.AddWithValue("expires", (object?)expiresAt ?? DBNull.Value);
        command.Parameters.AddWithValue("scopes", (object?)ApiTokenScopes.Serialize(scopes) ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(ct);

        var summary = new ApiTokenResponse(
            id,
            name.Trim(),
            prefix,
            normalizedRole,
            true,
            DateTimeOffset.UtcNow,
            expiresAt,
            null,
            null,
            scopes);

        return (summary, plaintext);
    }

    /// <summary>
    /// Crée le jeton administrateur du cluster lors de sa création ou adoption s'il n'existe pas encore.
    /// Retourne le jeton en clair s'il vient d'être généré, ou null s'il existait déjà.
    /// </summary>
    public async Task<string?> EnsureClusterTokenAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var check = Sql.Command("""
            SELECT EXISTS(SELECT 1 FROM api_tokens WHERE name = @name AND is_active = true AND revoked_at IS NULL)
            """, connection);
        check.Parameters.AddWithValue("name", ClusterDefaultTokenName);
        var exists = await check.ScalarBoolAsync(ct);
        if (exists) return null;

        var (_, plaintext) = await CreateTokenAsync(ClusterDefaultTokenName, "admin", null, ct);
        return plaintext;
    }

    public async Task<bool> HasClusterTokenAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var check = Sql.Command("""
            SELECT EXISTS(SELECT 1 FROM api_tokens WHERE name = @name AND is_active = true AND revoked_at IS NULL)
            """, connection);
        check.Parameters.AddWithValue("name", ClusterDefaultTokenName);
        return await check.ScalarBoolAsync(ct);
    }

    public async Task<ApiTokenRecord?> ValidateTokenAsync(byte[] tokenHash, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = Sql.Command($"""
            SELECT id, name, role, last_used_at, scopes
            FROM api_tokens
            WHERE token_hash = @hash
              AND is_active = true
              AND revoked_at IS NULL
              AND (expires_at IS NULL OR expires_at > {Sql.UtcNow})
            """, connection);
        command.Parameters.AddWithValue("hash", SqlKind.Bytes, tokenHash);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var id = reader.GetGuid(0);
        var name = reader.GetString(1);
        var role = reader.GetString(2);
        var lastUsedAt = reader.IsDBNull(3) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(3);
        var scopes = ApiTokenScopes.Parse(reader.IsDBNull(4) ? null : reader.GetString(4));
        await reader.CloseAsync();

        var now = DateTimeOffset.UtcNow;
        if (lastUsedAt is null || lastUsedAt < now - TimeSpan.FromMinutes(1))
        {
            try
            {
                await using var touch = Sql.Command($"UPDATE api_tokens SET last_used_at = {Sql.UtcNow} WHERE id = @id", connection);
                touch.Parameters.AddWithValue("id", id);
                await touch.ExecuteNonQueryAsync(ct);
            }
            catch
            {
                // Un échec de mise à jour de télémétrie ne doit jamais invalider l'authentification d'un jeton valide.
            }
        }

        return new ApiTokenRecord(id, name, role, scopes);
    }

    public async Task<IReadOnlyList<ApiTokenResponse>> ListAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = Sql.Command("""
            SELECT id, name, token_prefix, role, is_active, created_at, expires_at, last_used_at, revoked_at, scopes
            FROM api_tokens
            ORDER BY created_at DESC, id
            """, connection);

        await using var reader = await command.ExecuteReaderAsync(ct);
        var list = new List<ApiTokenResponse>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetBoolean(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                ApiTokenScopes.Parse(reader.IsDBNull(9) ? null : reader.GetString(9))));
        }
        return list;
    }

    /// <summary>Identifiants et états de révocation connus de ce nœud, pour contrôler la propagation du cluster.</summary>
    public async Task<IReadOnlyList<ApiTokenReplicationEntry>> ReplicationStateAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = Sql.Command("SELECT id, is_active, revoked_at FROM api_tokens ORDER BY id", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var list = new List<ApiTokenReplicationEntry>();
        while (await reader.ReadAsync(ct)) list.Add(new(reader.GetGuid(0), !reader.GetBoolean(1) || !reader.IsDBNull(2)));
        return list;
    }

    public async Task<bool> RevokeAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = Sql.Command($"""
            UPDATE api_tokens
            SET is_active = false, revoked_at = {Sql.UtcNow}
            WHERE id = @id AND is_active = true
            """, connection);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = Sql.Command("""
            DELETE FROM api_tokens
            WHERE id = @id
            """, connection);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }
}
