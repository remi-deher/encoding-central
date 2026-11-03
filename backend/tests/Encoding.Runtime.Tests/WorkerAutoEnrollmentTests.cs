using Encoding.WorkerAgent.Services;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class WorkerAutoEnrollmentTests
{
    [Fact]
    public void ConfiguredCentralIsSelectedWhenOtherCentralsAreDiscovered()
    {
        var configured = new DiscoveredCentral(Guid.NewGuid(), "Configured", "http://server:8080/", DateTimeOffset.UtcNow, "configured");
        var discovered = new DiscoveredCentral(Guid.NewGuid(), "Discovered", "http://other:8080/", DateTimeOffset.UtcNow);

        Assert.Same(configured, WorkerAutoEnrollmentService.GetAutomaticEnrollmentTarget([discovered, configured], hasConfiguredApiBaseUrl: true));
    }

    [Fact]
    public void MultipleDiscoveredCentralsWithoutConfigurationAreNotSelected()
    {
        var centrals = new[]
        {
            new DiscoveredCentral(Guid.NewGuid(), "First", "http://first:8080/", DateTimeOffset.UtcNow),
            new DiscoveredCentral(Guid.NewGuid(), "Second", "http://second:8080/", DateTimeOffset.UtcNow)
        };

        Assert.Null(WorkerAutoEnrollmentService.GetAutomaticEnrollmentTarget(centrals, hasConfiguredApiBaseUrl: false));
    }

    [Fact]
    public void SingleDiscoveredCentralIsSelectedWithoutConfiguration()
    {
        var central = new DiscoveredCentral(Guid.NewGuid(), "Discovered", "http://server:8080/", DateTimeOffset.UtcNow);

        Assert.Same(central, WorkerAutoEnrollmentService.GetAutomaticEnrollmentTarget([central], hasConfiguredApiBaseUrl: false));
    }

    [Fact]
    public void ConfiguredCentralIsNotReplacedWhileItIsUnavailable()
    {
        var discovered = new DiscoveredCentral(Guid.NewGuid(), "Discovered", "http://other:8080/", DateTimeOffset.UtcNow);

        Assert.Null(WorkerAutoEnrollmentService.GetAutomaticEnrollmentTarget([discovered], hasConfiguredApiBaseUrl: true));
    }
}
