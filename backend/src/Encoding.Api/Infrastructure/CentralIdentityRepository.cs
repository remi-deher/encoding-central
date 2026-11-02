using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using System.Text.Json;

namespace Encoding.Api.Infrastructure;

/// <summary>Identité du central : un identifiant stable et un nom d'affichage modifiable. Le central existe dès l'installation.</summary>
public sealed record CentralIdentity(Guid Id, string Name);

public sealed class CentralIdentityRepository(DbDataSource dataSource)
{
    public const int MaximumNameLength = 128;

    public async Task<CentralIdentity> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command("SELECT id, name FROM cluster_identity WHERE singleton = true", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("L’identité du central est absente.");
        return new CentralIdentity(reader.GetGuid(0), reader.GetString(1));
    }

    public static bool IsValidName(string? name) => name?.Trim() is { Length: > 0 and <= MaximumNameLength };

    /// <summary>Renomme le central.</summary>
    public async Task RenameAsync(string name, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("UPDATE cluster_identity SET name = @name WHERE singleton = true");
        command.Parameters.AddWithValue("name", name.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
