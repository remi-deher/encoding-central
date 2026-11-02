using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
using Encoding.Api.Infrastructure;
using Encoding.NodeAgent;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Test multi-processus : un vrai serveur et deux vrais workers ; le worker qui encode est tué (SIGKILL) en plein ffmpeg.</summary>
public sealed class MultiProcessFactAttribute : FactAttribute
{
    public MultiProcessFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ENCODING_TEST_MULTIPROCESS") != "1")
            Skip = "Définir ENCODING_TEST_MULTIPROCESS=1 (processus réels, ffmpeg requis, plusieurs minutes).";
        else if (!OperatingSystem.IsLinux() || Encoding.NodeAgent.Runtime.ProcessToolRunner.Find("ffmpeg") is null)
            Skip = "Linux et ffmpeg sont requis.";
    }
}

[Collection("peer-operations")]
public sealed class WorkerProcessLossTests
{
    private sealed record Proc(Process Process, ConcurrentQueue<string> Output, string Name);

    [MultiProcessFact]
    public async Task KillingTheEncodingWorkerMidFfmpegResumesOnTheOtherWorkerAndKeepsTheSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "encoding-kill-" + Guid.NewGuid().ToString("N"));
        var media = Path.Combine(root, "media");
        Directory.CreateDirectory(media);
        var processes = new List<Proc>();
        try
        {
            var source = Path.Combine(media, "film.mkv");
            await RunAsync("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=duration=90:size=1280x720:rate=30",
                "-f", "lavfi", "-i", "sine=duration=90:frequency=440", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", source);
            var sourceSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));

            var apiPort = FreePort();
            PrepareStorePackages(Path.Combine(root, "api"));
            var secret = Path.Combine(root, "local-enrollment.secret");
            File.WriteAllText(secret, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            var api = Start("api", typeof(ClusterReconciliationRepository).Assembly.Location, processes, env =>
            {
                env["ASPNETCORE_ENVIRONMENT"] = "Production";
                env["ASPNETCORE_URLS"] = $"http://127.0.0.1:{apiPort}";
                env["ConnectionStrings__Cluster"] = $"Data Source={Path.Combine(root, "api", "cluster.sqlite")}";
                env["Node__DataDirectory"] = Path.Combine(root, "api");
                env["PluginSettings__KeyDirectory"] = Path.Combine(root, "keys");
                env["Node__LocalEnrollmentSecretFile"] = secret;
                env["Auth__CookieSecure"] = "false";
                env["Logging__LogLevel__Default"] = "Warning";
            });
            using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri($"http://127.0.0.1:{apiPort}/api/v1/"), Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.Add("X-Encoding-Csrf", "1");
            await WaitAsync(async () => (await client.GetAsync("../../health/live")).IsSuccessStatusCode, 60, "API", processes);
            using (var setup = await client.PostAsJsonAsync("auth/setup", new { username = "admin", password = "StrongPassword123!", setupToken = (string?)null, nodeDisplayName = "srv" }))
                Assert.Equal(HttpStatusCode.Created, setup.StatusCode);

            foreach (var name in new[] { "worker-a", "worker-b" })
            {
                var data = Path.Combine(root, name);
                var port = FreePort();
                Start(name, typeof(NodeAgentOptions).Assembly.Location, processes, env =>
                {
                    env["Node__Name"] = name; env["Node__Roles"] = "worker"; env["Node__MaxConcurrentTasks"] = "1";
                    env["Node__DataDirectory"] = data; env["Cluster__ApiBaseUrl"] = $"http://127.0.0.1:{apiPort}/";
                    env["WorkerUi__Port"] = port.ToString(); env["Node__LocalEnrollmentSecretFile"] = secret;
                    env["Worker__AutoAcceptEnrollment"] = "true"; env["Logging__LogLevel__Default"] = "Warning";
                });
            }
            JsonElement nodes = default;
            await WaitAsync(async () =>
            {
                nodes = await client.GetFromJsonAsync<JsonElement>("nodes");
                return nodes.GetArrayLength() == 2 && nodes.EnumerateArray().All(node => node.GetProperty("status").GetString() == "online");
            }, 90, "two online workers", processes);

            var library = (await PostAsync(client, "libraries", new { name = "Kill" })).GetProperty("id").GetGuid();
            using (var access = await client.PutAsJsonAsync($"libraries/{library}/node-access", new
            {
                nodes = nodes.EnumerateArray().Select(node => new { nodeId = node.GetProperty("id").GetGuid(), mode = "direct", localPath = media, canRead = true, canWrite = true, transferSourceNodeId = (Guid?)null, isEnabled = true })
            })) Assert.True(access.IsSuccessStatusCode, await access.Content.ReadAsStringAsync());
            var flow = (await PostAsync(client, "flows", new
            {
                name = "Kill", mediaType = "video", publish = true,
                nodes = new object[]
                {
                    new { id = "entry", component = "base-input-video", configuration = new { } },
                    new { id = "encode", component = "base-transcode-video", configuration = new { processingMode = "direct" } },
                    new { id = "end", component = "base-complete-flow", configuration = new { } }
                },
                connections = new object[]
                {
                    new { id = "c1", sourceNodeId = "entry", sourceOutputId = "success", targetNodeId = "encode" },
                    new { id = "c2", sourceNodeId = "encode", sourceOutputId = "success", targetNodeId = "end" }
                }
            })).GetProperty("definition").GetProperty("id").GetGuid();
            var workflow = await client.GetFromJsonAsync<JsonElement>($"libraries/{library}/workflow");
            using (var assign = await client.PutAsJsonAsync($"libraries/{library}/flow", new { flowId = flow, version = 1, expectedRevision = workflow.GetProperty("revision").GetInt32() }))
                Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);
            using (var discover = await client.PostAsync($"libraries/{library}/discover", null)) Assert.Equal(HttpStatusCode.Accepted, discover.StatusCode);
            await WaitAsync(async () => (await client.GetFromJsonAsync<JsonElement>($"libraries/{library}/workflow")).GetProperty("files").GetArrayLength() == 1, 60, "file discovery", processes);
            workflow = await client.GetFromJsonAsync<JsonElement>($"libraries/{library}/workflow");
            var queued = await PostAsync(client, $"libraries/{library}/executions", new { fileIds = new[] { workflow.GetProperty("files")[0].GetProperty("id").GetGuid() }, assignmentRevision = workflow.GetProperty("revision").GetInt32() });
            Assert.Equal(1, queued.GetProperty("queued").GetInt32());

            // Attendre que ffmpeg encode réellement, puis tuer le worker propriétaire (et son ffmpeg) sans préavis.
            string? victim = null; Guid executionId = default;
            await WaitAsync(async () =>
            {
                var current = (await client.GetFromJsonAsync<JsonElement>("executions")).GetProperty("executions")[0];
                executionId = current.GetProperty("id").GetGuid();
                if (current.GetProperty("state").GetString() != "running" || current.GetProperty("node").ValueKind != JsonValueKind.String) return false;
                if (!FfmpegRunningFor(executionId)) return false;
                victim = current.GetProperty("node").GetString();
                return true;
            }, 180, "ffmpeg encoding", processes);
            var killed = processes.Single(proc => proc.Name == victim);
            killed.Process.Kill(entireProcessTree: true);
            await killed.Process.WaitForExitAsync();
            Assert.False(FfmpegRunningFor(executionId), "Le ffmpeg du worker tué doit avoir disparu.");

            // Après expiration du bail (60 s), le serveur reprend l'exécution sur le worker survivant, sans doublon.
            JsonElement final = default;
            await WaitAsync(async () =>
            {
                var list = (await client.GetFromJsonAsync<JsonElement>("executions")).GetProperty("executions");
                Assert.Equal(1, list.GetArrayLength());
                final = list[0].Clone();
                return final.GetProperty("state").GetString() is "succeeded" or "failed";
            }, 360, "resume and completion", processes);

            if (final.GetProperty("state").GetString() != "succeeded")
            {
                var history = (await client.GetFromJsonAsync<JsonElement>($"executions/{executionId}/events")).ToString();
                var state = (await client.GetFromJsonAsync<JsonElement>("nodes")).EnumerateArray().Select(node => $"{node.GetProperty("name")}={node.GetProperty("status")}");
                Assert.Fail($"Exécution : {final.GetProperty("detail")}\nNœuds : {string.Join(", ", state)}\nHistorique : {history}");
            }
            Assert.Equal(executionId, final.GetProperty("id").GetGuid());
            Assert.NotEqual(victim, final.GetProperty("node").GetString());
            var events = (await client.GetFromJsonAsync<JsonElement>($"executions/{executionId}/events")).ToString();
            Assert.Contains("reprise", events, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(sourceSha, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))));
            Assert.DoesNotContain(Directory.GetFiles(media, "*", SearchOption.TopDirectoryOnly), path => path != source);
        }
        finally
        {
            foreach (var proc in processes)
                try { if (!proc.Process.HasExited) proc.Process.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static bool FfmpegRunningFor(Guid executionId)
    {
        foreach (var pid in Directory.GetDirectories("/proc").Select(Path.GetFileName).Where(name => name is not null && name.All(char.IsDigit)))
        {
            try
            {
                var command = File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ');
                if (command.Contains("ffmpeg", StringComparison.Ordinal) && command.Contains(executionId.ToString("D"), StringComparison.Ordinal)) return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path} : {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Proc Start(string name, string assembly, List<Proc> processes, Action<IDictionary<string, string?>> configure)
    {
        var start = new ProcessStartInfo("dotnet") { ArgumentList = { assembly }, WorkingDirectory = Path.GetDirectoryName(assembly)!, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        configure(start.Environment);
        var process = Process.Start(start) ?? throw new InvalidOperationException($"Impossible de démarrer {name}.");
        var output = new ConcurrentQueue<string>();
        process.OutputDataReceived += (_, args) => { if (args.Data is not null) output.Enqueue(args.Data); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) output.Enqueue(args.Data); };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        var proc = new Proc(process, output, name);
        processes.Add(proc);
        return proc;
    }

    private static void PrepareStorePackages(string dataDirectory)
    {
        var fixtureDirectory = Path.Combine(Harness.RepositoryRoot(), "tests", "Fixtures", "StoreCatalog");
        var installed = new List<object>();
        foreach (var (id, version) in new[] { ("base", "1.10.0"), ("video", "1.3.0") })
        {
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtureDirectory, id + ".json")))!.AsObject();
            manifest["runtime"] = new JsonObject { ["type"] = "native", ["entrypoint"] = "" };
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            using (var writer = new StreamWriter(archive.CreateEntry("plugin.json").Open()))
                writer.Write(manifest.ToJsonString());
            var bytes = buffer.ToArray();
            var packageDirectory = Path.Combine(dataDirectory, "plugin-store", "packages", id, version);
            Directory.CreateDirectory(packageDirectory);
            File.WriteAllBytes(Path.Combine(packageDirectory, "package.zip"), bytes);
            installed.Add(new
            {
                id,
                version,
                name = manifest["name"]!.GetValue<string>(),
                sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                source = "repo:test",
                installedAt = DateTimeOffset.UtcNow
            });
        }
        var storeDirectory = Path.Combine(dataDirectory, "plugin-store");
        File.WriteAllText(Path.Combine(storeDirectory, "installed.json"),
            JsonSerializer.Serialize(installed, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, int seconds, string description, List<Proc> processes)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            try { if (await condition()) return; }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or ArgumentOutOfRangeException) { }
            await Task.Delay(500);
        }
        var logs = string.Join("\n", processes.SelectMany(proc => proc.Output.TakeLast(15).Select(line => $"[{proc.Name}] {line}")));
        throw new TimeoutException($"Délai dépassé : {description}\n{logs}");
    }

    private static async Task RunAsync(string tool, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
