using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Flows.Conditions;
using Encoding.Core;
using Encoding.Api.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using Encoding.Api.Infrastructure.Data.Sqlite;

namespace Encoding.Runtime.Tests;

internal static class FlowSimulationScenarios
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static async Task RunAsync(FlowComponentRegistry registry, FlowDefinitionValidator validator)
    {
        var simulator = new FlowSimulator(registry, validator);
        FlowNodeDefinition Node(string nodeId, string componentId, IReadOnlyDictionary<string, JsonElement>? changes = null)
        {
            var reference = registry.FindReference(componentId) ?? throw new InvalidOperationException($"Composant {componentId} introuvable.");
            var plugin = registry.Plugins.Single(plugin => plugin.Id == reference.PluginId);
            var descriptor = plugin.Components.Single(component => component.Id == reference.ComponentId);
            var config = descriptor.ConfigurationSchema.Fields.Where(field => field.DefaultValue is not null).ToDictionary(field => field.Id, field => field.DefaultValue!.Value);
            if (changes is not null) foreach (var pair in changes) config[pair.Key] = pair.Value;
            return new(nodeId, null, new(plugin.Id, plugin.Version, descriptor.Id, descriptor.Version, 1), config);
        }
        JsonElement Rule(params ConditionExpression[] rules) => ConditionTree.Serialize(new ConditionGroup("all", rules));
        var source = Guid.NewGuid(); var worker = Guid.NewGuid(); var target = Guid.NewGuid();
        var before = J(new { format = new { duration = 1, size = 1234 }, streams = new object[] { new { codec_type = "video", codec_name = "h264", width = 1920, height = 1080 } } });
        var after = J(new { format = new { duration = 1, size = 1000 }, streams = new object[] { new { codec_type = "video", codec_name = "hevc", width = 1920, height = 1080 } } });
        var file = new FlowSimulationFile("folder/film.mkv", before, new Dictionary<string, JsonElement>(), source, worker);
        var input = Node("entry", "base-input-file");
        var match = Node("match", "conditions-match", new Dictionary<string, JsonElement> { ["rules"] = Rule(new ConditionRule("video.codec", "eq", J("h264"))) });
        var finish = Node("finish", "base-complete-flow");
        var basic = new FlowDefinition(1, Guid.NewGuid(), 1, "Simulator", "video", [input, match, finish],
            [new("e1", "entry", "success", "match"), new("e2", "match", "yes", "finish"), new("e3", "match", "no", "finish")]);
        FlowSimulationResult Run(FlowDefinition flow, FlowSimulationFile? profile = null, IReadOnlyDictionary<string, FlowSimulationAssumption>? assumptions = null)
            => simulator.Simulate(new(J(flow), profile ?? file, assumptions));
        var yes = Run(basic);
        Check(yes.Status == "completed" && yes.Steps[1].OutputId == "yes" && yes.Steps.All(step => !step.Assumed), "Native true branch is evaluated without assumptions.");
        Check(yes.Steps[1].Conditions is [{ Result: true }] && yes.Steps[1].Conditions![0].Actual!.Value.GetString() == "h264", "The evaluator reports the actual value and decision.");
        var no = Run(basic, file with { Metadata = after });
        Check(no.Status == "completed" && no.Steps[1].OutputId == "no", "Native false branch reconverges exactly once.");
        var missing = Run(basic, file with { Metadata = J(new { format = new { }, streams = Array.Empty<object>() }) });
        Check(missing.Status == "failed" && missing.Steps[1].OutputId == "error", "Missing comparison data routes to error, not false.");
        Check(Run(basic with { Connections = [.. basic.Connections, new("cycle", "finish", "success", "entry")] }).Status == "invalid", "Invalid flows never simulate.");

        var videoCodecNode = Node("vcodec", "conditions-video-codec", new Dictionary<string, JsonElement> { ["codec"] = J("h264") });
        var flowVCodec = new FlowDefinition(1, Guid.NewGuid(), 1, "Simulate VCodec", "video", [input, videoCodecNode, finish],
            [new("e1", "entry", "success", "vcodec"), new("e2", "vcodec", "yes", "finish"), new("e3", "vcodec", "no", "finish")]);
        var vcodecYes = Run(flowVCodec);
        Check(vcodecYes.Status == "completed" && vcodecYes.Steps[1].OutputId == "yes", "Dedicated video codec condition simulates deterministically with metadata.");
        var vcodecNo = Run(flowVCodec, file with { Metadata = after });
        Check(vcodecNo.Status == "completed" && vcodecNo.Steps[1].OutputId == "no", "Dedicated video codec condition simulates no for different codec.");

        var transform = Node("transform", "base-transcode-video", new Dictionary<string, JsonElement> { ["processingMode"] = J("direct") });
        var handoff = Node("handoff", "nodes-handoff", new Dictionary<string, JsonElement> { ["mode"] = J("inherit") });
        var variable = Node("variable", "base-variable", new Dictionary<string, JsonElement> { ["name"] = J("score"), ["value"] = J("42") });
        var probe = Node("probe", "base-probe-media");
        var finalMatch = match with { Configuration = new Dictionary<string, JsonElement> { ["rules"] = Rule(
            new ConditionRule("video.codec", "eq", J("hevc")), new ConditionRule("video.codec", "eq", J("h264"), Scope: "source"),
            new ConditionRule("worker.nodeId", "eq", J(target.ToString())), new ConditionRule("processing.modified", "eq", J(true)),
            new ConditionRule("variable.number", "eq", J(42), "score")) } };
        var flow = basic with { Nodes = [input, variable, probe, transform, handoff, finalMatch, finish], Connections = [
            new("a", "entry", "success", "variable"), new("b", "variable", "success", "probe"), new("c", "probe", "success", "transform"),
            new("d", "transform", "success", "handoff"), new("e", "handoff", "success", "match"), new("f", "match", "yes", "finish") ] };
        var waiting = Run(flow);
        Check(waiting.Status == "waiting" && waiting.WaitingNodeId == "transform" && waiting.Steps.Count == 3, "Real transformations require an explicit result.");
        Check(waiting.Steps[2].Context.Variables.ContainsKey("mediaDetails"), "Probe uses the profile and produces the same variable shape as a worker.");
        var assumptions = new Dictionary<string, FlowSimulationAssumption> { ["transform"] = new("success", after, Modified: true), ["handoff"] = new("success", WorkerNodeId: target) };
        var completed = simulator.Simulate(new(J(flow), file, assumptions), new Dictionary<string, JsonElement> { ["mode"] = J("node"), ["targetNodeId"] = J(target.ToString()) });
        Check(completed.Status == "completed" && completed.Steps[^2].OutputId == "yes", "Assumed conversion and relay retain source metadata, update worker and evaluate typed variables.");
        Check(completed.Steps[3].Assumed && completed.Steps[4].Assumed && !completed.Steps[5].Assumed, "The trace distinguishes assumptions from real decisions.");
        ExpectArgument(() => simulator.Simulate(new(J(flow), file, assumptions), new Dictionary<string, JsonElement> { ["mode"] = J("node"), ["targetNodeId"] = J(Guid.NewGuid().ToString()) }));
        ExpectArgument(() => Run(flow, assumptions: new Dictionary<string, FlowSimulationAssumption> { ["transform"] = new("unknown") }));
        ExpectArgument(() => Run(basic, file with { Metadata = J(new { streams = new[] { "invalid" }, format = new { } }) }));
        var planned = transform with { Configuration = new Dictionary<string, JsonElement> { ["processingMode"] = J("plan") } };
        var planFlow = basic with { Nodes = [input, planned, finish], Connections = [new("a", "entry", "success", "transform"), new("b", "transform", "success", "finish")] };
        Check(Run(planFlow).Status == "failed", "A pending plan cannot be reported as a completed simulation.");
        var constrained = basic with { Nodes = [input, Node("constraint", "nodes-run-on-node", new Dictionary<string, JsonElement> { ["targetNodeId"] = J(target.ToString()) }), finish],
            Connections = [new("a", "entry", "success", "constraint"), new("b", "constraint", "success", "finish")] };
        var constraintResult = new Dictionary<string, FlowSimulationAssumption> { ["constraint"] = new("success") };
        ExpectArgument(() => Run(constrained, assumptions: constraintResult));
        Check(Run(constrained, file with { WorkerNodeId = target }, constraintResult).Status == "completed", "A node constraint cannot contradict the simulated worker.");
        var selectedConstraint = basic with { Nodes = [input, Node("selector", "nodes-run-on-node", new Dictionary<string, JsonElement> { ["targetGroupId"] = J(Guid.NewGuid().ToString()), ["requiredLabels"] = J("gpu=nvidia") }), finish],
            Connections = [new("a", "entry", "success", "selector"), new("b", "selector", "success", "finish")] };
        var selectorResult = Run(selectedConstraint, file with { WorkerNodeId = target }, new Dictionary<string, FlowSimulationAssumption> { ["selector"] = new("success") });
        Check(selectorResult.Status == "completed" && selectorResult.Steps[1].Detail.Contains("ne sont pas vérifiées"), "Group and label eligibility remains an explicit assumption in simulation.");
        var directAccessFlow = basic with { Nodes = [input, Node("directAccess", "nodes-require-direct-access"), finish],
            Connections = [new("a", "entry", "success", "directAccess"), new("b", "directAccess", "success", "finish")] };
        var directAccessSim = Run(directAccessFlow);
        Check(directAccessSim.Status == "completed" && directAccessSim.Steps[1].OutputId == "success"
            && directAccessSim.Steps[1].Detail.Contains("accès direct"), "Direct access constraint simulation succeeds with detail.");
        var selectorRelay = simulator.Simulate(new(J(flow), file, assumptions), new Dictionary<string, JsonElement>
        {
            ["mode"] = J("selector"), ["targetGroupId"] = J(Guid.NewGuid().ToString()), ["requiredLabels"] = J("gpu=nvidia"),
        });
        Check(selectorRelay.Status == "completed" && selectorRelay.Steps[4].Detail.Contains("Le scénario suppose"), "A simulated relay records assumed group and label eligibility.");
        var deferred = basic with { Nodes = [input, Node("defer", "base-defer-processing", new Dictionary<string, JsonElement> { ["delayMinutes"] = J(7), ["reason"] = J("Simulation") })], Connections = [new("a", "entry", "success", "defer")] };
        var deferredResult = Run(deferred);
        Check(deferredResult.TerminalResult == "deferred" && deferredResult.Steps[^1].Context.Variables["runtime.deferMinutes"].GetInt32() == 7, "Deferred execution retains the configured delay.");
        Check(FlowVariableValue.Parse("42").GetInt32() == 42 && FlowVariableValue.Parse("plain text").GetString() == "plain text", "Variable JSON/text semantics are shared.");

        var mediaInfo = J(new { media = new { track = new object[] {
            new Dictionary<string,object>{{"@type","General"},{"Duration","1.5"},{"FileSize","1234"}},
            new Dictionary<string,object>{{"@type","Video"},{"Format","HEVC"},{"Width","1920"},{"Height","1080"}},
            new Dictionary<string,object>{{"@type","Audio"},{"Format","AAC"},{"Channels","6"},{"Language","fr"}} } } });
        var normalized = MediaInfoMetadata.Normalize(mediaInfo);
        Check(normalized.GetProperty("streams")[0].GetProperty("codec_name").GetString() == "hevc"
            && normalized.GetProperty("streams")[1].GetProperty("tags").GetProperty("language").GetString() == "fra"
            && normalized.GetProperty("format").GetProperty("duration").GetDecimal() == 1.5m, "MediaInfo codecs, numbers and languages match the native conditions.");
        await ImportAsync();
        await HttpAsync(registry, validator, new(J(basic), file));
    }
    private static void ExpectArgument(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new InvalidOperationException("An invalid scenario must be rejected.");
    }
    private static async Task ImportAsync()
    {
        if (Environment.GetEnvironmentVariable("ENCODING_TEST_REQUIRE_MEDIA_TOOLS") != "1" && Encoding.NodeAgent.Runtime.ProcessToolRunner.Find("mediainfo") is null) return;
        var directory = Path.Combine(Path.GetTempPath(), "encoding-simulator-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "synthetic.mkv");
            var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
            foreach (var arg in new[] { "-v", "error", "-f", "lavfi", "-i", "color=c=black:s=64x64:r=1", "-t", "1", "-c:v", "ffv1", path }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
            Check(process.ExitCode == 0, "Synthetic media generation failed: " + await errors);
            var digest = SHA256.HashData(await File.ReadAllBytesAsync(path));
            var importer = new FlowMetadataImporter();
            await using var input = File.OpenRead(path);
            var metadata = await importer.ImportAsync(input, CancellationToken.None);
            Check(metadata.GetProperty("streams")[0].GetProperty("width").GetInt32() == 64, "Real MediaInfo reads a synthetic file.");
            var after = SHA256.HashData(await File.ReadAllBytesAsync(path));
            Check(digest.SequenceEqual(after), "Metadata inspection preserves the source bytes.");
            try { await importer.ImportAsync(new MemoryStream(), CancellationToken.None); throw new Exception("An empty import must fail."); }
            catch (InvalidDataException) { }
            using var cancelled = new CancellationTokenSource();
            await using var first = new BlockingStream(); await using var second = new BlockingStream();
            var firstRead = importer.ImportAsync(first, cancelled.Token);
            var secondRead = importer.ImportAsync(second, cancelled.Token);
            await Task.WhenAll(first.Started.Task, second.Started.Task);
            try { await importer.ImportAsync(new MemoryStream(), CancellationToken.None); throw new Exception("Concurrency must be bounded."); }
            catch (InvalidOperationException) { }
            cancelled.Cancel();
            foreach (var read in new[] { firstRead, secondRead })
            { try { await read; throw new Exception("A cancelled import must stop."); } catch (OperationCanceledException) { } }
            await using var retry = File.OpenRead(path);
            Check((await importer.ImportAsync(retry, CancellationToken.None)).GetProperty("streams").GetArrayLength() > 0, "Cancellation releases inspection slots.");
            try { await importer.ImportAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("not a media")), CancellationToken.None); throw new Exception("An unrecognized import must fail."); }
            catch (InvalidDataException) { }
        }
        finally { Directory.Delete(directory, true); }
    }
    private static async Task HttpAsync(FlowComponentRegistry registry, FlowDefinitionValidator validator, FlowSimulationRequest request)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(registry); builder.Services.AddSingleton(validator);
        builder.Services.AddSingleton<FlowSimulator>(); builder.Services.AddSingleton<FlowMetadataImporter>();
        await using var unusedDatabase = new SqliteDataSource("Data Source=:memory:");
        builder.Services.AddSingleton(new PluginConfigurationRepository(unusedDatabase, new EphemeralDataProtectionProvider()));
        await using var app = builder.Build(); app.MapFlowSimulationEndpoints(); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        using var success = await client.PostAsJsonAsync("/api/v1/flows/simulate", request);
        Check(success.StatusCode == HttpStatusCode.OK && (await success.Content.ReadFromJsonAsync<FlowSimulationResult>())?.Steps[1].OutputId == "yes", "The HTTP simulation contract returns typed trace decisions.");
        using var invalid = await client.PostAsJsonAsync("/api/v1/flows/simulate", request with { Definition = J(new { invalid = true }) });
        Check(invalid.StatusCode == HttpStatusCode.UnprocessableEntity, "Invalid flow definitions return 422.");
        using var invalidProfile = await client.PostAsJsonAsync("/api/v1/flows/simulate", request with { File = request.File with { SourceNodeId = Guid.Empty } });
        Check(invalidProfile.StatusCode == HttpStatusCode.BadRequest, "Invalid profiles return 400.");
        using var empty = await client.PostAsync("/api/v1/flows/inspect-metadata", new ByteArrayContent([]));
        Check(empty.StatusCode == HttpStatusCode.BadRequest, "Empty metadata imports return 400.");
        await app.StopAsync();
    }
    private sealed class BlockingStream : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
