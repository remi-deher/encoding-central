using System.Text.Json;
using Encoding.Api.Mcp;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class McpSessionManagerTests
{
    [Fact]
    public void SessionIsBoundToItsOwnerAndRequestIdsAreReservedOnce()
    {
        var manager = new McpSessionManager();
        Assert.True(manager.TryCreateSession("session-1", "owner-1", out _));
        using var document = JsonDocument.Parse("42");

        Assert.False(manager.TryReserveRequest("session-1", "owner-2", document.RootElement, out _, out _));
        Assert.True(manager.TryReserveRequest("session-1", "owner-1", document.RootElement, out var duplicate, out var limitReached));
        Assert.False(duplicate);
        Assert.False(limitReached);

        Assert.True(manager.TryReserveRequest("session-1", "owner-1", document.RootElement, out duplicate, out limitReached));
        Assert.True(duplicate);
        Assert.False(limitReached);
    }

    [Fact]
    public async Task ConcurrentReservationsAcceptOnlyOneCopyOfARequestId()
    {
        var manager = new McpSessionManager();
        Assert.True(manager.TryCreateSession("session-1", "owner-1", out _));
        using var document = JsonDocument.Parse("\"same\"");
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            var accepted = manager.TryReserveRequest("session-1", "owner-1", document.RootElement, out var duplicate, out var limitReached);
            return accepted && !duplicate && !limitReached;
        })));

        Assert.Single(results, accepted => accepted);
    }
}
