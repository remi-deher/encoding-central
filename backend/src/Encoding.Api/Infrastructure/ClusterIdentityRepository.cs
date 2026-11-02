using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using System.Text.Json;

namespace Encoding.Api.Infrastructure;

/// <summary>
/// Identité du cluster local. <paramref name="Standalone"/> : le nœud est autonome (architecture 0045) tant qu'aucun cluster n'a été
/// créé explicitement et qu'il est seul ; un second nœud fait du cluster une réalité sans autre action.
/// </summary>
public sealed record ClusterIdentity(Guid Id, string Name, bool Standalone);

public sealed class ClusterIdentityRepository(DbDataSource dataSource)
{
    public const int MaximumNameLength = 128;

    public async Task<ClusterIdentity> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(
            "SELECT id, name, established, (SELECT count(*) FROM cluster_nodes WHERE deleted_at IS NULL) FROM cluster_identity WHERE singleton = true", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("L’identité du cluster est absente.");
        var established = Convert.ToBoolean(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture);
        var nodes = Convert.ToInt64(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture);
        var id = reader.GetGuid(0);
        var name = reader.GetString(1);
        var standalone = !established && nodes <= 1;
        return new ClusterIdentity(id, name, standalone);
    }

    public static bool IsValidName(string? name) => name?.Trim() is { Length: > 0 and <= MaximumNameLength };

    /// <summary>Crée le cluster à partir du nœud autonome : lui donne un nom et le déclare établi. Faux si ce nœud est déjà dans un cluster.</summary>
    public async Task<bool> CreateAsync(string name, CancellationToken cancellationToken)
    {
        if (!(await GetAsync(cancellationToken)).Standalone) return false;
        await SetNameAsync(name, establish: true, cancellationToken);
        return true;
    }

    /// <summary>Renomme un cluster existant. Faux pour un nœud autonome.</summary>
    public async Task<bool> RenameAsync(string name, CancellationToken cancellationToken)
    {
        if ((await GetAsync(cancellationToken)).Standalone) return false;
        await SetNameAsync(name, establish: true, cancellationToken);
        return true;
    }

    /// <summary>Adopte l'identité du cluster d'accueil après un rattachement confirmé du nœud local.</summary>
    public async Task AdoptAsync(Guid id, string name, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty || !IsValidName(name)) throw new ArgumentException("Identité du cluster invalide.");
        var reconciliation = new ClusterReconciliationRepository(dataSource);
        await reconciliation.EnsureIdentityAsync(cancellationToken);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var update = Sql.Command("UPDATE cluster_identity SET id = @id, name = @name, established = true WHERE singleton = true", connection, transaction))
        {
            update.Parameters.AddWithValue("id", id);
            update.Parameters.AddWithValue("name", name.Trim());
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var reset = Sql.Command("UPDATE cluster_sync_identity SET origin_id = @origin, next_sequence = 0 WHERE singleton = true", connection, transaction))
        {
            reset.Parameters.AddWithValue("origin", Guid.NewGuid());
            await reset.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var clearRegisters = Sql.Command("DELETE FROM cluster_sync_registers", connection, transaction))
            await clearRegisters.ExecuteNonQueryAsync(cancellationToken);
        await using (var clearCursors = Sql.Command("DELETE FROM cluster_sync_peer_vectors", connection, transaction))
            await clearCursors.ExecuteNonQueryAsync(cancellationToken);
        await using (var clearEntityVersions = Sql.Command("DELETE FROM cluster_sync_entities", connection, transaction))
            await clearEntityVersions.ExecuteNonQueryAsync(cancellationToken);
        await using (var clearPending = Sql.Command("DELETE FROM cluster_sync_pending_projections", connection, transaction))
            await clearPending.ExecuteNonQueryAsync(cancellationToken);
        await using (var clearTableCursors = Sql.Command("DELETE FROM cluster_sync_table_cursors", connection, transaction))
            await clearTableCursors.ExecuteNonQueryAsync(cancellationToken);
        await using (var resetCapture = Sql.Command("UPDATE cluster_sync_capture_tables SET revision=revision+1,captured_revision=0,full_scan_revision=revision+1; DELETE FROM cluster_sync_dirty_entities",
            connection, transaction))
            await resetCapture.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task SetNameAsync(string name, bool establish, CancellationToken cancellationToken)
    {
        if (establish)
        {
            var reconciliation = new ClusterReconciliationRepository(dataSource);
            await reconciliation.SetRegisterAsync("cluster.name", System.Text.Json.JsonSerializer.Serialize(name.Trim()), cancellationToken);
        }
        else
        {
            await using var command = dataSource.CreateCommand("UPDATE cluster_identity SET name = @name, established = false WHERE singleton = true");
            command.Parameters.AddWithValue("name", name.Trim());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
