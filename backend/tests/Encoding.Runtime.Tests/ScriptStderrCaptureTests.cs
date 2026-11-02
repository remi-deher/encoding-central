using Encoding.NodeAgent.Runtime;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class ScriptStderrCaptureTests
{
    [Fact]
    public void Script_Standard_Error_Is_Kept_And_Protocol_Output_Is_Hidden()
    {
        using var capture = RuntimeCommandCapture.Begin();
        var invocation = Guid.NewGuid();
        using (capture.BeginStep("script", invocation))
            RuntimeCommandCapture.Record("python3", ["script.py"], "{\"outputId\":\"success\"}",
                "Traceback: boom\ntoken=abc123", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false, protocolOutput: true);
        var log = Assert.Single(capture.ForStep("script", invocation));
        Assert.Equal(string.Empty, log.StandardOutput);
        Assert.Contains("Traceback: boom", log.StandardError);
        Assert.DoesNotContain("abc123", log.StandardError);
    }
}
