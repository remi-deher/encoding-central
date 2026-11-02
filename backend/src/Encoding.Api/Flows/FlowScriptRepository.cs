using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Flows;

public sealed class FlowScriptRepository(DbDataSource dataSource)
{
    public async Task<IReadOnlyList<FlowScriptSummary>> ListAsync(CancellationToken ct)
    {
        var scripts = new List<FlowScriptSummary>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using (var command = Sql.Command("""
            SELECT id,draft_revision,draft_name,draft_language,draft_description,published_version,updated_at
            FROM flow_scripts ORDER BY lower(draft_name),id
            """, connection))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) scripts.Add(ReadSummary(reader, []));
        }
        if (scripts.Count == 0) return scripts;
        var versions = new Dictionary<Guid, List<FlowScriptVersionSummary>>();
        await using (var command = Sql.Command("""
            SELECT script_id,version,name,language,description,created_at FROM flow_script_versions
            WHERE script_id=ANY(@ids) ORDER BY version DESC
            """, connection))
        {
            command.Parameters.AddWithValue("ids", scripts.Select(script => script.Id).ToArray());
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var version = ReadVersionSummary(reader);
                if (!versions.TryGetValue(version.ScriptId, out var list)) versions[version.ScriptId] = list = [];
                list.Add(version);
            }
        }
        return scripts.Select(script => script with { Versions = versions.GetValueOrDefault(script.Id) ?? [] }).ToArray();
    }

    public async Task<FlowScriptDetails?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        FlowScriptSummary summary;
        FlowScriptDraft draft;
        await using (var command = Sql.Command("""
            SELECT id,draft_revision,draft_name,draft_language,draft_description,published_version,updated_at,draft_code
            FROM flow_scripts WHERE id=@id
            """, connection))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            summary = ReadSummary(reader, []);
            draft = new(reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(7));
        }
        return new(summary with { Versions = await ListVersionsAsync(id, connection, ct) }, draft);
    }

    public async Task<FlowScriptVersion?> GetVersionAsync(Guid id, int version, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT script_id,version,name,language,description,code,created_at
            FROM flow_script_versions WHERE script_id=@id AND version=@version
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("version", version);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadVersion(reader) : null;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var lockScript = Sql.Command("SELECT id FROM flow_scripts WHERE id=@id FOR UPDATE", connection, transaction))
        {
            lockScript.Parameters.AddWithValue("id", id);
            if (await lockScript.ExecuteScalarAsync(ct) is null) return false;
        }
        await using (var versions = Sql.Command("DELETE FROM flow_script_versions WHERE script_id=@id", connection, transaction))
        {
            versions.Parameters.AddWithValue("id", id);
            await versions.ExecuteNonQueryAsync(ct);
        }
        await using var script = Sql.Command("DELETE FROM flow_scripts WHERE id=@id", connection, transaction);
        script.Parameters.AddWithValue("id", id);
        var deleted = await script.ExecuteNonQueryAsync(ct) > 0;
        await transaction.CommitAsync(ct);
        return deleted;
    }

    public async Task<FlowScriptDetails?> CreateAsync(Guid id, FlowScriptDraft draft, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO flow_scripts(id,draft_name,draft_language,draft_description,draft_code)
            VALUES(@id,@name,@language,@description,@code)
            """);
        AddDraft(command, draft);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<FlowScriptDetails?> SaveDraftAsync(Guid id, SaveFlowScriptDraftRequest request, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE flow_scripts SET draft_revision=draft_revision+1,draft_name=@name,draft_language=@language,
                draft_description=@description,draft_code=@code,updated_at=now()
            WHERE id=@id AND draft_revision=@revision
            """);
        AddDraft(command, request.Draft);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("revision", request.ExpectedRevision);
        if (await command.ExecuteNonQueryAsync(ct) == 0) return null;
        return await GetAsync(id, ct);
    }

    public async Task<FlowScriptVersion?> PublishAsync(Guid id, int expectedRevision, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        string name, language, description, code;
        int version;
        await using (var read = Sql.Command("""
            SELECT draft_name,draft_language,draft_description,draft_code,published_version
            FROM flow_scripts WHERE id=@id AND draft_revision=@revision FOR UPDATE
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("id", id);
            read.Parameters.AddWithValue("revision", expectedRevision);
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            name = reader.GetString(0);
            language = reader.GetString(1);
            description = reader.GetString(2);
            code = reader.GetString(3);
            version = reader.GetInt32(4) + 1;
        }
        await using (var insert = Sql.Command("""
            INSERT INTO flow_script_versions(script_id,version,name,language,description,code)
            VALUES(@id,@version,@name,@language,@description,@code)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("version", version);
            insert.Parameters.AddWithValue("name", name);
            insert.Parameters.AddWithValue("language", language);
            insert.Parameters.AddWithValue("description", description);
            insert.Parameters.AddWithValue("code", code);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using (var update = Sql.Command("UPDATE flow_scripts SET published_version=@version,updated_at=now() WHERE id=@id", connection, transaction))
        {
            update.Parameters.AddWithValue("id", id);
            update.Parameters.AddWithValue("version", version);
            await update.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return await GetVersionAsync(id, version, ct);
    }

    private static async Task<IReadOnlyList<FlowScriptVersionSummary>> ListVersionsAsync(Guid id, DbConnection connection, CancellationToken ct)
    {
        var versions = new List<FlowScriptVersionSummary>();
        await using var command = Sql.Command("""
            SELECT script_id,version,name,language,description,created_at FROM flow_script_versions
            WHERE script_id=@id ORDER BY version DESC
            """, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) versions.Add(ReadVersionSummary(reader));
        return versions;
    }

    private static FlowScriptSummary ReadSummary(DbDataReader reader, IReadOnlyList<FlowScriptVersionSummary> versions) =>
        new(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetFieldValue<DateTimeOffset>(6), versions);

    private static FlowScriptVersionSummary ReadVersionSummary(DbDataReader reader) =>
        new(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5));

    private static FlowScriptVersion ReadVersion(DbDataReader reader) =>
        new(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6));

    private static void AddDraft(DbCommand command, FlowScriptDraft draft)
    {
        command.Parameters.AddWithValue("name", draft.Name.Trim());
        command.Parameters.AddWithValue("language", draft.Language);
        command.Parameters.AddWithValue("description", draft.Description.Trim());
        command.Parameters.AddWithValue("code", draft.Code);
    }
}
