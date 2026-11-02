using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using Encoding.Contracts.Auth;

namespace Encoding.Api.Nodes;

/// <summary>
/// Mode « accepter la première liaison » (architecture 0040) : désactivé par défaut, activé explicitement par un administrateur,
/// à usage unique (consommé par la première demande de rattachement) et à durée limitée. Partagé par tous les serveurs du cluster.
/// </summary>
public sealed class FirstLinkRepository(DbDataSource dataSource)
{
    public const int MaximumMinutes = 60;

    public async Task<FirstLinkResponse> GetAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT expires_at FROM first_link_acceptance WHERE expires_at > clock_timestamp()");
        return await command.ScalarInstantAsync(ct) is {} expiresAt
            ? new FirstLinkResponse(true, new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)))
            : new FirstLinkResponse(false, null);
    }

    public async Task<FirstLinkResponse> EnableAsync(int minutes, string administrator, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO first_link_acceptance(id, expires_at, enabled_by) VALUES (true, clock_timestamp() + @minutes * interval '1 minute', @by)
            ON CONFLICT (id) DO UPDATE SET expires_at = EXCLUDED.expires_at, enabled_by = EXCLUDED.enabled_by
            RETURNING expires_at
            """);
        command.Parameters.AddWithValue("minutes", minutes);
        command.Parameters.AddWithValue("by", administrator);
        var expiresAt = (await command.ScalarInstantAsync(ct))!.Value;
        return new FirstLinkResponse(true, new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)));
    }

    public async Task DisableAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM first_link_acceptance");
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Consomme le mode de façon atomique : une seule demande l'obtient, même si plusieurs serveurs la reçoivent en même temps.</summary>
    public async Task<string?> TryConsumeAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM first_link_acceptance WHERE expires_at > clock_timestamp() RETURNING enabled_by");
        return await command.ExecuteScalarAsync(ct) as string;
    }
}
