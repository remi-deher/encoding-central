using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using Encoding.Contracts.Auth;

namespace Encoding.Api.Nodes;

/// <summary>Fenêtre d’enrôlement direct ouverte par un administrateur ; fermée par défaut et partagée par tous les serveurs.</summary>
public sealed class EnrollmentWindowRepository(DbDataSource dataSource)
{
    public const int MaximumMinutes = 60;

    public async Task<EnrollmentWindowResponse> GetAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT open_until FROM enrollment_window WHERE open_until > clock_timestamp()");
        return await command.ScalarInstantAsync(ct) is {} openUntil
            ? new EnrollmentWindowResponse(true, new DateTimeOffset(DateTime.SpecifyKind(openUntil, DateTimeKind.Utc)))
            : new EnrollmentWindowResponse(false, null);
    }

    public async Task<EnrollmentWindowResponse> OpenAsync(int minutes, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO enrollment_window(id, open_until) VALUES (true, clock_timestamp() + @minutes * interval '1 minute')
            ON CONFLICT (id) DO UPDATE SET open_until = EXCLUDED.open_until
            RETURNING open_until
            """);
        command.Parameters.AddWithValue("minutes", minutes);
        var openUntil = (await command.ScalarInstantAsync(ct))!.Value;
        return new EnrollmentWindowResponse(true, new DateTimeOffset(DateTime.SpecifyKind(openUntil, DateTimeKind.Utc)));
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM enrollment_window");
        await command.ExecuteNonQueryAsync(ct);
    }
}
