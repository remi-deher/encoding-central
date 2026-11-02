namespace Encoding.Contracts.Executions;

public sealed record TaskOwnershipStatus(Guid ExecutionId,Guid OwnerPeerId,long Epoch,string State);
