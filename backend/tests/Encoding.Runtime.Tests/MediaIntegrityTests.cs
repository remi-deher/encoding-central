using Encoding.Contracts.Flows;
using Encoding.NodeAgent.Runtime;
using Encoding.NodeAgent.Runtime.Media;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class MediaIntegrityTests
{
    [Fact]
    public void ExplicitPolicyIsRejectedOnLegacyWorkersEvenBeforeItsSegment()
    {
        var (registry, validator, eligibility) = Harness.Services();
        var flow = Harness.Chain(Harness.Node(registry, "input", "base-input-file"),
            Harness.Node(registry, "integrity", MediaIntegrityPolicy.Component), Harness.Node(registry, "end", "base-complete-flow"));
        flow = flow with { Connections = flow.Connections.Select(edge => edge.SourceNodeId == "integrity" ? edge with { SourceOutputId = "valid" } : edge).ToArray() };
        Assert.True(validator.Validate(flow).IsValid);
        Assert.True(eligibility.CanRun(flow, [.. Harness.Capabilities(true).Where(cap => cap.ToolId != "plugin/probe"), new("plugin/probe", "1.1.0", ["general"], []), new(MediaIntegrityPolicy.Capability, "1.0.0", ["general"], [])], Guid.NewGuid()));
        Assert.False(eligibility.CanRun(flow, Harness.Capabilities(true), Guid.NewGuid()));
    }

    [Fact]
    public void ImpossibleGpuCheckExposesSafeActionableDiagnosticWithoutEchoingArbitraryValues()
    {
        Encoding.Contracts.Executions.RuntimeCheckpoint Checkpoint(string reason) => new("check", Guid.NewGuid(), "error", null,
            new(new string('a',64), ".mkv", new Dictionary<string,System.Text.Json.JsonElement> {
                [MediaIntegrityPolicy.ReceiptVariable] = System.Text.Json.JsonSerializer.SerializeToElement(new { reason })
            }, null, null, []), null);
        var diagnostic = Encoding.Api.Executions.ExecutionRepository.ReadIntegrityDiagnostic(Checkpoint("gpu_decoder_unavailable"));
        Assert.NotNull(diagnostic);
        Assert.Equal("gpu_decoder_unavailable", diagnostic.Code);
        Assert.Contains("secours CPU est interdit", diagnostic.Message);
        Assert.Null(Encoding.Api.Executions.ExecutionRepository.ReadIntegrityDiagnostic(Checkpoint("untrusted-value")));
    }

    [MediaToolsFact]
    public async Task QuickAndDeepCheckExactArtifactAndGpuNeverSilentlyFallsBack()
    {
        var directory = Path.Combine(Path.GetTempPath(), "integrity-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var tools = new ProcessToolRunner();
            var path = Path.Combine(directory, "synthetic.mkv");
            await tools.CheckedAsync("ffmpeg", ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=size=32x32:rate=10", "-t", "2", "-c:v", "mpeg4", path], directory, default);
            var sha = await SafeMediaPaths.DigestAsync(path, default);
            var checker = new MediaIntegrityChecker(tools, new MediaScanner(tools));
            Assert.Equal("valid", (await checker.CheckAsync(path, sha, "video", "quick", "auto", [], default)).Output);
            using var capture = RuntimeCommandCapture.Begin();
            using var step = capture.BeginStep("integrity", Guid.NewGuid());
            var deep = await checker.CheckAsync(path, sha, "video", "deep", "cpu", [], default);
            Assert.Equal("valid", deep.Output);
            Assert.Equal("cpu", deep.Acceleration);
            Assert.Contains(capture.Pending(), log => log.Command.Command.Contains("-progress") && log.Command.FinishedAt is not null);
            var gpu = await checker.CheckAsync(path, sha, "video", "deep", "gpu", [], default);
            Assert.Equal("error", gpu.Output);
            Assert.Equal("gpu_decoder_unavailable", gpu.Reason);
            await File.AppendAllTextAsync(path, "changed");
            Assert.Equal("artifact_changed", (await checker.CheckAsync(path, sha, "video", "quick", "cpu", [], default)).Reason);
        }
        finally { Directory.Delete(directory, true); }
    }
}
