using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Encoding.Runtime.Tests;

public sealed class CoordinationMeasurementTests(ITestOutputHelper output)
{
    [CoordinationMeasurementTheory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public async Task MeasuresActualClaimsRenewalsAndIncrementalCapture(bool concurrentCapture, int repetition)
    {
        var (registry, validator, eligibility) = Harness.Services();
        await using var fixture = await Harness.Fixture.CreateAsync("sqlite", registry, validator, eligibility);
        var measurement = await fixture.MeasureCoordinationAsync(concurrentCapture);
        output.WriteLine("COORDINATION_MEASUREMENT " + JsonSerializer.Serialize(new { repetition, measurement }));
    }
    [LargeCoordinationMeasurementTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MeasuresSevenThousandQueuedJobsWithTwoWorkersAndPagedReads(bool concurrentCapture)
    {
        var (registry, validator, eligibility) = Harness.Services();
        await using var fixture = await Harness.Fixture.CreateAsync("sqlite", registry, validator, eligibility);
        var measurement = await fixture.MeasureCoordinationAsync(concurrentCapture, queued: 7000, workerNodes: 2, claimsPerClient: 16, readOverview: true);
        output.WriteLine("COORDINATION_LARGE_MEASUREMENT " + JsonSerializer.Serialize(measurement));
    }

}

public sealed class CoordinationMeasurementTheoryAttribute : TheoryAttribute
{
    public CoordinationMeasurementTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("ENCODING_TEST_COORDINATION_MEASUREMENTS") != "1")
            Skip = "Opt-in synthetic measurements: set ENCODING_TEST_COORDINATION_MEASUREMENTS=1; timing is not a CI assertion.";
    }
}

public sealed class LargeCoordinationMeasurementTheoryAttribute : TheoryAttribute
{
    public LargeCoordinationMeasurementTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("ENCODING_TEST_LARGE_COORDINATION_MEASUREMENTS") != "1")
            Skip = "Opt-in 7000-job measurement: set ENCODING_TEST_LARGE_COORDINATION_MEASUREMENTS=1; two workers on one server, no media or production data.";
    }
}
