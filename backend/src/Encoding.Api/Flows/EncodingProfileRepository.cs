using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using System.Text.Json;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Flows;

public sealed class EncodingProfileRepository(DbDataSource dataSource)
{
    public async Task<IReadOnlyList<EncodingProfileSummary>> ListAsync(CancellationToken ct)
    {
        var profiles = new List<EncodingProfileSummary> { BuiltInSummary };
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using (var command = Sql.Command("""
            SELECT id,draft_revision,draft_name,draft_description,published_version,jsonb_array_length(draft_targets),updated_at
            FROM encoding_profiles ORDER BY lower(draft_name),id
            """, connection))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) profiles.Add(ReadSummary(reader, []));
        }
        if (profiles.Count == 1) return profiles;
        var versions = new Dictionary<Guid, List<EncodingProfileVersionSummary>>();
        await using (var command = Sql.Command("""
            SELECT profile_id,version,name,description,created_at FROM encoding_profile_versions
            WHERE profile_id=ANY(@ids) ORDER BY version DESC
            """, connection))
        {
            command.Parameters.AddWithValue("ids", profiles.Where(profile => !profile.IsBuiltIn).Select(profile => profile.Id).ToArray());
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var version = ReadVersionSummary(reader);
                if (!versions.TryGetValue(version.ProfileId, out var list)) versions[version.ProfileId] = list = [];
                list.Add(version);
            }
        }
        return profiles.Select(profile => profile.IsBuiltIn ? profile : profile with { Versions = versions.GetValueOrDefault(profile.Id) ?? [] }).ToArray();
    }

    public async Task<EncodingProfileDetails?> GetAsync(Guid id, CancellationToken ct)
    {
        if (id == BuiltInEncodingProfiles.BalancedId)
            return new(BuiltInSummary, new(BuiltInEncodingProfiles.Balanced.Name,
                BuiltInEncodingProfiles.Balanced.Description, BuiltInEncodingProfiles.Balanced.Targets));
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        EncodingProfileSummary summary;
        EncodingProfileDraft draft;
        await using (var command = Sql.Command("""
            SELECT id,draft_revision,draft_name,draft_description,published_version,jsonb_array_length(draft_targets),updated_at,draft_targets::text
            FROM encoding_profiles WHERE id=@id
            """, connection))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            summary = ReadSummary(reader, []);
            draft = new(reader.GetString(2), reader.GetString(3), DeserializeTargets(reader.GetString(7)));
        }
        return new(summary with { Versions = await ListVersionsAsync(id, connection, ct) }, draft);
    }

    public async Task<EncodingProfileVersion?> GetVersionAsync(Guid id, int version, CancellationToken ct)
    {
        if (id == BuiltInEncodingProfiles.BalancedId && version == 1)
        {
            var profile = BuiltInEncodingProfiles.Balanced;
            return new(id, 1, profile.Name, profile.Description, profile.Targets, DateTimeOffset.UnixEpoch);
        }
        await using var command = dataSource.CreateCommand("""
            SELECT profile_id,version,name,description,targets::text,created_at
            FROM encoding_profile_versions WHERE profile_id=@id AND version=@version
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("version", version);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadVersion(reader) : null;
    }

    public async Task<EncodingProfileDetails?> CreateAsync(Guid id, EncodingProfileDraft draft, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO encoding_profiles(id,draft_name,draft_description,draft_targets)
            VALUES(@id,@name,@description,@targets)
            """);
        command.Parameters.AddWithValue("id", id);
        AddDraft(command, draft);
        await command.ExecuteNonQueryAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<EncodingProfileDetails?> SaveDraftAsync(Guid id, SaveEncodingProfileDraftRequest request, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE encoding_profiles SET draft_revision=draft_revision+1,draft_name=@name,
                draft_description=@description,draft_targets=@targets,updated_at=now()
            WHERE id=@id AND draft_revision=@revision
            """);
        AddDraft(command, request.Draft);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("revision", request.ExpectedRevision);
        if (await command.ExecuteNonQueryAsync(ct) == 0) return null;
        return await GetAsync(id, ct);
    }

    public async Task<EncodingProfileVersion?> PublishAsync(Guid id, int expectedRevision, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        string name, description;
        IReadOnlyList<EncodingProfileTarget> targets;
        int version;
        await using (var read = Sql.Command("""
            SELECT draft_name,draft_description,draft_targets::text,published_version
            FROM encoding_profiles WHERE id=@id AND draft_revision=@revision FOR UPDATE
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("id", id);
            read.Parameters.AddWithValue("revision", expectedRevision);
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            name = reader.GetString(0);
            description = reader.GetString(1);
            targets = DeserializeTargets(reader.GetString(2));
            version = reader.GetInt32(3) + 1;
        }
        await using (var insert = Sql.Command("""
            INSERT INTO encoding_profile_versions(profile_id,version,name,description,targets)
            VALUES(@id,@version,@name,@description,@targets)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("version", version);
            insert.Parameters.AddWithValue("name", name);
            insert.Parameters.AddWithValue("description", description);
            insert.Parameters.AddWithValue("targets", SqlKind.Json, JsonSerializer.Serialize(targets, FlowRepository.JsonOptions));
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using (var update = Sql.Command("UPDATE encoding_profiles SET published_version=@version,updated_at=now() WHERE id=@id", connection, transaction))
        {
            update.Parameters.AddWithValue("id", id);
            update.Parameters.AddWithValue("version", version);
            await update.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return await GetVersionAsync(id, version, ct);
    }

    private static EncodingProfileSummary BuiltInSummary => new(
        BuiltInEncodingProfiles.BalancedId, 1, BuiltInEncodingProfiles.Balanced.Name,
        BuiltInEncodingProfiles.Balanced.Description, 1, BuiltInEncodingProfiles.Balanced.Targets.Count,
        DateTimeOffset.UnixEpoch, true,
        [new(BuiltInEncodingProfiles.BalancedId, 1, BuiltInEncodingProfiles.Balanced.Name,
            BuiltInEncodingProfiles.Balanced.Description, DateTimeOffset.UnixEpoch)]);

    private static async Task<IReadOnlyList<EncodingProfileVersionSummary>> ListVersionsAsync(Guid id, DbConnection connection, CancellationToken ct)
    {
        var versions = new List<EncodingProfileVersionSummary>();
        await using var command = Sql.Command("""
            SELECT profile_id,version,name,description,created_at FROM encoding_profile_versions
            WHERE profile_id=@id ORDER BY version DESC
            """, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) versions.Add(ReadVersionSummary(reader));
        return versions;
    }

    private static EncodingProfileSummary ReadSummary(DbDataReader reader, IReadOnlyList<EncodingProfileVersionSummary> versions) =>
        new(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetFieldValue<DateTimeOffset>(6), false, versions);

    private static EncodingProfileVersionSummary ReadVersionSummary(DbDataReader reader) =>
        new(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4));

    private static EncodingProfileVersion ReadVersion(DbDataReader reader) =>
        new(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), DeserializeTargets(reader.GetString(4)), reader.GetFieldValue<DateTimeOffset>(5));

    private static IReadOnlyList<EncodingProfileTarget> DeserializeTargets(string json) =>
        JsonSerializer.Deserialize<IReadOnlyList<EncodingProfileTarget>>(json, FlowRepository.JsonOptions) ?? [];

    private static void AddDraft(DbCommand command, EncodingProfileDraft draft)
    {
        command.Parameters.AddWithValue("name", draft.Name.Trim());
        command.Parameters.AddWithValue("description", draft.Description.Trim());
        command.Parameters.AddWithValue("targets", SqlKind.Json, JsonSerializer.Serialize(draft.Targets, FlowRepository.JsonOptions));
    }
}
