using System.Text.Json;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Flows.Conditions;
using Encoding.Contracts.Nodes;
using Encoding.NodeAgent.Runtime;
using Encoding.NodeAgent.Runtime.Media;

namespace Encoding.Runtime.Tests;

internal static partial class Harness
{
    internal sealed partial class Fixture
    {
        public async Task ArtifactRelayAsync(bool missingProducer = false)
        {
            Check(ProcessToolRunner.Find("mkvmerge") is not null,
                "mkvmerge is required for extraction/consumption scenarios. Install MKVToolNix in the runtime test job or add its directory to PATH.");
            var capabilities = Capabilities(true).Concat(new NodeToolCapability[] {
                new("mkvtoolnix", "90.0", ["video"], []),
                new(FlowArtifactReference.Capability, "1.0.0", ["general"], []) }).ToArray();
            await using (var advertise = db.CreateCommand("UPDATE cluster_nodes SET capabilities=@caps"))
            {
                advertise.Parameters.AddWithValue("caps", SqlKind.Json, JsonSerializer.Serialize(capabilities, Json));
                await advertise.ExecuteNonQueryAsync();
            }
            try
            {
                var tools = new ProcessToolRunner();
                var scanner = new MediaScanner(tools);
                var source = Path.Combine(directory, "artifact-source.mkv");
                await tools.CheckedAsync("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=duration=1:size=64x64:rate=5",
                    "-f", "lavfi", "-i", "sine=duration=1:sample_rate=48000", "-c:v", "libx264", "-c:a", "aac", source], directory, CancellationToken.None);
                var sourceSha = await SafeMediaPaths.DigestAsync(source, CancellationToken.None);
                var reference = new FlowArtifactReference("extract", 0);
                var flow = Chain(Node(registry, "entry", "base-input-file"),
                    Node(registry, "extract", "audio-extract"),
                    Node(registry, "relay", "nodes-handoff", new() { ["mode"] = J("node"), ["targetNodeId"] = J(b) }),
                    Node(registry, "consume", "audio-add-track", new() { ["sourceFile"] = J(reference.ToString()), ["processingMode"] = J("direct"), ["trackName"] = J("Extracted"), ["language"] = J("fra") }),
                    Node(registry, "return", "nodes-handoff", new() { ["mode"] = J("node"), ["targetNodeId"] = J(a) }),
                    Node(registry, "end", "base-complete-flow"));
                if (missingProducer) flow = flow with {
                    Nodes = [..flow.Nodes, Node(registry, "branch", "conditions-match", new() { ["rules"] = ConditionTree.Serialize(new ConditionGroup("all", [new ConditionRule("library.id", "exists")])) })],
                    Connections = [new("enter", "entry", "success", "branch"), new("skip", "branch", "yes", "relay"), new("produce", "branch", "no", "extract"),
                    .. flow.Connections.Where(edge => edge.SourceNodeId != "entry")] };
                var (id, lease, _) = await EnqueueAsync(flow, await File.ReadAllBytesAsync(source));
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                using (var client = Client(a)) await Runner("extract-a", out _).RunAsync(lease, client, deadline.Token);
                async Task<RuntimeLease?> ClaimAfterRelayAsync(Guid worker)
                {
                    for (var attempt = 0; attempt < 100; attempt++)
                    {
                        var claim = await runtime.ClaimAsync(worker, deadline.Token);
                        if (claim is not null) return claim;
                        await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
                    }
                    return null;
                }
                var received = await ClaimAfterRelayAsync(b);
                if (received is null)
                {
                    var execution = await executions.GetAsync(id, deadline.Token);
                    throw new InvalidOperationException($"The extraction must checkpoint before relay. State: {execution?.State}; detail: {execution?.Detail}");
                }
                if (missingProducer)
                {
                    using var missingClient = Client(b);
                    await Runner("missing-b", out _).RunAsync(received, missingClient, deadline.Token);
                    var failed = await executions.GetAsync(id, deadline.Token);
                    Check(failed?.State == "failed" && failed.Detail?.Contains("produit", StringComparison.Ordinal) == true,
                        "A path without production fails explicitly instead of requesting a library input.");
                    Check(await SafeMediaPaths.DigestAsync(source, deadline.Token) == sourceSha, "A failed reference preserves the source.");
                    return;
                }
                var produced = reference.Resolve(received.Checkpoint!.Context.Variables);
                Check(received.Checkpoint.NextNodeId == "consume", "Resume at the consumer without repeating extraction.");
                using (var client = Client(b)) await Runner("consume-b", out _).RunAsync(received, client, deadline.Token);
                var returned = await ClaimAfterRelayAsync(a);
                if (returned is null)
                {
                    var execution = await executions.GetAsync(id, deadline.Token);
                    throw new InvalidOperationException($"The consumer must complete and checkpoint. State: {execution?.State}; detail: {execution?.Detail}");
                }
                Check(returned.Checkpoint!.Context.CurrentSha256 != sourceSha, "Adding the extracted track changes the temporary result.");
                Check(reference.Resolve(returned.Checkpoint.Context.Variables).GetProperty("sha256").GetString() == produced.GetProperty("sha256").GetString(),
                    "The durable file reference survives both worker changes.");
                var candidate = Path.Combine(directory, "artifact-result.mkv");
                using (var client = Client(a)) await client.DownloadAsync(id, returned.Epoch, returned.Checkpoint.Context.CurrentSha256, candidate, deadline.Token);
                var metadata = await scanner.ScanAsync(candidate, deadline.Token);
                Check(metadata.GetProperty("streams").EnumerateArray().Count(stream => stream.GetProperty("codec_type").GetString() == "audio") == 2,
                    "The real extraction is consumed as a second audio track after relay.");
                Check(await SafeMediaPaths.DigestAsync(source, deadline.Token) == sourceSha, "The source stays byte-identical.");
                Check(await executions.CancelAsync(id, deadline.Token), "Cancel before publication in this fixture.");
                await ExpireAsync(id);
                await RecoverCancellationAsync(id);
            }
            finally
            {
                await using var restore = db.CreateCommand("UPDATE cluster_nodes SET capabilities=@caps");
                restore.Parameters.AddWithValue("caps", SqlKind.Json, JsonSerializer.Serialize(Capabilities(true), Json));
                await restore.ExecuteNonQueryAsync();
            }
        }
    }
}
