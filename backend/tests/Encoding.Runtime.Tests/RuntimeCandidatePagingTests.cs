using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class RuntimeCandidatePagingTests
{
    [Theory]
    [InlineData(100, false, false)]
    [InlineData(210, false, false)]
    [InlineData(100, true, false)]
    [InlineData(210, true, false)]
    [InlineData(210, false, true)]
    public async Task ClaimsPastIncompatiblePagesWithoutBypassingAccessOrCapacity(int blocked, bool missingAccess, bool concurrent)
    {
        var (registry, validator, eligibility) = Harness.Services();
        await using var fixture = await Harness.Fixture.CreateAsync("sqlite", registry, validator, eligibility);
        await fixture.ClaimPastIncompatiblePagesAsync(blocked, missingAccess, concurrent);
    }
}
