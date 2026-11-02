using Encoding.Contracts.Executions;

namespace Encoding.NodeAgent.Runtime;

/// <summary>Décision de contrôle après une étape : continuer, céder/attendre, ou échouer.</summary>
internal enum StepOutcomeKind
{
    Continue,
    HandoffOrWait,
    Fail
}

/// <summary>
/// Résultat d'étape consommé par <see cref="RuntimeFlowExecution"/> : un seul applicateur
/// met à jour <see cref="FlowExecutionState"/> à partir de ces valeurs.
/// </summary>
internal readonly record struct StepOutcome(
    StepOutcomeKind Kind,
    string? Output = null,
    string? NextNodeId = null,
    string? Terminal = null,
    RuntimeHandoffSelector? Handoff = null,
    RuntimeWait? Wait = null,
    bool PlacementHandoff = false,
    string? FailDetail = null)
{
    public static StepOutcome Continue(string? output = null, string? nextNodeId = null, string? terminal = null) =>
        new(StepOutcomeKind.Continue, output, nextNodeId, terminal);

    public static StepOutcome HandoffOrWait(
        string? output,
        string? nextNodeId,
        string? terminal,
        RuntimeHandoffSelector? handoff,
        RuntimeWait? wait,
        bool placementHandoff = false) =>
        new(StepOutcomeKind.HandoffOrWait, output, nextNodeId, terminal, handoff, wait, placementHandoff);

    public static StepOutcome Fail(string detail) =>
        new(StepOutcomeKind.Fail, FailDetail: detail);

    public bool StopsLocalRun => Kind is StepOutcomeKind.HandoffOrWait or StepOutcomeKind.Fail;
}
