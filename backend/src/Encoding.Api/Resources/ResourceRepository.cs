using System.Data.Common;
using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Resources;

public sealed class ResourceRepository(DbDataSource dataSource, ResourceBlobStore blobs)
{
    public async Task<IReadOnlyList<ResourceSummary>> ListAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var resources = new List<(Guid Id, string Kind, string Name, string Description, DateTimeOffset UpdatedAt)>();
        await using (var command = Sql.Command("SELECT id,kind,name,description,updated_at FROM flow_resources ORDER BY lower(name),id", connection))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                resources.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)));
        }
        var latest = new Dictionary<Guid, ResourceVersion>();
        await using (var command = Sql.Command("SELECT resource_id,version,files::text,total_bytes,created_at FROM flow_resource_versions ORDER BY version", connection))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) { var version = ReadVersion(reader); latest[version.ResourceId] = version; }
        }
        return resources.Select(item => Summary(item.Id, item.Kind, item.Name, item.Description, item.UpdatedAt, latest.GetValueOrDefault(item.Id))).ToArray();
    }

    public async Task<ResourceDetails?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        (string Kind, string Name, string Description, DateTimeOffset UpdatedAt) header;
        await using (var command = Sql.Command("SELECT kind,name,description,updated_at FROM flow_resources WHERE id=@id", connection))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            header = (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3));
        }
        var versions = new List<ResourceVersion>();
        await using (var command = Sql.Command("SELECT resource_id,version,files::text,total_bytes,created_at FROM flow_resource_versions WHERE resource_id=@id ORDER BY version DESC", connection))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) versions.Add(ReadVersion(reader));
        }
        return new(Summary(id, header.Kind, header.Name, header.Description, header.UpdatedAt, versions.FirstOrDefault()), versions);
    }

    public async Task<ResourceVersion?> GetVersionAsync(Guid id, int version, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT resource_id,version,files::text,total_bytes,created_at FROM flow_resource_versions WHERE resource_id=@id AND version=@version");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("version", version);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadVersion(reader) : null;
    }

    public async Task<ResourceDetails?> UpdateAsync(Guid id, string name, string description, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("UPDATE flow_resources SET name=@name,description=@description,updated_at=now() WHERE id=@id");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", name.Trim());
        command.Parameters.AddWithValue("description", description.Trim());
        return await command.ExecuteNonQueryAsync(ct) > 0 ? await GetAsync(id, ct) : null;
    }

    /// <summary>Crée la ressource avec sa première version ; les fichiers doivent déjà être dans le magasin.</summary>
    public async Task<ResourceDetails?> CreateAsync(Guid id, string kind, string name, string description, IReadOnlyList<ResourceFileReference> files, CancellationToken ct)
    {
        var resolved = Resolve(kind, files);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var insert = Sql.Command("INSERT INTO flow_resources(id,kind,name,description) VALUES(@id,@kind,@name,@description)", connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("kind", kind);
            insert.Parameters.AddWithValue("name", name.Trim());
            insert.Parameters.AddWithValue("description", description.Trim());
            await insert.ExecuteNonQueryAsync(ct);
        }
        await InsertVersionAsync(id, 1, resolved, connection, transaction, ct);
        await transaction.CommitAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<ResourceVersion?> AddVersionAsync(Guid id, IReadOnlyList<ResourceFileReference> files, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        string kind;
        await using (var read = Sql.Command("SELECT kind FROM flow_resources WHERE id=@id", connection, transaction))
        {
            read.Parameters.AddWithValue("id", id);
            if (await read.ExecuteScalarAsync(ct) is not string value) return null;
            kind = value;
        }
        var resolved = Resolve(kind, files);
        int next;
        await using (var read = Sql.Command("SELECT COALESCE(MAX(version),0)+1 FROM flow_resource_versions WHERE resource_id=@id", connection, transaction))
        {
            read.Parameters.AddWithValue("id", id);
            next = Convert.ToInt32(await read.ExecuteScalarAsync(ct));
        }
        await InsertVersionAsync(id, next, resolved, connection, transaction, ct);
        await using (var update = Sql.Command("UPDATE flow_resources SET updated_at=now() WHERE id=@id", connection, transaction))
        {
            update.Parameters.AddWithValue("id", id);
            await update.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return await GetVersionAsync(id, next, ct);
    }

    /// <summary>Dernière version de chaque ressource (préchargement des workers).</summary>
    public async Task<IReadOnlyList<ResourceVersion>> LatestVersionsAsync(CancellationToken ct)
    {
        var latest = new Dictionary<Guid, ResourceVersion>();
        await using var command = dataSource.CreateCommand("SELECT resource_id,version,files::text,total_bytes,created_at FROM flow_resource_versions ORDER BY version");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) { var version = ReadVersion(reader); latest[version.ResourceId] = version; }
        return latest.Values.ToArray();
    }

    /// <summary>Retrouve un fichier par son empreinte dans les métadonnées (taille attendue pour une récupération).</summary>
    public async Task<ResourceFile?> FindFileAsync(string digest, CancellationToken ct)
    {
        if (!ResourceBlobStore.ValidDigest(digest)) return null;
        await using var command = dataSource.CreateCommand("SELECT resource_id,version,files::text,total_bytes,created_at FROM flow_resource_versions WHERE files::text LIKE @pattern");
        command.Parameters.AddWithValue("pattern", "%" + digest + "%");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (ReadVersion(reader).Files.FirstOrDefault(file => file.Sha256 == digest) is { } file) return file;
        return null;
    }

    /// <summary>Fichiers de toutes les versions que le magasin local n'a pas encore (un flow référence une version précise).</summary>
    public async Task<IReadOnlyList<ResourceFile>> MissingFilesAsync(Func<string, bool> isPresent, CancellationToken ct)
    {
        var files = new List<ResourceFile>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = Sql.Command("SELECT resource_id,version,files::text,total_bytes,created_at FROM flow_resource_versions ORDER BY version DESC", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) files.AddRange(ReadVersion(reader).Files);
        return files.Where(file => !isPresent(file.Sha256)).DistinctBy(file => file.Sha256).ToArray();
    }

    /// <summary>Flows (toutes versions) qui référencent la ressource, avec le bloc concerné.</summary>
    public async Task<IReadOnlyList<ResourceUsage>> UsageAsync(Guid id, CancellationToken ct)
    {
        var needle = id.ToString("D");
        var usage = new List<ResourceUsage>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = Sql.Command("SELECT flow_id,version,definition::text FROM flow_versions WHERE definition::text LIKE @pattern ORDER BY flow_id,version", connection);
        command.Parameters.AddWithValue("pattern", "%" + needle + "%");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var flow = JsonSerializer.Deserialize<FlowDefinition>(reader.GetString(2), FlowRepository.JsonOptions);
            if (flow is null) continue;
            foreach (var node in flow.Nodes)
                if (node.Configuration.Values.Any(value => value.ToString().Contains(needle, StringComparison.OrdinalIgnoreCase)))
                    usage.Add(new(reader.GetGuid(0), flow.Name, reader.GetInt32(1), node.Id, node.Name));
        }
        return usage;
    }

    /// <summary>Supprime la ressource et ses versions ; l'appelant a vérifié qu'aucun flow ne la référence.</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var details = await GetAsync(id, ct);
        if (details is null) return false;
        await using (var connection = await dataSource.OpenConnectionAsync(ct))
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            // Named deletes only — never interpolate a caller-supplied table/filter fragment.
            foreach (var sql in new[]
            {
                "DELETE FROM flow_resource_versions WHERE resource_id=@id",
                "DELETE FROM flow_resources WHERE id=@id",
            })
            {
                await using var delete = Sql.Command(sql, connection, transaction);
                delete.Parameters.AddWithValue("id", id);
                await delete.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        await RemoveUnreferencedBlobsAsync(details.Versions.SelectMany(version => version.Files).Select(file => file.Sha256).Distinct(), ct);
        return true;
    }

    private async Task RemoveUnreferencedBlobsAsync(IEnumerable<string> digests, CancellationToken ct)
    {
        foreach (var digest in digests)
        {
            await using var command = dataSource.CreateCommand("SELECT COUNT(*) FROM flow_resource_versions WHERE files::text LIKE @pattern");
            command.Parameters.AddWithValue("pattern", "%" + digest + "%");
            if (Convert.ToInt64(await command.ExecuteScalarAsync(ct)) == 0) blobs.Delete(digest);
        }
    }

    private IReadOnlyList<ResourceFile> Resolve(string kind, IReadOnlyList<ResourceFileReference> files)
    {
        if (files.Count is 0 or > ResourceBlobStore.MaxFilesPerVersion)
            throw new ResourceException($"Une version contient entre 1 et {ResourceBlobStore.MaxFilesPerVersion} fichiers.");
        var resolved = new List<ResourceFile>();
        foreach (var file in files)
        {
            var name = ResourceBlobStore.NormalizeName(file.Name, kind);
            if (resolved.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ResourceException($"Le fichier « {name} » est présent deux fois.");
            var digest = file.Sha256?.ToLowerInvariant();
            if (!ResourceBlobStore.ValidDigest(digest) || !blobs.Exists(digest!))
                throw new ResourceException($"Le fichier « {name} » n'a pas été envoyé.");
            resolved.Add(new(name, digest!, new FileInfo(blobs.PathFor(digest!)).Length));
        }
        if (resolved.Sum(item => item.Bytes) > ResourceBlobStore.MaxResourceBytes)
            throw new ResourceException("La ressource dépasse 100 Mio.");
        return resolved;
    }

    private static async Task InsertVersionAsync(Guid id, int version, IReadOnlyList<ResourceFile> files, DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        await using var insert = Sql.Command("INSERT INTO flow_resource_versions(resource_id,version,files,total_bytes) VALUES(@id,@version,@files,@bytes)", connection, transaction);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("version", version);
        insert.Parameters.AddWithValue("files", SqlKind.Json, JsonSerializer.Serialize(files, FlowRepository.JsonOptions));
        insert.Parameters.AddWithValue("bytes", files.Sum(item => item.Bytes));
        await insert.ExecuteNonQueryAsync(ct);
    }

    private static ResourceSummary Summary(Guid id, string kind, string name, string description, DateTimeOffset updatedAt, ResourceVersion? latest) =>
        new(id, kind, name, description, latest?.Version ?? 0, latest?.TotalBytes ?? 0, updatedAt);

    private static ResourceVersion ReadVersion(DbDataReader reader) =>
        new(reader.GetGuid(0), reader.GetInt32(1),
            JsonSerializer.Deserialize<IReadOnlyList<ResourceFile>>(reader.GetString(2), FlowRepository.JsonOptions) ?? [],
            reader.GetInt64(3), reader.GetFieldValue<DateTimeOffset>(4));
}
