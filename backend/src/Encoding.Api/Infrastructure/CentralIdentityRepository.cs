using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using System.Text.Json;

namespace Encoding.Api.Infrastructure;

/// <summary>
/// Identité du central local. <paramref name="Standalone"/> : le worker est autonome (architecture 0045) tant qu'aucun central n'a été
/// créé explicitement et qu'il est seul ; un second worker fait du central une réalité sans autre action.
/// </summary>
public sealed record CentralIdentity(Guid Id, string Name, bool Standalone);

public sealed class CentralIdentityRepository(DbDataSource dataSource)
{
    public const int MaximumNameLength = 128;

    public async Task<CentralIdentity> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(
            "SELECT id, name, established, (SELECT count(*) FROM cluster_nodes WHERE deleted_at IS NULL) FROM cluster_identity WHERE singleton = true", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("L’identité du central est absente.");
        var established = Convert.ToBoolean(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture);
        var nodes = Convert.ToInt64(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture);
        var id = reader.GetGuid(0);
        var name = reader.GetString(1);
        var standalone = !established && nodes <= 1;
        return new CentralIdentity(id, name, standalone);
    }

    public static bool IsValidName(string? name) => name?.Trim() is { Length: > 0 and <= MaximumNameLength };

    /// <summary>Crée le central à partir du worker autonome : lui donne un nom et le déclare établi. Faux si ce worker est déjà dans un central.</summary>
    public async Task<bool> CreateAsync(string name, CancellationToken cancellationToken)
    {
        if (!(await GetAsync(cancellationToken)).Standalone) return false;
        await SetNameAsync(name, establish: true, cancellationToken);
        return true;
    }

    /// <summary>Renomme un central existant. Faux pour un worker autonome.</summary>
    public async Task<bool> RenameAsync(string name, CancellationToken cancellationToken)
    {
        if ((await GetAsync(cancellationToken)).Standalone) return false;
        await SetNameAsync(name, establish: true, cancellationToken);
        return true;
    }

    private async Task SetNameAsync(string name, bool establish, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("UPDATE cluster_identity SET name = @name, established = @established WHERE singleton = true");
        command.Parameters.AddWithValue("name", name.Trim());
        command.Parameters.AddWithValue("established", establish);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
