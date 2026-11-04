namespace Encoding.Contracts.Executions;

// Télémétrie temps réel worker → central (ADR 0045). Le WebSocket ne porte que de la télémétrie
// « au mieux » : bail, étapes, fin et journaux durables restent sur les POST transactionnels.
// Trames JSON texte, camelCase, discriminant "type".

/// <summary>Types de trames échangées sur <c>GET /api/v1/runtime/workers/{workerId}/socket</c>.</summary>
public static class WorkerSocketFrameTypes
{
    // worker → central
    public const string Hello = "hello";
    public const string Step = "step";
    public const string Progress = "progress";
    public const string Log = "log";
    public const string Pong = "pong";
    // central → worker
    public const string Ready = "ready";
    public const string Wake = "wake";
    public const string Ping = "ping";
}

public static class WorkerStepStates
{
    public const string Started = "started";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

/// <summary>Enveloppe minimale pour lire le discriminant avant de désérialiser la trame complète.</summary>
public sealed record WorkerSocketEnvelope(string Type);

public sealed record WorkerSocketHello(string Type, string? AgentVersion);

/// <summary>Une étape (nœud du flow) démarre ou se termine. Informatif : la vérité reste <c>steps/start</c> et <c>steps/complete</c>.</summary>
public sealed record WorkerStepFrame(string Type, Guid ExecutionId, long Epoch, string NodeId, string State, DateTimeOffset At);

/// <summary>Progression d'une commande. Le worker n'en envoie pas plus de 2 par seconde et par commande.</summary>
public sealed record WorkerProgressFrame(string Type, Guid ExecutionId, long Epoch, string NodeId, Guid CommandId,
    double? Percent, long? BytesProcessed, long ElapsedMs, DateTimeOffset At);

/// <summary>Lignes de journal d'une commande, regroupées (250 ms ou 64 lignes). <c>Seq</c> croît par commande pour détecter les trous.</summary>
public sealed record WorkerLogFrame(string Type, Guid ExecutionId, long Epoch, string NodeId, Guid CommandId,
    string Stream, long Seq, IReadOnlyList<string> Lines, DateTimeOffset At);

/// <summary>Trame central → worker sans charge (<c>ready</c>, <c>wake</c>, <c>ping</c>).</summary>
public sealed record CentralSocketSignal(string Type);

// Côté interface : flux SSE alimentés par la télémétrie (ADR 0045).

public sealed record ExecutionTelemetryStep(string NodeId, string State, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);

public sealed record ExecutionTelemetryProgress(string NodeId, Guid CommandId, double? Percent, long? BytesProcessed, long ElapsedMs, DateTimeOffset At);

public sealed record ExecutionTelemetryLogLine(string NodeId, Guid CommandId, string Stream, long Seq, string Text, DateTimeOffset At);

/// <summary>État en mémoire d'une exécution en cours, envoyé en <c>snapshot</c> à l'ouverture du flux.</summary>
public sealed record ExecutionTelemetrySnapshot(Guid ExecutionId, Guid WorkerId, DateTimeOffset StartedAt, string? CurrentNodeId,
    IReadOnlyList<ExecutionTelemetryStep> Steps, ExecutionTelemetryProgress? Progress, IReadOnlyList<ExecutionTelemetryLogLine> Logs);

/// <summary>Exécution en cours sur un worker, pour la page du worker.</summary>
public sealed record WorkerRunningExecution(Guid ExecutionId, DateTimeOffset StartedAt, string? CurrentNodeId, ExecutionTelemetryProgress? Progress);
