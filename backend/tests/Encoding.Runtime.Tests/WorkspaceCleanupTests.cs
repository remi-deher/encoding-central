using Encoding.Api.Infrastructure.Operations;
using Encoding.NodeAgent.Runtime;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class WorkspaceCleanupTests
{
    private static WorkspaceRetentionOptions Defaults() => WorkspaceRetentionOptions.FromConfiguration(new ConfigurationBuilder().Build());

    [Fact]
    public void DefaultsPurgeFinishedExecutionsImmediately()
    {
        var options = Defaults();
        Assert.True(options.Enabled);
        Assert.True(options.ShouldDelete("succeeded", TimeSpan.Zero));
        Assert.True(options.ShouldDelete("cancelled", TimeSpan.Zero));
        Assert.True(options.ShouldDelete("failed", TimeSpan.Zero));
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    [InlineData("cancelling")]
    [InlineData("deferred")]
    public void UnfinishedExecutionsAreNeverDeleted(string state) =>
        Assert.False(Defaults().ShouldDelete(state, TimeSpan.FromDays(365)));

    [Fact]
    public void UnknownExecutionsWaitForTheLongestDelay()
    {
        var options = Defaults();
        Assert.False(options.ShouldDelete(null, TimeSpan.FromHours(71)));
        Assert.True(options.ShouldDelete(null, TimeSpan.FromHours(72)));
    }

    [Fact]
    public void ConfigurationOverridesDelaysAndCanDisableCleanup()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Node:Workspace:CompletedRetentionHours"] = "0.5",
            ["Node:Workspace:FailedRetentionHours"] = "6",
            ["Node:Workspace:CleanupEnabled"] = "false"
        }).Build();
        var options = WorkspaceRetentionOptions.FromConfiguration(configuration);
        Assert.False(options.Enabled);
        Assert.True(options.ShouldDelete("succeeded", TimeSpan.FromMinutes(30)));
        Assert.False(options.ShouldDelete("failed", TimeSpan.FromHours(5)));
    }

    [Fact]
    public void ArtifactRetentionIsEnabledByDefaultAndPurgesWithoutDelay()
    {
        var options = new ArtifactRetentionOptions();
        Assert.True(options.Enabled);
        Assert.Equal(0, options.Days);
        Assert.Equal(0, options.FailedDays);
        options.Validate();
    }
}
