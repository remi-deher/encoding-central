using Encoding.Contracts.Nodes;

namespace Encoding.NodeAgent.Services;

public interface IToolCapabilityProbe
{
    Task<IReadOnlyList<NodeToolCapability>> DiscoverAsync(CancellationToken cancellationToken);
}
