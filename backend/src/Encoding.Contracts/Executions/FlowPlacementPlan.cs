namespace Encoding.Contracts.Executions;

/// <summary>
/// Décision de placement d'un flow de bout en bout (ADR 0024) pour minimiser les transferts réseau
/// et attribuer dès l'entrée le worker optimal selon les capacités réelles exigées par le chemin du flow.
/// </summary>
public sealed record FlowPlacementPlan(
    string Strategy,
    Guid? AssignedWorkerId,
    Guid? AccessWorkerId,
    Guid? TranscodeWorkerId,
    string Reason,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> MissingCapabilities,
    DateTimeOffset PlannedAt,
    IReadOnlyDictionary<string, Guid>? WorkerAssignments = null)
{
    public const string StrategyDirectUnified = "direct_unified";
    public const string StrategyTranscodeRelay = "transcode_relay";
    public const string StrategySegmented = "segmented";
    public const string StrategyTransferWorker = "transfer_worker";
    public const string StrategyImpossible = "impossible";

    public bool IsExecutable => Strategy != StrategyImpossible && AssignedWorkerId.HasValue;
}
