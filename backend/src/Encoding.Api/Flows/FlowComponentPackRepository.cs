using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using System.Text.Json;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Flows;

public sealed class FlowComponentPackRepository(DbDataSource dataSource, FlowComponentRegistry registry)
{
    public async Task<IReadOnlyList<FlowComponentPackSummary>> ListAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id,name,description,media_type,source_flow_id,source_version,source_flow_name,
                   jsonb_array_length(definition->'nodes') - 1,created_at
            FROM flow_component_packs ORDER BY lower(name),id
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<FlowComponentPackSummary>();
        while (await reader.ReadAsync(ct)) result.Add(ReadSummary(reader));
        return result;
    }

    public async Task<FlowComponentPackDetails?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id,name,description,media_type,source_flow_id,source_version,source_flow_name,
                   jsonb_array_length(definition->'nodes') - 1,created_at,definition::text,input_node_id,outputs::text
            FROM flow_component_packs WHERE id=@id
            """);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var summary = ReadSummary(reader);
        var definition = JsonSerializer.Deserialize<FlowDefinition>(reader.GetString(9), FlowRepository.JsonOptions)
            ?? throw new InvalidDataException("La définition du pack est invalide.");
        var inputNodeId = reader.GetString(10);
        var outputs = JsonSerializer.Deserialize<IReadOnlyList<FlowComponentPackOutput>>(
            reader.GetString(11), FlowRepository.JsonOptions) ?? [];
        return new(summary, definition, inputNodeId, outputs);
    }

    public async Task<FlowComponentPackDetails?> CreateFromFlowAsync(
        Guid id,
        CreateFlowComponentPackRequest request,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var source = Sql.Command(
            "SELECT definition::text FROM flow_versions WHERE flow_id=@flowId AND version=@version",
            connection, transaction);
        source.Parameters.AddWithValue("flowId", request.SourceFlowId);
        source.Parameters.AddWithValue("version", request.SourceVersion);
        var sourceJson = await source.ExecuteScalarAsync(ct) as string;
        if (sourceJson is null) return null;
        var definition = JsonSerializer.Deserialize<FlowDefinition>(sourceJson, FlowRepository.JsonOptions);
        if (definition is null) return null;
        var entry = definition.Nodes.FirstOrDefault(node => registry.Resolve(node.Component)?.Kind == "input");
        if (entry is null) return null;
        var inputNodeId = definition.Connections
            .FirstOrDefault(edge => edge.SourceNodeId == entry.Id && edge.SourceOutputId == "success")?.TargetNodeId;
        if (inputNodeId is null || definition.Nodes.Count <= 1) return null;
        var internalNodeIds = definition.Nodes.Where(node => node.Id != entry.Id).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var connectedOutputs = definition.Connections
            .Where(edge => internalNodeIds.Contains(edge.SourceNodeId) && internalNodeIds.Contains(edge.TargetNodeId))
            .Select(edge => (edge.SourceNodeId, edge.SourceOutputId)).ToHashSet();
        var outputs = definition.Nodes.Where(node => internalNodeIds.Contains(node.Id))
            .SelectMany(node => registry.Resolve(node.Component)?.Outputs
                .Where(output => !connectedOutputs.Contains((node.Id, output.Id)))
                .Select(output => new FlowComponentPackOutput(node.Id + ":" + output.Id, output.Label, node.Id, output.Id)) ?? [])
            .ToArray();
        await using var insert = Sql.Command("""
            INSERT INTO flow_component_packs(
                id,name,description,source_flow_id,source_version,source_flow_name,media_type,input_node_id,definition,outputs)
            VALUES(@id,@name,@description,@sourceFlowId,@sourceVersion,@sourceFlowName,@mediaType,@inputNodeId,@definition,@outputs)
            RETURNING id
            """, connection, transaction);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("name", request.Name.Trim());
        insert.Parameters.AddWithValue("description", request.Description.Trim());
        insert.Parameters.AddWithValue("sourceFlowId", request.SourceFlowId);
        insert.Parameters.AddWithValue("sourceVersion", request.SourceVersion);
        insert.Parameters.AddWithValue("sourceFlowName", definition.Name);
        insert.Parameters.AddWithValue("mediaType", definition.MediaType);
        insert.Parameters.AddWithValue("inputNodeId", inputNodeId);
        insert.Parameters.AddWithValue("definition", SqlKind.Json, sourceJson);
        insert.Parameters.AddWithValue("outputs", SqlKind.Json, JsonSerializer.Serialize(outputs, FlowRepository.JsonOptions));
        if (await insert.ExecuteScalarAsync(ct) is null) return null;
        await transaction.CommitAsync(ct);
        return await GetAsync(id, ct);
    }

    private static FlowComponentPackSummary ReadSummary(DbDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetGuid(4), reader.GetInt32(5), reader.GetString(6), reader.GetInt32(7),
        reader.GetFieldValue<DateTimeOffset>(8));
}
