namespace Encoding.Contracts.Flows;

public sealed record FlowComponentPackSummary(
    Guid Id,
    string Name,
    string Description,
    string MediaType,
    Guid SourceFlowId,
    int SourceVersion,
    string SourceFlowName,
    int ComponentCount,
    DateTimeOffset CreatedAt);

public sealed record FlowComponentPackDetails(
    FlowComponentPackSummary Pack,
    FlowDefinition Definition,
    string InputNodeId,
    IReadOnlyList<FlowComponentPackOutput> Outputs);

public sealed record FlowComponentPackOutput(
    string Id,
    string Label,
    string SourceNodeId,
    string SourceOutputId);

public sealed record CreateFlowComponentPackRequest(
    Guid SourceFlowId,
    int SourceVersion,
    string Name,
    string Description);
