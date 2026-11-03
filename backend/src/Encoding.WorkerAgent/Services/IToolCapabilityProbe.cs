using Encoding.Contracts.Workers;

namespace Encoding.WorkerAgent.Services;

public interface IToolCapabilityProbe
{
    Task<IReadOnlyList<WorkerToolCapability>> DiscoverAsync(CancellationToken cancellationToken);
}
