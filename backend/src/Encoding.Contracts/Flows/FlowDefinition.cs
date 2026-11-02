using System.Text.Json;

namespace Encoding.Contracts.Flows;

/// <summary>Portable definition. FormatVersion and Version evolve independently.</summary>
public sealed record FlowDefinition(
    int FormatVersion,
    Guid Id,
    int Version,
    string Name,
    string MediaType,
    IReadOnlyList<FlowNodeDefinition> Nodes,
    IReadOnlyList<FlowConnectionDefinition> Connections,
    IReadOnlyList<FlowComponentPackInstance>? ComponentPacks = null);

/// <summary>Visual grouping metadata for a flattened, executable pack instance.</summary>
public sealed record FlowComponentPackInstance(
    string Id,
    Guid PackId,
    int PackVersion,
    string Name,
    string InputNodeId,
    IReadOnlyList<string> NodeIds,
    IReadOnlyList<FlowComponentPackOutputBinding> Outputs);

public sealed record FlowComponentPackOutputBinding(
    string Id,
    string Label,
    string SourceNodeId,
    string SourceOutputId);

/// <summary>References an exact installed component; never embeds its implementation.</summary>
public sealed record FlowComponentReference(
    string PluginId,
    string PluginVersion,
    string ComponentId,
    string ComponentVersion,
    int ConfigurationSchemaVersion);

public sealed record FlowNodeDefinition(
    string Id,
    string? Name,
    FlowComponentReference Component,
    IReadOnlyDictionary<string, JsonElement> Configuration);

public sealed record FlowConnectionDefinition(
    string Id,
    string SourceNodeId,
    string SourceOutputId,
    string TargetNodeId);

public sealed record FlowValidationIssue(
    string Code,
    string Path,
    string Message);

public sealed record FlowValidationResult(IReadOnlyList<FlowValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}
