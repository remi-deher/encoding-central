using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using Encoding.Contracts.Libraries;

namespace Encoding.Api.Libraries;

public sealed class LibraryWorkerAccessValidationException(IReadOnlyDictionary<string, string[]> errors)
    : Exception("La configuration des accès de bibliothèque est invalide.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

public sealed class LibraryWorkerAccessRepository(DbDataSource dataSource)
{
    public async Task<LibraryWorkerAccessResponse?> SetEnabledAsync(
        Guid libraryId,
        Guid nodeId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE library_node_accesses
            SET is_enabled = @is_enabled, updated_at = clock_timestamp()
            WHERE library_id = @library_id AND node_id = @node_id AND mode <> 'unsupported'
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(sql, connection);
        command.Parameters.AddWithValue("library_id", libraryId);
        command.Parameters.AddWithValue("node_id", nodeId);
        command.Parameters.AddWithValue("is_enabled", isEnabled);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return null;

        var accesses = await ListAsync(libraryId, cancellationToken);
        return accesses?.SingleOrDefault(item => item.NodeId == nodeId);
    }

    public async Task<IReadOnlyList<LibraryWorkerAccessResponse>?> ListAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var exists = Sql.Command("SELECT EXISTS (SELECT 1 FROM libraries WHERE id = @id)", connection))
        {
            exists.Parameters.AddWithValue("id", libraryId);
            if ((await exists.ScalarBoolAsync(cancellationToken)) is false) return null;
        }

        const string sql = """
            SELECT a.library_id, a.node_id, n.name, a.mode, a.is_enabled, a.local_path, a.can_read, a.can_write,
                   a.transfer_source_node_id, source.name, a.updated_at
            FROM library_node_accesses a
            JOIN cluster_nodes n ON n.id = a.node_id AND n.deleted_at IS NULL
            LEFT JOIN cluster_nodes source ON source.id = a.transfer_source_node_id
            WHERE a.library_id = @library_id
            ORDER BY n.name, n.id
            """;
        await using var command = Sql.Command(sql, connection);
        command.Parameters.AddWithValue("library_id", libraryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var accesses = new List<LibraryWorkerAccessResponse>();
        while (await reader.ReadAsync(cancellationToken)) accesses.Add(Read(reader));
        return accesses;
    }

    public async Task<IReadOnlyList<LibraryWorkerAccessResponse>?> ConfigureAsync(
        Guid libraryId,
        ConfigureLibraryWorkerAccessRequest request,
        CancellationToken cancellationToken)
    {
        var nodes = request.Nodes ?? [];
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var exists = Sql.Command("SELECT EXISTS (SELECT 1 FROM libraries WHERE id = @id)", connection, transaction))
        {
            exists.Parameters.AddWithValue("id", libraryId);
            if ((await exists.ScalarBoolAsync(cancellationToken)) is false)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        var nodeRoles = new Dictionary<Guid, string[]>();
        if (nodes.Count > 0)
        {
            await using var nodeCommand = Sql.Command("SELECT id, roles FROM cluster_nodes WHERE id = ANY(@ids) AND deleted_at IS NULL", connection, transaction);
            nodeCommand.Parameters.AddWithValue("ids", nodes.Select(item => item.NodeId).Distinct().ToArray());
            await using var nodeReader = await nodeCommand.ExecuteReaderAsync(cancellationToken);
            while (await nodeReader.ReadAsync(cancellationToken)) nodeRoles[nodeReader.GetGuid(0)] = nodeReader.GetStringArray(1);
        }

        var errors = Validate(nodes, nodeRoles);
        if (errors.Count > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new LibraryWorkerAccessValidationException(errors);
        }

        await using (var delete = Sql.Command("DELETE FROM library_node_accesses WHERE library_id = @library_id", connection, transaction))
        {
            delete.Parameters.AddWithValue("library_id", libraryId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        const string insertSql = """
            INSERT INTO library_node_accesses
                (library_id, node_id, mode, is_enabled, local_path, can_read, can_write, transfer_source_node_id)
            VALUES
                (@library_id, @node_id, @mode, @is_enabled, @local_path, @can_read, @can_write, @transfer_source_node_id)
            """;
        foreach (var item in nodes)
        {
            await using var insert = Sql.Command(insertSql, connection, transaction);
            insert.Parameters.AddWithValue("library_id", libraryId);
            insert.Parameters.AddWithValue("node_id", item.NodeId);
            insert.Parameters.AddWithValue("mode", item.Mode.ToString().ToLowerInvariant());
            insert.Parameters.AddWithValue("is_enabled", item.Mode != LibraryWorkerAccessMode.Unsupported && item.IsEnabled);
            insert.Parameters.AddWithValue("local_path", (object?)Normalize(item.LocalPath) ?? DBNull.Value);
            insert.Parameters.AddWithValue("can_read", item.CanRead);
            insert.Parameters.AddWithValue("can_write", item.CanWrite);
            insert.Parameters.AddWithValue("transfer_source_node_id", (object?)item.TransferSourceNodeId ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await ListAsync(libraryId, cancellationToken);
    }

    private static Dictionary<string, string[]> Validate(
        IReadOnlyList<LibraryWorkerAccessRequest> nodes,
        IReadOnlyDictionary<Guid, string[]> registeredNodes)
    {
        var errors = new Dictionary<string, string[]>();
        if (nodes.Select(item => item.NodeId).Distinct().Count() != nodes.Count)
            errors["nodes"] = ["Chaque worker ne peut apparaître qu’une fois."];

        var configured = nodes.GroupBy(item => item.NodeId).ToDictionary(group => group.Key, group => group.First());
        for (var index = 0; index < nodes.Count; index++)
        {
            var item = nodes[index];
            var key = $"nodes[{index}]";
            if (item.NodeId == Guid.Empty || !registeredNodes.ContainsKey(item.NodeId))
                errors[$"{key}.nodeId"] = ["Ce worker n’existe pas dans le central."];

            switch (item.Mode)
            {
                case LibraryWorkerAccessMode.Direct:
                    if (string.IsNullOrWhiteSpace(item.LocalPath)) errors[$"{key}.localPath"] = ["Indiquez le chemin de cette bibliothèque sur le worker."];
                    if (!item.CanRead) errors[$"{key}.canRead"] = ["L’accès direct doit autoriser la lecture de la source."];
                    if (item.TransferSourceNodeId is not null) errors[$"{key}.transferSourceNodeId"] = ["Un accès direct ne peut pas référencer un worker source."];
                    break;
                case LibraryWorkerAccessMode.Unsupported:
                    if (!string.IsNullOrWhiteSpace(item.LocalPath) || item.CanRead || item.CanWrite || item.TransferSourceNodeId is not null)
                        errors[key] = ["Un worker sans accès ne peut déclarer ni chemin, ni droit, ni source de transfert."];
                    break;
                case LibraryWorkerAccessMode.Transfer:
                    if (item.LocalPath is not null) errors[$"{key}.localPath"] = ["Le worker traite une copie locale temporaire ; aucun chemin de bibliothèque n’est requis."];
                    if (!item.CanRead || !item.CanWrite) errors[key] = ["Le worker doit pouvoir lire la copie et renvoyer le résultat."];
                    if (!item.TransferSourceNodeId.HasValue || item.TransferSourceNodeId == item.NodeId)
                    {
                        errors[$"{key}.transferSourceNodeId"] = ["Sélectionnez un autre worker avec accès direct à la bibliothèque."];
                    }
                    else
                    {
                        if (!registeredNodes.TryGetValue(item.NodeId, out var roles) || !roles.Contains("worker", StringComparer.OrdinalIgnoreCase))
                            errors[$"{key}.nodeId"] = ["Le transfert doit cibler un worker ayant le rôle worker."];
                        if (!configured.TryGetValue(item.TransferSourceNodeId.Value, out var source)
                            || source.Mode != LibraryWorkerAccessMode.Direct || !source.CanRead || !source.CanWrite
                            || (item.IsEnabled && !source.IsEnabled))
                            errors[$"{key}.transferSourceNodeId"] = ["Le worker source doit déclarer un accès direct en lecture et en écriture dans cette configuration."];
                    }
                    break;
                default:
                    errors[$"{key}.mode"] = ["Ce mode d’accès n’est pas pris en charge."];
                    break;
            }
        }
        return errors;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static LibraryWorkerAccessResponse Read(DbDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
        Enum.Parse<LibraryWorkerAccessMode>(reader.GetString(3), true),
        reader.GetBoolean(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetBoolean(6), reader.GetBoolean(7),
        reader.IsDBNull(8) ? null : reader.GetGuid(8), reader.IsDBNull(9) ? null : reader.GetString(9),
        new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(10), DateTimeKind.Utc)));
}
