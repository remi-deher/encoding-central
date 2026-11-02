namespace Encoding.Contracts.Executions;

public sealed record TaskAssignment(Guid ExecutionId,Guid LibraryId,string MediaKey,DateTimeOffset QueuedAt,Guid OwnerPeerId);
public sealed record TaskOwnershipPlan(int Version,Guid EventId,Guid ClusterId,Guid AuthorPeerId,IReadOnlyList<TaskAssignment> Assignments);
public sealed record SignedTaskOwnershipPlan(string Data,string Signature);
public sealed record TaskOwnershipStatus(Guid ExecutionId,Guid OwnerPeerId,long Epoch,string State);
