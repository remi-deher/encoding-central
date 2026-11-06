using Encoding.Contracts.Executions;

namespace Encoding.Api.Executions;

/// <summary>Événement de télémétrie diffusé aux flux de l'interface (une seule des propriétés est remplie).</summary>
public sealed record ExecutionTelemetryEvent(Guid ExecutionId, Guid WorkerId,
    ExecutionTelemetryStep? Step = null, ExecutionTelemetryProgress? Progress = null, IReadOnlyList<ExecutionTelemetryLogLine>? Logs = null, bool Ended = false);

/// <summary>
/// Télémétrie temps réel en mémoire (ADR 0045). Alimentée par le WebSocket des workers, lue par les flux SSE de l'interface.
/// Rien n'y est durable : les journaux persistés restent ceux de <c>renew</c> et <c>steps/complete</c>.
/// </summary>
public interface IExecutionTelemetry
{
    /// <summary>Enregistre une trame déjà validée (bail et époque vérifiés par l'appelant).</summary>
    void Apply(Guid workerId, WorkerStepFrame frame);
    void Apply(Guid workerId, WorkerProgressFrame frame);
    void Apply(Guid workerId, WorkerLogFrame frame);

    /// <summary>Oublie une exécution terminée et prévient les abonnés (<c>Ended = true</c>).</summary>
    void End(Guid executionId);

    ExecutionTelemetrySnapshot? Snapshot(Guid executionId);
    IReadOnlyList<WorkerRunningExecution> Running(Guid workerId);

    /// <summary>Abonnement borné : les événements les plus anciens sont jetés si l'abonné est lent.</summary>
    IAsyncEnumerable<ExecutionTelemetryEvent> SubscribeExecution(Guid executionId, CancellationToken ct);
    IAsyncEnumerable<ExecutionTelemetryEvent> SubscribeWorker(Guid workerId, CancellationToken ct);
}
