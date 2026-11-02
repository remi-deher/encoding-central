using System.Data.Common;
using System.Text.Json;
using Encoding.Api.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;

namespace Encoding.Api.Tools;

/// <summary>Connexion IA répliquée d’un fournisseur ; <see cref="Secret"/> est null après une déconnexion.</summary>
public sealed record AiCredentialRow(string Provider, long Revision, string? Secret, DateTimeOffset UpdatedAt);

/// <summary>
/// Table <c>ai_credentials</c>, réconciliée entre nœuds comme les paramètres de plugins : chiffrée au repos par la protection
/// locale du nœud, puis par la clé de données du cluster pendant l’échange.
/// </summary>
public sealed class AiCredentialStore(DbDataSource dataSource, IDataProtectionProvider protection)
{
    public const string Purpose = "Encoding.Ai.Credentials.v1";
    private readonly IDataProtector protector = protection.CreateProtector(Purpose);

    public async Task<AiCredentialRow?> GetAsync(string provider, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = Sql.Command("SELECT revision, encrypted_values, updated_at FROM ai_credentials WHERE provider=@provider", connection);
        command.Parameters.AddWithValue("provider", provider);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var revision = Convert.ToInt64(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);
        var updatedAt = DateTimeOffset.Parse(Convert.ToString(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture)!,
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
        using var document = JsonDocument.Parse(protector.Unprotect((byte[])reader.GetValue(1)));
        var root = document.RootElement;
        var secret = root.TryGetProperty("secret", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return new(provider, revision, secret, updatedAt);
    }

    /// <summary>Publie la connexion locale (ou sa suppression si <paramref name="secret"/> est null).</summary>
    public async Task PutAsync(string provider, string? secret, CancellationToken ct)
    {
        var payload = secret is null ? JsonSerializer.SerializeToUtf8Bytes(new { deleted = true }) : JsonSerializer.SerializeToUtf8Bytes(new { secret });
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = Sql.Command("""
            INSERT INTO ai_credentials(provider, revision, encrypted_values, updated_at) VALUES(@provider, 1, @values, clock_timestamp())
            ON CONFLICT(provider) DO UPDATE SET revision=ai_credentials.revision+1, encrypted_values=excluded.encrypted_values, updated_at=excluded.updated_at
            """, connection);
        command.Parameters.AddWithValue("provider", provider);
        command.Parameters.AddWithValue("values", SqlKind.Bytes, protector.Protect(payload));
        await command.ExecuteNonQueryAsync(ct);
    }
}
