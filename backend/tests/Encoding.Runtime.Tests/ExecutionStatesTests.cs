using Encoding.Contracts.Executions;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class ExecutionStatesTests
{
    [Theory]
    [InlineData(ExecutionStates.Queued)]
    [InlineData(ExecutionStates.Running)]
    [InlineData(ExecutionStates.Cancelling)]
    [InlineData(ExecutionStates.Succeeded)]
    [InlineData(ExecutionStates.Failed)]
    [InlineData(ExecutionStates.Cancelled)]
    [InlineData(ExecutionStates.Deferred)]
    [InlineData(ExecutionStates.Quarantined)]
    public void KnownStatesAreRecognised(string state)
    {
        Assert.True(ExecutionStates.IsKnown(state));
        Assert.True(ExecutionStates.TryParse(state, out var parsed));
        Assert.Equal(state, parsed);
    }

    [Fact]
    public void UnknownStateIsRejected()
    {
        Assert.False(ExecutionStates.IsKnown("completed"));
        Assert.False(ExecutionStates.TryParse("completed", out _));
        Assert.False(ExecutionStates.IsTerminal("queued"));
        Assert.True(ExecutionStates.IsTerminal(ExecutionStates.Succeeded));
        Assert.True(ExecutionStates.IsActive(ExecutionStates.Running));
    }

    [Fact]
    public void FailureOutcomeUsesTerminalOrErrorOutput()
    {
        Assert.True(ExecutionOutcomes.IsFailure(ExecutionOutcomes.Failed, null));
        Assert.True(ExecutionOutcomes.IsFailure(null, ExecutionOutcomes.ErrorOutput));
        Assert.False(ExecutionOutcomes.IsFailure(ExecutionOutcomes.Success, "ok"));
        Assert.True(ExecutionOutcomes.IsKnownTerminal(ExecutionOutcomes.Deferred));
    }
}
