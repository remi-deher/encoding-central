using Encoding.Contracts.Nodes;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Compatibilité de version entre nœuds : un nœud trop ancien ou plus récent que le cluster est signalé, jamais accepté en silence.</summary>
public sealed class ClusterProtocolTests
{
    [Fact]
    public void CurrentAndMinimumVersionsAreCoherent() => Assert.True(ClusterProtocol.Minimum <= ClusterProtocol.Current);

    [Theory]
    [InlineData(ClusterProtocol.Minimum)]
    [InlineData(ClusterProtocol.Current)]
    public void SupportedVersionsAreAccepted(int version) => Assert.Null(ClusterProtocol.IncompatibilityReason(version));

    [Fact]
    public void UndeclaredVersionIsTreatedAsProtocolZero() =>
        Assert.Equal(ClusterProtocol.IncompatibilityReason(0), ClusterProtocol.IncompatibilityReason(null));

    [Fact]
    public void NewerNodeIsRefusedWithAnUpgradeServersMessage()
    {
        var reason = ClusterProtocol.IncompatibilityReason(ClusterProtocol.Current + 1);
        Assert.NotNull(reason);
        Assert.Contains("plus récent", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OlderNodeIsRefusedWithAnUpgradeNodeMessage()
    {
        var reason = ClusterProtocol.IncompatibilityReason(ClusterProtocol.Minimum - 1);
        Assert.NotNull(reason);
        Assert.Contains("mise à jour du nœud", reason, StringComparison.Ordinal);
    }

}
