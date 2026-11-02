namespace Encoding.Contracts.Flows;

/// <summary>Sequential traversal of a validated graph; reconvergence is not a synchronization barrier.</summary>
public static class FlowPath
{
    public static string? NextNodeId(FlowDefinition flow, string nodeId, string? outputId) =>
        flow.Connections.SingleOrDefault(edge => edge.SourceNodeId == nodeId && edge.SourceOutputId == outputId)?.TargetNodeId;
}
