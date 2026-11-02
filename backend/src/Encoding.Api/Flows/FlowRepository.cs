using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using System.Text.Json;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Flows;

public sealed partial class FlowRepository(DbDataSource dataSource)
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<FlowSummary>> ListAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT f.id,COALESCE(d.definition,v.definition)->>'name',COALESCE(d.definition,v.definition)->>'mediaType',f.current_version,
                   jsonb_array_length(COALESCE(d.definition,v.definition)->'nodes'),CASE WHEN d.flow_id IS NOT NULL THEN COALESCE(db.status,'pending') ELSE COALESCE(b.status,'pending') END,
                   GREATEST(f.updated_at,COALESCE(d.updated_at,f.updated_at)),d.flow_id IS NOT NULL
            FROM flows f LEFT JOIN flow_versions v ON v.flow_id=f.id AND v.version=f.current_version
            LEFT JOIN flow_drafts d ON d.flow_id=f.id LEFT JOIN flow_draft_backups db ON db.flow_id=d.flow_id AND db.revision=d.revision
            LEFT JOIN flow_backup_jobs b ON b.flow_id=v.flow_id AND b.version=v.version
            WHERE f.deleted_at IS NULL AND (d.flow_id IS NOT NULL OR v.flow_id IS NOT NULL)
            ORDER BY GREATEST(f.updated_at,COALESCE(d.updated_at,f.updated_at)) DESC,f.id
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<FlowSummary>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetInt32(3),reader.GetInt32(4),
            reader.GetString(5),reader.GetFieldValue<DateTimeOffset>(6),reader.GetBoolean(7)));
        return result;
    }

    public async Task<FlowVersionResponse?> GetAsync(Guid id, int? version, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT v.definition::text,b.status,v.created_at FROM flows f
            JOIN flow_versions v ON v.flow_id=f.id AND v.version=COALESCE(@version,f.current_version)
            JOIN flow_backup_jobs b ON b.flow_id=v.flow_id AND b.version=v.version
            WHERE f.id=@id AND (@version IS NOT NULL OR f.deleted_at IS NULL)
            """);
        command.Parameters.AddWithValue("id",id);
        command.Parameters.AddWithValue("version",SqlKind.Integer,(object?)version ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new(JsonSerializer.Deserialize<FlowDefinition>(reader.GetString(0),JsonOptions)!,reader.GetString(1),reader.GetFieldValue<DateTimeOffset>(2)) : null;
    }

    public async Task<FlowDraftResponse?> DraftAsync(Guid id,CancellationToken ct)
    {
        await using var command=dataSource.CreateCommand("SELECT d.definition::text,d.revision,f.current_version,d.updated_at,b.status FROM flow_drafts d JOIN flows f ON f.id=d.flow_id JOIN flow_draft_backups b ON b.flow_id=d.flow_id AND b.revision=d.revision WHERE f.id=@id AND f.deleted_at IS NULL");
        command.Parameters.AddWithValue("id",id); await using var reader=await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(JsonSerializer.Deserialize<FlowDefinition>(reader.GetString(0),JsonOptions)!,reader.GetInt32(1),
            reader.GetInt32(2),reader.GetString(4),reader.GetFieldValue<DateTimeOffset>(3)) : null;
    }
    public async Task<FlowDraftResponse?> SaveDraftAsync(SaveFlowDraftRequest request,CancellationToken ct)
    {
        request=request with { Definition=request.Definition.RepairText() };
        await using var connection=await dataSource.OpenConnectionAsync(ct); await using var tx=await connection.BeginTransactionAsync(ct);
        await using var command=Sql.Command(
          "INSERT INTO flows(id) VALUES(@id) ON CONFLICT DO NOTHING; SELECT current_version FROM flows WHERE id=@id FOR UPDATE",connection,tx);
        command.Parameters.AddWithValue("id",request.Definition.Id);
        var published=(await command.ScalarInt32Async(ct))!.Value;
        await using var save=Sql.Command(
          "INSERT INTO flow_drafts(flow_id,revision,definition) SELECT @id,1,@document WHERE @expected=0 ON CONFLICT(flow_id) DO UPDATE SET definition=EXCLUDED.definition,revision=flow_drafts.revision+1,updated_at=clock_timestamp() WHERE flow_drafts.revision=@expected RETURNING revision",
              connection,tx);
        save.Parameters.AddWithValue("id",request.Definition.Id); save.Parameters.AddWithValue("expected",request.ExpectedRevision);
        save.Parameters.AddWithValue("document",SqlKind.Json,JsonSerializer.Serialize(request.Definition,JsonOptions));
        // Update cannot use an INSERT filtered out by expected>0; lock and inspect first.
        await using var existing=Sql.Command("SELECT revision FROM flow_drafts WHERE flow_id=@id",connection,tx);
        existing.Parameters.AddWithValue("id",request.Definition.Id);
        var revision=await existing.ExecuteScalarAsync(ct);
        if((revision is null && request.ExpectedRevision!=0) || (revision is not null and not DBNull && Convert.ToInt32(revision)!=request.ExpectedRevision)) return null;
        await using var highest=Sql.Command("SELECT COALESCE(max(revision),0)+1 FROM flow_draft_backups WHERE flow_id=@id",connection,tx);
        highest.Parameters.AddWithValue("id",request.Definition.Id);
        var nextRevision=(await highest.ScalarInt32Async(ct))!.Value;
        save.Parameters.AddWithValue("next",nextRevision);
        save.CommandText="INSERT INTO flow_drafts(flow_id,revision,definition) VALUES(@id,@next,@document) ON CONFLICT(flow_id) DO UPDATE SET definition=EXCLUDED.definition,revision=EXCLUDED.revision,updated_at=clock_timestamp() RETURNING revision,updated_at";
        await using var written=await save.ExecuteReaderAsync(ct); await written.ReadAsync(ct); var result=new FlowDraftResponse(request.Definition,
            written.GetInt32(0),published,"draft",written.GetFieldValue<DateTimeOffset>(1)); await written.DisposeAsync();
        await using(var backup=Sql.Command("INSERT INTO flow_draft_backups(flow_id,revision,definition) VALUES(@id,@revision,@document)",connection,tx)) {
            backup.Parameters.AddWithValue("id",request.Definition.Id); backup.Parameters.AddWithValue("revision",result.DraftRevision);
                backup.Parameters.AddWithValue("document",SqlKind.Json,JsonSerializer.Serialize(request.Definition,JsonOptions));
                await backup.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct); return result with { BackupStatus="pending" };
    }

    public async Task<IReadOnlyList<FlowDefinition>> VersionsAsync(Guid id, CancellationToken ct)
    {
        await using var command=dataSource.CreateCommand("SELECT definition::text FROM flow_versions WHERE flow_id=@id ORDER BY version");
        command.Parameters.AddWithValue("id",id);
        await using var reader=await command.ExecuteReaderAsync(ct);
        var list=new List<FlowDefinition>();
        while(await reader.ReadAsync(ct)) list.Add(JsonSerializer.Deserialize<FlowDefinition>(reader.GetString(0),JsonOptions)!);
        return list;
    }

    /// <summary>CAS publication and backup outbox share one transaction. Versions are append-only.</summary>
    public async Task<FlowVersionResponse?> SaveAsync(SaveFlowRequest request, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var version = await SaveCoreAsync(request, false, connection, tx, ct, skipUnchanged: true);
        if (version is null) return null;
        await tx.CommitAsync(ct);
        return await GetAsync(request.Definition.Id, version.Value, ct);
    }

    public async Task<bool> SaveManyAsync(IReadOnlyList<SaveFlowRequest> requests, bool restore, CancellationToken ct,IReadOnlyList<FlowDraftImport>? drafts=null)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        foreach(var id in requests.Select(r=>r.Definition.Id).Concat((drafts??[]).Select(d=>d.Definition.Id)).Distinct().Order()) {
            await using var guard=Sql.Command("INSERT INTO flows(id) VALUES(@id) ON CONFLICT DO NOTHING; SELECT id FROM flows WHERE id=@id FOR UPDATE",connection,tx);
            guard.Parameters.AddWithValue("id",id); await guard.ExecuteScalarAsync(ct);
        }
        foreach (var request in requests.OrderBy(r => r.Definition.Id).ThenBy(r => r.Definition.Version))
        {
            if (await SaveCoreAsync(request, restore, connection, tx, ct) is null) { await tx.RollbackAsync(ct); return false; }
        }
        foreach(var imported in drafts??[]) {
            var draft=imported.Definition;
            await using var existing=Sql.Command("SELECT definition=@document FROM flow_drafts WHERE flow_id=@id",connection,tx);
            existing.Parameters.AddWithValue("id",draft.Id); existing.Parameters.AddWithValue("document",SqlKind.Json,JsonSerializer.Serialize(draft,JsonOptions));
            if(await existing.ScalarNullableBoolAsync(ct) is {} same) { if(!same) return false; continue; }
            await using var historical=Sql.Command("SELECT definition=@document FROM flow_draft_backups WHERE flow_id=@id AND revision=@revision",connection,tx);
            historical.Parameters.AddWithValue("id",draft.Id); historical.Parameters.AddWithValue("revision",imported.Revision);
                historical.Parameters.AddWithValue("document",SqlKind.Json,JsonSerializer.Serialize(draft,JsonOptions));
            if(await historical.ScalarNullableBoolAsync(ct) is {} sameHistory && !sameHistory) return false;
            await using var save=Sql.Command("INSERT INTO flow_drafts(flow_id,revision,definition) VALUES(@id,@revision,@document); INSERT INTO flow_draft_backups(flow_id,revision,definition) VALUES(@id,@revision,@document) ON CONFLICT DO NOTHING",
                connection,tx);
            save.Parameters.AddWithValue("id",draft.Id); save.Parameters.AddWithValue("revision",imported.Revision); save.Parameters.AddWithValue("document",
                SqlKind.Json,JsonSerializer.Serialize(draft,JsonOptions));
            await save.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct); return true;
    }

    private static async Task<int?> SaveCoreAsync(SaveFlowRequest request, bool restore, DbConnection connection, DbTransaction tx, CancellationToken ct, bool skipUnchanged = false)
    {
        var definition = request.Definition.RepairText();
        request = request with { Definition = definition };
        await using (var insert = Sql.Command("INSERT INTO flows(id) VALUES(@id) ON CONFLICT DO NOTHING",connection,tx))
        { insert.Parameters.AddWithValue("id",definition.Id); await insert.ExecuteNonQueryAsync(ct); }
        int current;
        await using (var read = Sql.Command("SELECT current_version FROM flows WHERE id=@id FOR UPDATE",connection,tx))
        { read.Parameters.AddWithValue("id",definition.Id); current = (await read.ScalarInt32Async(ct))!.Value; }
        var json = JsonSerializer.Serialize(definition,JsonOptions);
        // An identical retry is acknowledged even after a newer publication. It never changes the head.
        await using (var repeat = Sql.Command("SELECT definition = @document FROM flow_versions WHERE flow_id=@id AND version=@version",connection,tx))
        {
            repeat.Parameters.AddWithValue("id",definition.Id); repeat.Parameters.AddWithValue("version",definition.Version);
            repeat.Parameters.AddWithValue("document",SqlKind.Json,json);
            var same = await repeat.ExecuteScalarAsync(ct);
            if (same is not null and not DBNull) return Convert.ToBoolean(same) ? definition.Version : null;
        }
        if(request.ExpectedDraftRevision is { } expectedDraft)
        {
            await using var draft=Sql.Command("SELECT revision FROM flow_drafts WHERE flow_id=@id",connection,tx);
            draft.Parameters.AddWithValue("id",definition.Id);
            if(await draft.ScalarInt32Async(ct) is not {} revision || revision!=expectedDraft) return null;
        }
        if (!restore && (current != request.ExpectedVersion || definition.Version != current + 1)) return null;
        if (skipUnchanged && current > 0)
        {
            await using var readLatest = Sql.Command("SELECT definition FROM flow_versions WHERE flow_id=@id AND version=@version", connection, tx);
            readLatest.Parameters.AddWithValue("id", definition.Id); readLatest.Parameters.AddWithValue("version", current);
            var existingJson = (string?)await readLatest.ExecuteScalarAsync(ct);
            if (existingJson is not null)
            {
                var existingDef = JsonSerializer.Deserialize<FlowDefinition>(existingJson, JsonOptions);
                if (existingDef is not null && JsonSerializer.Serialize(existingDef with { Version = current },
                    JsonOptions) == JsonSerializer.Serialize(definition with { Version = current }, JsonOptions))
                {
                    await ClearDraftAsync(request, connection, tx, ct);
                    return current;
                }
            }
        }
        await using var command = Sql.Command("""
            INSERT INTO flow_versions(flow_id,version,definition) VALUES(@id,@version,@document);
            INSERT INTO flow_backup_jobs(flow_id,version) VALUES(@id,@version);
            UPDATE flows SET updated_at=CASE WHEN current_version<@version THEN clock_timestamp() ELSE updated_at END,current_version=GREATEST(current_version,@version) WHERE id=@id;
            """,connection,tx);
        command.Parameters.AddWithValue("id",definition.Id); command.Parameters.AddWithValue("version",definition.Version);
        command.Parameters.AddWithValue("document",SqlKind.Json,json);
        await command.ExecuteNonQueryAsync(ct);
        await ClearDraftAsync(request, connection, tx, ct);
        return definition.Version;
    }

    private static async Task ClearDraftAsync(SaveFlowRequest request, DbConnection connection, DbTransaction tx, CancellationToken ct)
    {
        if(request.ExpectedDraftRevision is { } draftRevision)
        {
            await using var clear=Sql.Command("DELETE FROM flow_drafts WHERE flow_id=@id AND revision=@revision",connection,tx);
            clear.Parameters.AddWithValue("id",request.Definition.Id); clear.Parameters.AddWithValue("revision",draftRevision);
            await clear.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<(FlowDeleteResult Result, string? Reason)> DeleteFlowAsync(Guid id, CancellationToken ct, int? expectedVersion = null, bool detachLibraries = false)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await using var checkFlow = Sql.Command("SELECT current_version FROM flows WHERE id = @id AND deleted_at IS NULL", connection, tx);
        checkFlow.Parameters.AddWithValue("id", id);
        var currentVersionObj = await checkFlow.ExecuteScalarAsync(ct);
        if (currentVersionObj is null or DBNull)
            return (FlowDeleteResult.NotFound, null);
        var currentVersion = Convert.ToInt32(currentVersionObj);
        if (expectedVersion is { } expected && currentVersion != expected)
            return (FlowDeleteResult.Conflict, "La version du flow a changé. Actualisez le contexte avant de confirmer sa suppression.");

        await using var checkAssign = Sql.Command("SELECT count(*) FROM library_flow_assignments WHERE flow_id = @id", connection, tx);
        checkAssign.Parameters.AddWithValue("id", id);
        var assignCount = Convert.ToInt64(await checkAssign.ExecuteScalarAsync(ct));
        if (assignCount > 0 && !detachLibraries)
            return (FlowDeleteResult.Conflict, "Ce flow est actuellement assigné à une ou plusieurs bibliothèques.");

        await using var checkExec = Sql.Command("""
            SELECT count(*) FROM executions
            WHERE json_extract(flow_definition, '$.id') = @idStr
              AND state IN ('queued', 'running', 'active')
            """, connection, tx);
        checkExec.Parameters.AddWithValue("idStr", id.ToString("D"));
        var activeExecCount = Convert.ToInt64(await checkExec.ExecuteScalarAsync(ct));
        if (activeExecCount > 0)
            return (FlowDeleteResult.Conflict, "Des exécutions sont actuellement en cours pour ce flow.");

        if (assignCount > 0)
        {
            // Les exécutions embarquent leur définition : détacher les bibliothèques ne touche pas aux tâches déjà créées.
            await using var advance = Sql.Command("UPDATE libraries SET flow_assignment_revision = flow_assignment_revision + 1 WHERE id IN (SELECT library_id FROM library_flow_assignments WHERE flow_id = @id)",
                connection, tx);
            advance.Parameters.AddWithValue("id", id);
            await advance.ExecuteNonQueryAsync(ct);
            await using var detach = Sql.Command("DELETE FROM library_flow_assignments WHERE flow_id = @id", connection, tx);
            detach.Parameters.AddWithValue("id", id);
            await detach.ExecuteNonQueryAsync(ct);
        }

        await using var clearDraft = Sql.Command("DELETE FROM flow_drafts WHERE flow_id = @id", connection, tx);
        clearDraft.Parameters.AddWithValue("id", id);
        await clearDraft.ExecuteNonQueryAsync(ct);

        await using var softDelete = Sql.Command("UPDATE flows SET deleted_at = clock_timestamp(), updated_at = clock_timestamp() WHERE id = @id AND (@expectedVersion IS NULL OR current_version = @expectedVersion)",
            connection, tx);
        softDelete.Parameters.AddWithValue("id", id);
        softDelete.Parameters.AddWithValue("expectedVersion", (object?)expectedVersion ?? DBNull.Value);
        if (await softDelete.ExecuteNonQueryAsync(ct) != 1)
            return (FlowDeleteResult.Conflict, "La version du flow a changé. Actualisez le contexte avant de confirmer sa suppression.");

        await tx.CommitAsync(ct);
        return (FlowDeleteResult.Deleted, null);
    }

    public async Task<bool> DeleteDraftAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await using var clearDraft = Sql.Command("DELETE FROM flow_drafts WHERE flow_id = @id", connection, tx);
        clearDraft.Parameters.AddWithValue("id", id);
        var rows = await clearDraft.ExecuteNonQueryAsync(ct);
        if (rows == 0) return false;

        await using var checkVersions = Sql.Command("SELECT count(*) FROM flow_versions WHERE flow_id = @id", connection, tx);
        checkVersions.Parameters.AddWithValue("id", id);
        var versionCount = Convert.ToInt64(await checkVersions.ExecuteScalarAsync(ct));

        await using var checkFlow = Sql.Command("SELECT current_version FROM flows WHERE id = @id", connection, tx);
        checkFlow.Parameters.AddWithValue("id", id);
        var currentVersion = Convert.ToInt32(await checkFlow.ExecuteScalarAsync(ct) ?? 0);

        if (currentVersion == 0 && versionCount == 0)
        {
            await using var removeFlow = Sql.Command("UPDATE flows SET deleted_at = clock_timestamp(), updated_at = clock_timestamp() WHERE id = @id", connection, tx);
            removeFlow.Parameters.AddWithValue("id", id);
            await removeFlow.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<FlowDraftResponse?> CloneFlowAsync(Guid sourceId, string? newName, CancellationToken ct)
    {
        var draft = await DraftAsync(sourceId, ct);
        var version = draft is null ? await GetAsync(sourceId, null, ct) : null;
        if (draft is null && version is null) return null;

        var baseDefinition = draft?.Definition ?? version!.Definition;
        var newId = Guid.NewGuid();
        var name = string.IsNullOrWhiteSpace(newName) ? $"{baseDefinition.Name} (copie)" : newName.Trim();
        var cloned = baseDefinition with { Id = newId, Version = 1, Name = name };
        return await SaveDraftAsync(new SaveFlowDraftRequest(0, cloned), ct);
    }

    public async Task<FlowDraftResponse?> CreateFromTemplateAsync(
        Guid templateId,
        CreateFlowFromTemplateRequest request,
        FlowTemplateRepository templates,
        FlowDefinitionValidator validator,
        CancellationToken ct)
    {
        var template = await templates.GetAsync(templateId, ct);
        if (template is null) return null;
        if (request.TemplateRevision is { } requestedRevision && requestedRevision != template.Template.Revision)
            throw new FlowTemplateRevisionConflictException();

        var newId = Guid.NewGuid();
        var name = string.IsNullOrWhiteSpace(request.Name) ? template.Template.Name : request.Name.Trim();
        var definition = ApplyTemplateConfiguration(template, request.Configuration) with { Id = newId, Version = 1, Name = name };
        var validation = validator.Validate(definition);
        if (!validation.IsValid)
            throw new FlowTemplateSetupValidationException(validation.Issues);
        var portableDefinition = templates.RemoveLocalConfiguration(definition);
        return await SaveDraftAsync(new SaveFlowDraftRequest(0, portableDefinition), ct);
    }

    private static FlowDefinition ApplyTemplateConfiguration(
        FlowTemplateDetails template,
        IReadOnlyList<FlowTemplateNodeValues>? overrides)
    {
        if (overrides is null || overrides.Count == 0) return template.Definition;
        var policy = template.Configuration.Nodes.ToDictionary(node => node.NodeId, node => node.FieldIds, StringComparer.Ordinal);
        var seenNodes = new HashSet<string>(StringComparer.Ordinal);
        var nodes = template.Definition.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (var update in overrides)
        {
            if (update is null || string.IsNullOrWhiteSpace(update.NodeId) || update.Values is null)
                throw new FlowTemplateSetupConfigurationException("La configuration de bloc envoyée est invalide.");
            if (!seenNodes.Add(update.NodeId) || !policy.TryGetValue(update.NodeId, out var allowedFields))
                throw new FlowTemplateSetupConfigurationException($"Le bloc « {update.NodeId} » n’est pas configurable dans ce modèle.");
            if (!nodes.TryGetValue(update.NodeId, out var node))
                throw new FlowTemplateSetupConfigurationException($"Le bloc « {update.NodeId} » n’existe plus dans ce modèle.");
            if (update.Values.Keys.Any(field => !allowedFields.Contains(field, StringComparer.Ordinal)))
                throw new FlowTemplateSetupConfigurationException($"Un paramètre du bloc « {update.NodeId} » n’est pas configurable dans ce modèle.");
            nodes[update.NodeId] = node with { Configuration = node.Configuration.Concat(update.Values).GroupBy(pair => pair.Key,
                StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal) };
        }
        return template.Definition with { Nodes = template.Definition.Nodes.Select(node => nodes[node.Id]).ToArray() };
    }
}

public enum FlowDeleteResult
{
    Deleted,
    NotFound,
    Conflict
}

public sealed class FlowTemplateRevisionConflictException : Exception { }

public sealed class FlowTemplateSetupConfigurationException(string message) : Exception(message) { }

public sealed class FlowTemplateSetupValidationException(IReadOnlyList<FlowValidationIssue> issues)
    : Exception("La configuration du flow n’est pas valide.")
{
    public IReadOnlyList<FlowValidationIssue> Issues { get; } = issues;
}

