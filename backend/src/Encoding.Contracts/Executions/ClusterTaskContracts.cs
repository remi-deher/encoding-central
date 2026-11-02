using System.Text.Json;
using Encoding.Contracts.Flows;
namespace Encoding.Contracts.Executions;

public sealed record ClusterTaskArtifact(string Sha256,long Bytes);
public sealed record ClusterTaskStep(string NodeId,Guid InvocationId,int Revision,DateTimeOffset StartedAt,RuntimeCheckpoint Checkpoint,bool Idempotent);
public sealed record ClusterTaskSnapshot(Guid ExecutionId,Guid LibraryId,string MediaPath,string MediaKey,string LibraryName,FlowDefinition Flow,int Attempt,
    int Revision,DateTimeOffset QueuedAt,long LeaseEpoch,
    string? SourceSha256,JsonElement? BeforeMetadata,IReadOnlyList<ClusterTaskStep> Steps,IReadOnlyList<ClusterTaskArtifact> Artifacts,IReadOnlyDictionary<string,string> Inputs,
    IReadOnlyDictionary<string,IReadOnlyDictionary<string,JsonElement>> PluginConfigurations,string MediaPolicy="keep");
public sealed record ClusterTaskEvent(int Version,Guid EventId,Guid ClusterId,Guid AuthorPeerId,string Kind,Guid ExecutionId,long Epoch,Guid PreviousEventId,
    Guid? TargetPeerId=null,Guid? TransferId=null,string? Snapshot=null,int Revision=0,Guid? SubjectPeerId=null,DateTimeOffset? RecordedAt=null,
        string? Reason=null,long Sequence=0,bool Reachable=false,Guid? NeutralizationId=null);
public sealed record ClusterTaskMoveRequest(Guid TargetPeerId);
public sealed record ClusterTaskNeutralizeRequest(bool ConfirmedStopped,string Reason);
public sealed record ClusterTaskForceSourceRetryRequest(bool ConfirmedStopped,string Reason);
public sealed record ClusterTaskResult(bool Success,string Detail,Guid? TransferId=null);
public sealed record ClusterTaskPeerRequest(int Version,Guid ClusterId,Guid Nonce,string Kind,Guid ExecutionId=default,string? Digest=null,string? Payload=null);
public sealed record ClusterTaskPeerReply(int Version,Guid ClusterId,Guid PeerId,Guid Nonce,bool Success,string? Payload=null,string? Detail=null);
public sealed record ClusterTaskObservation(Guid ObserverPeerId,Guid SubjectPeerId,long Sequence,DateTimeOffset ObservedAt,DateTimeOffset ExpiresAt,bool Reachable);
public sealed record ClusterTaskClusterStatus(bool Isolated,int WaitingSourceWrites,int PendingTransfers,IReadOnlyList<ClusterTaskObservation> Observations,
    Guid LocalPeerId=default,int PendingCheckpointPreparations=0,int PendingCheckpointCaches=0,bool Neutralized=false,bool CatchingUp=false,
    TaskCoordinationStatus? Coordination=null);

public sealed record ClusterTaskControlRequest(Guid ExecutionId,int? ExpectedRevision=null);

// Metadata only: never expose signed bodies or checkpoint contents in diagnostics.
public sealed record TaskCoordinationDiagnostic(string RegisterKey,string Disposition,string Reason,long Attempts,DateTimeOffset? NextAttemptAt);
public sealed record TaskCoordinationStatus(int Pending,int Parked,int Rejected,int Retained,IReadOnlyList<TaskCoordinationDiagnostic> Events);
