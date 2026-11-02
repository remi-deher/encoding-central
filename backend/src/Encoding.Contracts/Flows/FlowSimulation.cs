using System.Text.Json;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows.Conditions;

namespace Encoding.Contracts.Flows;

public sealed record FlowSimulationFile(string Name, JsonElement Metadata,
    IReadOnlyDictionary<string, JsonElement> Variables, Guid SourceNodeId, Guid WorkerNodeId, Guid? LibraryId = null);
public sealed record FlowSimulationAssumption(string OutputId, JsonElement? Metadata = null,
    IReadOnlyDictionary<string, JsonElement>? Variables = null, bool Modified = false, string? Extension = null,
    Guid? WorkerNodeId = null);
public sealed record FlowSimulationRequest(JsonElement Definition, FlowSimulationFile File,
    IReadOnlyDictionary<string, FlowSimulationAssumption>? Assumptions = null);
public sealed record FlowSimulationStep(string NodeId, string Name, string? OutputId, string Detail,
    bool Assumed, RuntimeContext Context, Guid WorkerNodeId, IReadOnlyList<ConditionObservation>? Conditions = null);
public sealed record FlowSimulationResult(string Status, string Detail, IReadOnlyList<FlowSimulationStep> Steps,
    string? WaitingNodeId = null, string? TerminalResult = null, IReadOnlyList<FlowValidationIssue>? Issues = null);

/// <summary>The variable block and simulation use exactly the same JSON-or-text interpretation.</summary>
public static class FlowVariableValue
{
    public static JsonElement Parse(string value)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(value); }
        catch (JsonException) { return JsonSerializer.SerializeToElement(value); }
    }
}
