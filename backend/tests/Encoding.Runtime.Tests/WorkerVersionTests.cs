using Encoding.Contracts.Workers;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Un worker ne fonctionne qu'avec la version exacte du central : toute différence, ou une version absente, est signalée et jamais acceptée en silence.</summary>
public sealed class WorkerVersionTests
{
    [Fact]
    public void SameVersionAsTheCentralIsAccepted() => Assert.Null(AppVersion.MismatchReason(AppVersion.Current));

    [Fact]
    public void DifferentVersionIsRefusedWhateverTheDirection()
    {
        foreach (var other in new[] { "0.0.1", "99.0.0", AppVersion.Current + "-x" })
        {
            var reason = AppVersion.MismatchReason(other, "1.2.3");
            Assert.NotNull(reason);
            Assert.Contains("mettez le worker à jour", reason, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void MissingVersionIsRefused(string? version) => Assert.NotNull(AppVersion.MismatchReason(version, "1.2.3"));

    [Fact]
    public void ComparisonIsExact() => Assert.NotNull(AppVersion.MismatchReason("1.2.3+abc", "1.2.3"));
}
