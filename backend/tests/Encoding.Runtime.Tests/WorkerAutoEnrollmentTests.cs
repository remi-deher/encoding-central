using Encoding.NodeAgent.Services;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class WorkerAutoEnrollmentTests
{
    [Fact]
    public void ConfiguredClusterIsSelectedWhenOtherClustersAreDiscovered()
    {
        var configured = new DiscoveredCentral(Guid.NewGuid(), "Configured", "http://server:8080/", DateTimeOffset.UtcNow, "configured");
        var discovered = new DiscoveredCentral(Guid.NewGuid(), "Discovered", "http://other:8080/", DateTimeOffset.UtcNow);

        Assert.Same(configured, WorkerAutoEnrollmentService.GetAutomaticEnrollmentTarget([discovered, configured], hasConfiguredApiBaseUrl: true));
    }

    [Fact]
    public void MultipleDiscoveredClustersWithoutConfigurationAreNotSelected()
    {
        var clusters = new[]
        {
            new DiscoveredCentral(Guid.NewGuid(), "First", "http://first:8080/", DateTimeOffset.UtcNow),
            new DiscoveredCentral(Guid.NewGuid(), "Second", "http://second:8080/", DateTimeOffset.UtcNow)
        };

        Assert.Null(WorkerAutoEnrollmentService.GetAutomaticEnrollmentTarget(clusters, hasConfiguredApiBaseUrl: false));
    }

    [Fact]
    public void SingleDiscoveredClusterIsSelectedWithoutConfiguration()
    {
        var cluster = new DiscoveredCentral(Guid.NewGuid(), "Discovered", "http://server:8080/", DateTimeOffset.UtcNow);

        Assert.Same(cluster, WorkerAutoEnrollmentService.GetAutomaticEnrollmentTarget([cluster], hasConfiguredApiBaseUrl: false));
    }

    [Fact]
    public void ConfiguredClusterIsNotReplacedWhileItIsUnavailable()
    {
        var discovered = new DiscoveredCentral(Guid.NewGuid(), "Discovered", "http://other:8080/", DateTimeOffset.UtcNow);

        Assert.Null(WorkerAutoEnrollmentService.GetAutomaticEnrollmentTarget([discovered], hasConfiguredApiBaseUrl: true));
    }
}
