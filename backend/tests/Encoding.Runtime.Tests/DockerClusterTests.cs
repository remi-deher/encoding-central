using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Encoding.NodeAgent.Runtime;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Tests de cluster avec de vrais conteneurs de l'image `encoding` (réseau Docker, utilisateur non-root, lanceur).</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute(bool needsPreviousImage = false)
    {
        if (Environment.GetEnvironmentVariable("ENCODING_TEST_DOCKER") != "1")
            Skip = "Définir ENCODING_TEST_DOCKER=1 et ENCODING_TEST_IMAGE (image construite) ; Docker, Linux et ffmpeg requis.";
        else if (!OperatingSystem.IsLinux() || Encoding.NodeAgent.Runtime.ProcessToolRunner.Find("ffmpeg") is null || Encoding.NodeAgent.Runtime.ProcessToolRunner.Find("docker") is null)
            Skip = "Linux, docker et ffmpeg sont requis.";
        else if (needsPreviousImage && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ENCODING_TEST_PREVIOUS_IMAGE")))
            Skip = "Définir ENCODING_TEST_PREVIOUS_IMAGE (ex. mrcryllix/encoding:latest, la dernière version publiée).";
    }
}

[Collection("peer-operations")]
public sealed class DockerClusterTests
{
    private static string Image => Environment.GetEnvironmentVariable("ENCODING_TEST_IMAGE") ?? "encoding:test";

    /// <summary>Un cluster éphémère : réseau Docker dédié, un dossier média partagé, un serveur joignable depuis l'hôte.</summary>
    private sealed class Cluster : IAsyncDisposable
    {
        private readonly string id = Guid.NewGuid().ToString("N")[..8];
        private readonly List<string> containers = [];
        private readonly List<string> dataDirectories = [];
        public string Network => "enc-test-" + id;
        public string Media { get; } = Path.Combine(Path.GetTempPath(), "encoding-docker-" + Guid.NewGuid().ToString("N"));
        public string Data { get; } = Path.Combine(Path.GetTempPath(), "encoding-docker-data-" + Guid.NewGuid().ToString("N"));
        public HttpClient Client { get; private set; } = null!;
        public string ServerName => "server-" + id;

        public async Task StartServerAsync()
        {
            Directory.CreateDirectory(Media);
            PrepareStorePackages(Data);
            dataDirectories.Add(Data);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Media, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            await DockerAsync("network", "create", Network);
            var port = FreePort();
            await RunAsync(ServerName, "--hostname", ServerName, "-v", $"{Data}:/var/lib/encoding-node", "-p", $"127.0.0.1:{port}:8080",
                "-e", "Node__DataDirectory=/var/lib/encoding-node",
                "-e", "Node__Roles=server", "-e", "Node__Name=" + ServerName,
                "-e", $"Cluster__AdvertisedApiBaseUrl=http://{ServerName}:8080/",
                "-e", $"Cluster__Reconciliation__AdvertisedApiBaseUrl=http://{ServerName}.local:8080/", Image);
            Client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(20) };
            Client.DefaultRequestHeaders.Add("X-Encoding-Csrf", "1");
            await WaitAsync(async () => (await Client.GetAsync("health/live")).IsSuccessStatusCode, 90, "le serveur");
            using var setup = await Client.PostAsJsonAsync("api/v1/auth/setup", new
            {
                username = "admin",
                password = "StrongPassword123!",
                setupToken = (string?)null,
                nodeDisplayName = "srv",
                mode = "createCluster",
                clusterName = "Docker integration"
            });
            Assert.Equal(HttpStatusCode.Created, setup.StatusCode);
            using var window = await Client.PutAsJsonAsync("api/v1/nodes/enrollment-window", new { minutes = 30 });
            Assert.True(window.IsSuccessStatusCode);
        }

        public Task StartWorkerAsync(string name, string image, bool preseedRuntimePackages = false)
        {
            var workerData = Data + "-" + name;
            PrepareStorePackages(workerData);
            dataDirectories.Add(workerData);
            if (preseedRuntimePackages) PrepareCustomPluginCatalog(workerData);
            var arguments = new List<string> { "--hostname", name, "-v", $"{workerData}:/var/lib/encoding-node", "-v", $"{Media}:/media/lib",
                "-e", "Node__DataDirectory=/var/lib/encoding-node" };
            if (preseedRuntimePackages) arguments.AddRange(["-v", $"{Path.Combine(workerData, "custom-plugins")}:/opt/encoding/custom-plugins"]);
            arguments.AddRange(["-e", "Node__Roles=worker", "-e", "Node__Name=" + name, "-e", "Node__MaxConcurrentTasks=1", "-e", $"Cluster__ApiBaseUrl=http://{ServerName}:8080/",
                "-e", "Worker__AutoAcceptEnrollment=true", image]);
            return RunAsync(name, [.. arguments]);
        }

        private static void PrepareStorePackages(string dataDirectory)
        {
            var fixtureDirectory = Path.Combine(Harness.RepositoryRoot(), "tests", "Fixtures", "StoreCatalog");
            var storeDirectory = Path.Combine(dataDirectory, "plugin-store");
            var installed = new List<object>();
            foreach (var (id, version) in new[] { ("base", "1.10.0"), ("video", "1.3.0") })
            {
                var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtureDirectory, id + ".json")))!.AsObject();
                manifest["runtime"] = new JsonObject { ["type"] = "native", ["entrypoint"] = "" };
                using var buffer = new MemoryStream();
                using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var entry = archive.CreateEntry("plugin.json");
                    entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(manifest.ToJsonString());
                }
                var bytes = buffer.ToArray();
                var packageDirectory = Path.Combine(storeDirectory, "packages", id, version);
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
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(Path.Combine(storeDirectory, "installed.json"),
                JsonSerializer.Serialize(installed, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(dataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        }

        private static void PrepareCustomPluginCatalog(string dataDirectory)
        {
            var fixtureDirectory = Path.Combine(Harness.RepositoryRoot(), "tests", "Fixtures", "StoreCatalog");
            var customPlugins = Path.Combine(dataDirectory, "custom-plugins");
            foreach (var id in new[] { "base", "video" })
            {
                var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtureDirectory, id + ".json")))!.AsObject();
                manifest["runtime"] = new JsonObject { ["type"] = "native", ["entrypoint"] = "" };
                var version = manifest["version"]!.GetValue<string>();
                var directory = Path.Combine(customPlugins, id, version);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "plugin.json"), manifest.ToJsonString());
                var package = File.ReadAllBytes(Path.Combine(dataDirectory, "plugin-store", "packages", id, version, "package.zip"));
                var sha256 = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
                File.WriteAllText(Path.Combine(directory, CustomPluginCatalog.VerifiedMarker), sha256);
            }
            if (!OperatingSystem.IsWindows() && Directory.Exists(customPlugins))
            {
                var fullMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                File.SetUnixFileMode(customPlugins, fullMode);
                foreach (var path in Directory.EnumerateFileSystemEntries(customPlugins, "*", SearchOption.AllDirectories))
                    File.SetUnixFileMode(path, fullMode);
            }
        }

        private async Task RunAsync(string name, params string[] arguments)
        {
            var full = new List<string> { "run", "-d", "--name", $"{name}-{id}".Replace($"-{id}-{id}", $"-{id}"), "--network", Network,
                "--network-alias", name + ".local",
                "-e", "ASPNETCORE_ENVIRONMENT=Production", "-e", "Auth__CookieSecure=false" };
            full.AddRange(arguments);
            containers.Add($"{name}-{id}".Replace($"-{id}-{id}", $"-{id}"));
            await DockerAsync([.. full]);
        }

        public string ContainerOf(string nodeName) => containers.Single(container => container.StartsWith(nodeName, StringComparison.Ordinal));

        public async Task<string> ProcessesAsync(string container) => await DockerAsync("top", container);
        public async Task<string> ProcessDiagnosticsAsync() => string.Join("\n", await Task.WhenAll(containers.Select(async container =>
            $"[{container}] {await DockerAsync(allowFailure: true, "top", container)}")));

        public async ValueTask DisposeAsync()
        {
            foreach (var container in containers)
            {
                var logs = await DockerAsync(allowFailure: true, "logs", "--tail", "5", container);
                if (Environment.GetEnvironmentVariable("ENCODING_TEST_DOCKER_LOGS") == "1") Console.WriteLine($"[{container}]\n{logs}");
                await DockerAsync(allowFailure: true, "rm", "-f", container);
            }
            await DockerAsync(allowFailure: true, "network", "rm", Network);
            Client?.Dispose();
            try { Directory.Delete(Media, true); } catch { }
            foreach (var directory in dataDirectories)
                try { Directory.Delete(directory, true); } catch { }
        }

        public async Task<string> IdentityDiagnosticsAsync()
        {
            var entries = new List<string>();
            foreach (var container in containers)
            {
                var path = Path.Combine(Path.GetTempPath(), $"encoding-node-identity-{id}-{container}.json");
                await DockerAsync(allowFailure: true, "cp", $"{container}:/var/lib/encoding-node/node-identity.json", path);
                if (!File.Exists(path)) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                File.Delete(path);
                var identity = document.RootElement;
                entries.Add(JsonSerializer.Serialize(new
                {
                    node = container,
                    id = identity.TryGetProperty("id", out var nodeId) ? nodeId.GetString() : null,
                    clusterId = identity.TryGetProperty("clusterId", out var clusterId) ? clusterId.GetString() : null,
                    enrolled = identity.TryGetProperty("enrolled", out var enrolled) && enrolled.GetBoolean(),
                    apiBaseUrl = identity.TryGetProperty("apiBaseUrl", out var apiBaseUrl) ? apiBaseUrl.GetString() : null,
                    knownServerUrls = identity.TryGetProperty("knownServerUrls", out var knownServerUrls) ? knownServerUrls.ToString() : null
                }));
            }
            return string.Join(Environment.NewLine, entries);
        }

        public async Task<string> LogsAsync() => string.Join("\n", await Task.WhenAll(containers.Select(async container =>
        {
            var logs = await DockerAsync(allowFailure: true, "logs", "--since", "10m", container);
            var runtime = logs.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains("/api/v1/runtime/nodes/", StringComparison.OrdinalIgnoreCase)
                    && line.Contains("/executions/", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("RuntimeFlowRunner", StringComparison.Ordinal)
                    || line.Contains("RuntimeWorkerService", StringComparison.Ordinal)
                    || line.Contains("RuntimeRepository", StringComparison.Ordinal)
                    || line.Contains("[launcher]", StringComparison.Ordinal)
                    || line.Contains("warn:", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("fail:", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("crit:", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Exception", StringComparison.OrdinalIgnoreCase))
                .TakeLast(100);
            return $"[{container}] " + string.Join(Environment.NewLine, runtime);
        })));
        public async Task<string> NodeDiagnosticsAsync() => (await Client.GetFromJsonAsync<JsonElement>("api/v1/nodes")).ToString();
        public async Task<string> LibraryAccessDiagnosticsAsync()
        {
            var libraries = await Client.GetFromJsonAsync<JsonElement>("api/v1/libraries");
            var library = libraries.EnumerateArray().FirstOrDefault(item => item.GetProperty("name").GetString() == "Docker");
            if (library.ValueKind == JsonValueKind.Undefined) return "Bibliothèque Docker absente.";
            var id = library.GetProperty("id").GetGuid();
            return (await Client.GetFromJsonAsync<JsonElement>($"api/v1/libraries/{id:D}/node-access")).ToString();
        }
    }

    private static Task<string> DockerAsync(params string[] arguments) => DockerAsync(false, arguments);

    private static async Task<string> DockerAsync(bool allowFailure, params string[] arguments)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var text = await output + await error;
        Assert.True(allowFailure || process.ExitCode == 0, $"docker {string.Join(' ', arguments)} : {text}");
        return text;
    }

    [DockerFact]
    public async Task KillingTheEncodingWorkerContainerResumesOnTheOtherContainerAndKeepsTheSource()
    {
        await using var cluster = new Cluster();
        await cluster.StartServerAsync();
        var source = Path.Combine(cluster.Media, "film.mkv");
        await EncodeFixtureAsync(source, seconds: 12, size: "640x360");
        var sourceSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));
        await cluster.StartWorkerAsync("worker-a", Image, preseedRuntimePackages: true);
        await cluster.StartWorkerAsync("worker-b", Image, preseedRuntimePackages: true);
        var nodes = await WaitForWorkersAsync(cluster, 2, requiredCapabilities: ["plugin/video", "ffmpeg"]);
        var executionId = await QueueEncodingAsync(cluster, nodes);

        string? victim = null;
        string? executionDiagnostic = null;
        await WaitAsync(async () =>
        {
            var current = (await cluster.Client.GetFromJsonAsync<JsonElement>("api/v1/executions")).GetProperty("executions")[0];
            executionDiagnostic = $"État : {current.GetProperty("state").GetString()} ; nœud : {current.GetProperty("node")} ; détail : {current.GetProperty("detail")}";
            if (current.GetProperty("state").GetString() != "running" || current.GetProperty("node").ValueKind != JsonValueKind.String) return false;
            var name = current.GetProperty("node").GetString()!;
            if (!(await cluster.ProcessesAsync(cluster.ContainerOf(name))).Contains("ffmpeg", StringComparison.Ordinal)) return false;
            victim = name;
            return true;
        }, 240, "ffmpeg dans le conteneur du worker", cluster, () => executionDiagnostic);

        await DockerAsync("kill", cluster.ContainerOf(victim!));   // arrêt brutal du conteneur entier (SIGKILL), sans fermeture propre

        JsonElement final = default;
        await WaitAsync(async () =>
        {
            var list = (await cluster.Client.GetFromJsonAsync<JsonElement>("api/v1/executions")).GetProperty("executions");
            Assert.Equal(1, list.GetArrayLength());
            final = list[0].Clone();
            return final.GetProperty("state").GetString() is "succeeded" or "failed";
        }, 420, "reprise et fin de l'exécution", cluster);

        var history = (await cluster.Client.GetFromJsonAsync<JsonElement>($"api/v1/executions/{executionId}/events")).ToString();
        Assert.True(final.GetProperty("state").GetString() == "succeeded", $"{final.GetProperty("detail")}\n{history}\n{await cluster.NodeDiagnosticsAsync()}\n{await cluster.LibraryAccessDiagnosticsAsync()}\n{await cluster.IdentityDiagnosticsAsync()}\n{await cluster.ProcessDiagnosticsAsync()}\n{await cluster.LogsAsync()}");
        Assert.NotEqual(victim, final.GetProperty("node").GetString());
        Assert.Contains("reprise", history, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sourceSha, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))));
        Assert.DoesNotContain(Directory.GetFiles(cluster.Media), path => path != source);
    }

    [DockerFact(needsPreviousImage: true)]
    public async Task AWorkerFromTheLastPublishedImageJoinsTheNewServerAndCompletesAFlow()
    {
        var previous = Environment.GetEnvironmentVariable("ENCODING_TEST_PREVIOUS_IMAGE")!;
        await using var cluster = new Cluster();
        await cluster.StartServerAsync();
        var source = Path.Combine(cluster.Media, "film.mkv");
        await EncodeFixtureAsync(source, seconds: 6, size: "640x360");
        var sourceSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));
        await cluster.StartWorkerAsync("worker-old", previous, preseedRuntimePackages: true);   // seul worker : c'est l'ancienne version qui doit faire le travail
        var nodes = await WaitForWorkersAsync(cluster, 1, requiredCapabilities: ["plugin/base"]);
        var executionId = await QueueEncodingAsync(cluster, nodes, encodeVideo: false);

        JsonElement final = default;
        await WaitAsync(async () =>
        {
            final = (await cluster.Client.GetFromJsonAsync<JsonElement>("api/v1/executions")).GetProperty("executions")[0].Clone();
            return final.GetProperty("state").GetString() is "succeeded" or "failed";
        }, 300, "exécution par le worker de l'ancienne version", cluster);

        var history = (await cluster.Client.GetFromJsonAsync<JsonElement>($"api/v1/executions/{executionId}/events")).ToString();
        Assert.True(final.GetProperty("state").GetString() == "succeeded", $"{final.GetProperty("detail")}\n{history}\n{await cluster.NodeDiagnosticsAsync()}\n{await cluster.LibraryAccessDiagnosticsAsync()}\n{await cluster.IdentityDiagnosticsAsync()}\n{await cluster.ProcessDiagnosticsAsync()}\n{await cluster.LogsAsync()}");
        Assert.Equal("worker-old", final.GetProperty("node").GetString());
        Assert.Equal(sourceSha, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))));
    }

    private static async Task<JsonElement> WaitForWorkersAsync(Cluster cluster, int workers, IReadOnlyCollection<string>? requiredCapabilities = null)
    {
        JsonElement workersNow = default;
        string? nodeDiagnostic = null;
        await WaitAsync(async () =>
        {
            var all = await cluster.Client.GetFromJsonAsync<JsonElement>("api/v1/nodes");
            nodeDiagnostic = all.ToString();
            var online = all.EnumerateArray().Where(node => node.GetProperty("name").GetString()!.StartsWith("worker", StringComparison.Ordinal) && node.GetProperty("status").GetString() == "online").ToArray();
            if (online.Length != workers) return false;
            if (requiredCapabilities is { Count: > 0 })
            {
                var required = requiredCapabilities.ToHashSet(StringComparer.Ordinal);
                if (online.Any(node => !required.IsSubsetOf(node.GetProperty("capabilities").EnumerateArray()
                        .Select(capability => capability.GetProperty("toolId").GetString()!).ToHashSet(StringComparer.Ordinal)))) return false;
            }
            workersNow = JsonSerializer.SerializeToElement(online);
            return true;
        }, 150, $"{workers} worker(s) en ligne", cluster, () => nodeDiagnostic);
        return workersNow;
    }

    private static async Task<Guid> QueueEncodingAsync(Cluster cluster, JsonElement workers, bool encodeVideo = true)
    {
        var client = cluster.Client;
        var library = (await PostAsync(client, "api/v1/libraries", new { name = "Docker" })).GetProperty("id").GetGuid();
        using (var access = await client.PutAsJsonAsync($"api/v1/libraries/{library}/node-access", new
        {
            nodes = workers.EnumerateArray().Select(node => new { nodeId = node.GetProperty("id").GetGuid(), mode = "direct", localPath = "/media/lib", canRead = true, canWrite = true, transferSourceNodeId = (Guid?)null, isEnabled = true })
        })) Assert.True(access.IsSuccessStatusCode, await access.Content.ReadAsStringAsync());
        var flowNodes = encodeVideo
            ? new object[]
            {
                new { id = "entry", component = "base-input-video", configuration = new { } },
                new { id = "encode", component = "base-transcode-video", configuration = new { processingMode = "direct", videoCodec = "h264", preset = "fast" } },
                new { id = "end", component = "base-complete-flow", configuration = new { } }
            }
            :
            [
                new { id = "entry", component = "base-input-file", configuration = new { } },
                new { id = "end", component = "base-complete-flow", configuration = new { } }
            ];
        var flowConnections = encodeVideo
            ? new object[]
            {
                new { id = "c1", sourceNodeId = "entry", sourceOutputId = "success", targetNodeId = "encode" },
                new { id = "c2", sourceNodeId = "encode", sourceOutputId = "success", targetNodeId = "end" }
            }
            : [new { id = "c1", sourceNodeId = "entry", sourceOutputId = "success", targetNodeId = "end" }];
        var flow = (await PostAsync(client, "api/v1/flows", new
        {
            name = "Docker", mediaType = "video", publish = true,
            nodes = flowNodes,
            connections = flowConnections
        })).GetProperty("definition").GetProperty("id").GetGuid();
        var workflow = await client.GetFromJsonAsync<JsonElement>($"api/v1/libraries/{library}/workflow");
        using (var assign = await client.PutAsJsonAsync($"api/v1/libraries/{library}/flow", new { flowId = flow, version = 1, expectedRevision = workflow.GetProperty("revision").GetInt32() }))
            Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);
        using (var discover = await client.PostAsync($"api/v1/libraries/{library}/discover", null)) Assert.Equal(HttpStatusCode.Accepted, discover.StatusCode);
        await WaitAsync(async () => (await client.GetFromJsonAsync<JsonElement>($"api/v1/libraries/{library}/workflow")).GetProperty("files").GetArrayLength() == 1, 90, "la découverte du fichier", cluster);
        workflow = await client.GetFromJsonAsync<JsonElement>($"api/v1/libraries/{library}/workflow");
        var queued = await PostAsync(client, $"api/v1/libraries/{library}/executions", new { fileIds = new[] { workflow.GetProperty("files")[0].GetProperty("id").GetGuid() }, assignmentRevision = workflow.GetProperty("revision").GetInt32() });
        Assert.Equal(1, queued.GetProperty("queued").GetInt32());
        return (await client.GetFromJsonAsync<JsonElement>("api/v1/executions")).GetProperty("executions")[0].GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path} : {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task EncodeFixtureAsync(string path, int seconds, string size)
    {
        var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var argument in new[] { "-v", "error", "-y", "-f", "lavfi", "-i", $"testsrc2=duration={seconds}:size={size}:rate=30", "-f", "lavfi", "-i", $"sine=duration={seconds}:frequency=440", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", path })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        // Les conteneurs tournent en non-root : le média de test doit rester lisible et modifiable par eux.
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, int seconds, string description, Cluster? cluster = null, Func<string?>? diagnostic = null)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            try { if (await condition()) return; }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or ArgumentOutOfRangeException) { }
            await Task.Delay(500);
        }
        throw new TimeoutException($"Délai dépassé : {description}\n{diagnostic?.Invoke()}\n{(cluster is null ? "" : await cluster.LogsAsync())}");
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
