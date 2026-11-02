using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Plugins;

namespace Encoding.Api.Flows;

public sealed class FlowTemplateRepository(
    DbDataSource dataSource,
    FlowComponentRegistry registry)
{
    private static readonly FlowTemplateConfiguration EmptyConfiguration = new([]);

    public async Task<IReadOnlyList<FlowTemplateSummary>> ListAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id,name,description,media_type,source_flow_id,source_version,source_flow_name,
                   jsonb_array_length(definition->'nodes'),created_at,revision,configuration
            FROM flow_templates ORDER BY lower(name),id
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var templates = new List<FlowTemplateSummary>();
        while (await reader.ReadAsync(ct))
        {
            var summary = ReadSummary(reader);
            var configuration = JsonSerializer.Deserialize<FlowTemplateConfiguration>(reader.GetString(10), FlowRepository.JsonOptions)
                ?? EmptyConfiguration;
            templates.Add(summary with { ConfigurableFieldCount = CountConfigurableFields(configuration) });
        }
        return templates;
    }

    public async Task<FlowTemplateDetails?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id,name,description,media_type,source_flow_id,source_version,source_flow_name,
                   jsonb_array_length(definition->'nodes'),created_at,revision,definition::text,configuration
            FROM flow_templates WHERE id=@id
            """);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var summary = ReadSummary(reader);
        var definition = JsonSerializer.Deserialize<FlowDefinition>(reader.GetString(10), FlowRepository.JsonOptions)
            ?? throw new InvalidDataException("La définition du modèle est invalide.");
        var configuration = JsonSerializer.Deserialize<FlowTemplateConfiguration>(reader.GetString(11), FlowRepository.JsonOptions)
            ?? EmptyConfiguration;
        return new(summary with { ConfigurableFieldCount = CountConfigurableFields(configuration) }, definition, configuration);
    }

    public string? ValidateConfiguration(FlowDefinition definition, FlowTemplateConfiguration? configuration)
    {
        if (configuration is not null && configuration.Nodes is null)
            return "La liste des blocs configurables est obligatoire.";
        var seenNodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var configurableNode in configuration?.Nodes ?? [])
        {
            if (configurableNode is null || string.IsNullOrWhiteSpace(configurableNode.NodeId))
                return "Chaque bloc configurable doit avoir un identifiant.";
            if (configurableNode.FieldIds is null)
                return $"La liste des paramètres du bloc « {configurableNode.NodeId} » est obligatoire.";
            if (!seenNodes.Add(configurableNode.NodeId)) return "Un même bloc ne peut apparaître qu’une fois dans la configuration du modèle.";
            var node = definition.Nodes.FirstOrDefault(candidate => candidate.Id == configurableNode.NodeId);
            if (node is null) return $"Le bloc « {configurableNode.NodeId} » n’existe pas dans le flow source.";
            var descriptor = registry.Resolve(node.Component);
            if (descriptor is null) return $"Le composant du bloc « {configurableNode.NodeId} » n’est plus disponible.";
            var seenFields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fieldId in configurableNode.FieldIds)
            {
                if (string.IsNullOrWhiteSpace(fieldId)) return $"Un paramètre du bloc « {configurableNode.NodeId} » est invalide.";
                if (!seenFields.Add(fieldId)) return $"Le paramètre « {fieldId} » est répété dans le bloc « {configurableNode.NodeId} ».";
                var field = descriptor.ConfigurationSchema.Fields.FirstOrDefault(candidate => candidate.Id == fieldId);
                if (field is null) return $"Le paramètre « {fieldId} » n’existe pas dans le bloc « {configurableNode.NodeId} ».";
                if (!IsTemplateConfigurable(field)) return $"Le paramètre « {fieldId} » dépend de l’environnement local et ne peut pas être exposé par un modèle.";
            }
        }
        return null;
    }

    private static int CountConfigurableFields(FlowTemplateConfiguration configuration) =>
        configuration.Nodes?.Sum(node => node?.FieldIds?.Count ?? 0) ?? 0;

    public static bool IsTemplateConfigurable(Encoding.Contracts.Plugins.FlowConfigurationField field)
    {
        var tokens = FieldTokens(field);
        return field.Type is not ("secret" or "script-reference" or "condition-tree" or "record-list" or "path" or "file-path" or "directory-path" or "file" or "directory")
            && !string.Equals(field.Editor, "artifact", StringComparison.OrdinalIgnoreCase)
            && !HasLocalPath(tokens);
    }

    public FlowDefinition RemoveLocalConfiguration(FlowDefinition definition)
    {
        var nodes = definition.Nodes.Select(node =>
        {
            var descriptor = registry.Resolve(node.Component);
            if (descriptor is null) return node;
            var values = node.Configuration.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            RemoveLocalValues(values, descriptor.ConfigurationSchema.Fields);
            return node with { Configuration = values };
        }).ToArray();
        return definition with { Nodes = nodes };
    }

    private static void RemoveLocalValues(
        Dictionary<string, JsonElement> values,
        IReadOnlyList<FlowConfigurationField> fields)
    {
        foreach (var field in fields)
        {
            if (!values.TryGetValue(field.Id, out var value)) continue;
            if (IsLocalValue(field))
            {
                values.Remove(field.Id);
                continue;
            }
            if (field.Type != "record-list" || field.ItemFields is not { Count: > 0 } itemFields
                || value.ValueKind != JsonValueKind.Array) continue;

            var items = new List<Dictionary<string, JsonElement>>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var itemValues = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.GetRawText(), FlowRepository.JsonOptions) ?? [];
                RemoveLocalValues(itemValues, itemFields);
                items.Add(itemValues);
            }
            values[field.Id] = JsonSerializer.SerializeToElement(items, FlowRepository.JsonOptions);
        }
    }

    private static bool IsLocalValue(FlowConfigurationField field)
    {
        var tokens = FieldTokens(field);
        return field.Type is "secret" or "path" or "file-path" or "directory-path" or "file" or "directory"
            || string.Equals(field.Editor, "artifact", StringComparison.OrdinalIgnoreCase)
            || HasLocalPath(tokens);
    }

    private static bool HasLocalPath(HashSet<string> tokens) =>
        new[] { "path", "paths", "chemin", "chemins", "directory", "folder", "dossier" }
            .Any(tokens.Contains)
        || (tokens.Contains("file") || tokens.Contains("fichier"))
            && new[] { "source", "input", "output", "local", "current", "original", "temp", "temporary", "artifact" }
                .Any(tokens.Contains);

    private static HashSet<string> FieldTokens(FlowConfigurationField field)
    {
        var expanded = Regex.Replace(field.Id + " " + field.Label, "([a-z0-9])([A-Z])", "$1 $2");
        return Regex.Split(expanded.ToLowerInvariant(), "[^a-z0-9]+")
            .Where(token => token.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    public async Task<FlowTemplateDetails?> CreateFromFlowAsync(
        Guid templateId,
        CreateFlowTemplateFromFlowRequest request,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var configuration = request.Configuration ?? EmptyConfiguration;
        await using var insert = Sql.Command("""
            INSERT INTO flow_templates(
                id,name,description,source_flow_id,source_version,source_flow_name,media_type,definition,configuration,revision)
            SELECT @templateId,@name,@description,@sourceFlowId,@sourceVersion,
                   definition->>'name',definition->>'mediaType',definition,@configuration,1
            FROM flow_versions
            WHERE flow_id=@sourceFlowId AND version=@sourceVersion
            RETURNING id
            """, connection, transaction);
        insert.Parameters.AddWithValue("templateId", templateId);
        insert.Parameters.AddWithValue("name", request.Name.Trim());
        insert.Parameters.AddWithValue("description", request.Description.Trim());
        insert.Parameters.AddWithValue("sourceFlowId", request.SourceFlowId);
        insert.Parameters.AddWithValue("sourceVersion", request.SourceVersion);
        insert.Parameters.AddWithValue("configuration", SqlKind.Json, JsonSerializer.Serialize(configuration, FlowRepository.JsonOptions));
        if (await insert.ExecuteScalarAsync(ct) is null) return null;
        await transaction.CommitAsync(ct);
        return await GetAsync(templateId, ct);
    }

    public async Task<FlowTemplateDetails?> UpdateAsync(Guid id, UpdateFlowTemplateRequest request, CancellationToken ct)
    {
        var configuration = JsonSerializer.Serialize(request.Configuration, FlowRepository.JsonOptions);
        var definition = JsonSerializer.Serialize(request.Definition, FlowRepository.JsonOptions);
        await using var update = dataSource.CreateCommand("""
            UPDATE flow_templates
            SET name=@name,description=@description,media_type=@mediaType,definition=@definition,
                configuration=@configuration,revision=revision+1
            WHERE id=@id AND revision=@expectedRevision
            RETURNING id
            """);
        update.Parameters.AddWithValue("id", id);
        update.Parameters.AddWithValue("name", request.Name.Trim());
        update.Parameters.AddWithValue("description", request.Description.Trim());
        update.Parameters.AddWithValue("mediaType", request.Definition.MediaType);
        update.Parameters.AddWithValue("definition", SqlKind.Json, definition);
        update.Parameters.AddWithValue("configuration", SqlKind.Json, configuration);
        update.Parameters.AddWithValue("expectedRevision", request.ExpectedRevision);
        if (await update.ExecuteScalarAsync(ct) is null) return null;
        return await GetAsync(id, ct);
    }

    private static FlowTemplateSummary ReadSummary(DbDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetGuid(4), reader.GetInt32(5), reader.GetString(6), reader.GetInt32(7),
        reader.GetFieldValue<DateTimeOffset>(8), reader.GetInt32(9), 0);
}
