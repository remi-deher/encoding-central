using Encoding.Contracts.Executions;
using Encoding.Contracts.Nodes;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>
/// Known <see cref="ClusterTaskEvent.Version"/> / peer envelope versions are accepted;
/// the next major (N+1) is rejected, matching TaskCheckpointRepository verification.
/// </summary>
public sealed class ClusterTaskMessageVersionTests
{
    [Fact]
    public void CurrentEventVersionIsSupported() =>
        Assert.True(ClusterTaskMessageVersions.IsSupported(ClusterTaskMessageVersions.Current));

    [Fact]
    public void SuccessorEventVersionIsRejected()
    {
        Assert.False(ClusterTaskMessageVersions.IsSupported(ClusterTaskMessageVersions.Current + 1));
        Assert.True(ClusterTaskMessageVersions.IsUnsupportedSuccessor(ClusterTaskMessageVersions.Current + 1));
    }

    [Fact]
    public void OlderEventVersionIsRejected() =>
        Assert.False(ClusterTaskMessageVersions.IsSupported(ClusterTaskMessageVersions.Current - 1));

    [Fact]
    public void ClusterProtocolRejectsSuccessorTheSameWay()
    {
        Assert.Null(ClusterProtocol.IncompatibilityReason(ClusterProtocol.Current));
        Assert.NotNull(ClusterProtocol.IncompatibilityReason(ClusterProtocol.Current + 1));
    }

    [Fact]
    public void ClusterTaskEventWireVersionMatchesConstant()
    {
        var evt = new ClusterTaskEvent(
            ClusterTaskMessageVersions.Current,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "checkpoint",
            Guid.NewGuid(),
            1,
            Guid.Empty);
        Assert.True(ClusterTaskMessageVersions.IsSupported(evt.Version));
        Assert.False(ClusterTaskMessageVersions.IsSupported(evt.Version + 1));
    }
}
