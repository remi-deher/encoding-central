using Encoding.WorkerAgent.Services;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class WorkerStoreCursorTests
{
    [Fact]
    public async Task LastEventIdSurvivesListenerRecreation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "encoding-store-cursor-" + Guid.NewGuid().ToString("N"));
        try
        {
            var firstListener = new StoreEventCursor(directory);
            await firstListener.SaveAsync("v1.42", CancellationToken.None);
            var restartedListener = new StoreEventCursor(directory);
            Assert.Equal("v1.42", await restartedListener.ReadAsync(CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
