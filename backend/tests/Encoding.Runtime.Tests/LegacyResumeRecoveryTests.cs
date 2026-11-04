using Xunit;

namespace Encoding.Runtime.Tests;

[Collection(RuntimeCollection.Name)]
public sealed class LegacyResumeRecoveryTests(RuntimeFixture database)
{
    [Theory]
    [InlineData("queued", false, false, false)]
    [InlineData("queued", true, false, false)]
    [InlineData("running", false, false, false)]
    [InlineData("running", true, false, false)]
    [InlineData("queued", false, true, false)]
    [InlineData("running", true, true, false)]
    [InlineData("queued", false, false, true)]
    [InlineData("cancelling", false, false, false)]
    public Task PersistedResumptionIsFencedAndClosedAfterUpgrade(string state, bool finiteReservation, bool uncertain, bool retryPolicy) =>
        database.Scenarios!.LegacyPendingResumptionIsClosedAsync(state, finiteReservation, uncertain, retryPolicy);
}
