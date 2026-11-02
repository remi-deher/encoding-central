using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Encoding.Api.Flows;
using Encoding.Api.Plugins;
using Encoding.Core.Plugins;
using Encoding.NodeAgent;
using Encoding.NodeAgent.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Encoding.Plugins.Tests.Support;

namespace Encoding.Plugins.Tests;

/// <summary>Store de plugins (0063) : paquet .zip, installation à chaud, mise à jour, dépôts, empreintes, plugins du worker.</summary>
internal static class StoreTests
{
    private sealed class Factory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    public static byte[] Package(string id, string version, Action<JsonObject>? tweak = null, Dictionary<string, string>? extra = null, bool withEntrypoint = true)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "backend", "tests", "Fixtures", "StoreCatalog", "example.json")))!.AsObject();
        manifest["id"] = id;
        manifest["version"] = version;
        foreach (var component in manifest["components"]!.AsArray()) component!["id"] = id.Replace('.', '-') + "-" + component["id"]!.GetValue<string>()["example-".Length..];
        manifest["runtime"] = new JsonObject { ["type"] = "python3", ["entrypoint"] = "main.py" };
        tweak?.Invoke(manifest);
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            void Add(string name, string content)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
            Add("plugin.json", manifest.ToJsonString());
            if (withEntrypoint) Add("main.py", "print('hello')\n");
            foreach (var (name, content) in extra ?? []) Add(name, content);
        }
        return memory.ToArray();
    }

    private static void Rejects(byte[] archive, string expected, string label)
    {
        try { PluginPackage.Inspect(archive); }
        catch (PluginPackageException exception) { Check(exception.Message.Contains(expected, StringComparison.OrdinalIgnoreCase), $"{label}: message « {exception.Message} »"); return; }
        throw new InvalidOperationException($"{label}: the package should have been refused.");
    }

    public static async Task RunAsync()
    {
        PackageValidation();
        await InstallLifecycle();
        await RepositoryRoundTrip();
        WorkerCatalog();
    }

    private static void PackageValidation()
    {
        var good = PluginPackage.Inspect(Package("acme.tools", "1.0.0"));
        Check(good.Manifest.Id == "acme.tools" && good.Runtime.Entrypoint == "main.py" && good.Sha256.Length == 64, "A valid package is inspected.");
        Rejects(new byte[] { 1, 2, 3 }, "zip", "not a zip");
        Rejects(Package("acme.tools", "1.0.0", withEntrypoint: false), "point d'entrée", "missing entrypoint");
        Rejects(Package("tools", "1.0.0"), "invalide", "id without namespace");
        Rejects(Package("acme.tools", "1.0.0", m => m["runtime"]!["type"] = "perl"), "runtime.type", "unknown runtime");
        Rejects(Package("acme.tools", "1.0.0", m => m.Remove("runtime")), "runtime", "missing runtime");
        Rejects(Package("acme.tools", "1.0.0", extra: new() { ["../escape.txt"] = "x" }), "refusé", "zip slip");
        Rejects(Package("acme.tools", "1.0.0", extra: new() { ["/etc/passwd"] = "x" }), "refusé", "absolute path");
        Rejects(Package("acme.tools", "1.0.0", m => m["runtime"]!["entrypoint"] = "../main.py"), "refusé", "entrypoint escape");
        var many = Enumerable.Range(0, PluginPackage.MaxEntries + 5).ToDictionary(i => $"f{i}.txt", _ => "x");
        Rejects(Package("acme.tools", "1.0.0", extra: many), "plus de", "too many files");
        var directory = Path.Combine(Path.GetTempPath(), "encoding-package-" + Guid.NewGuid().ToString("N"));
        try
        {
            PluginPackage.Extract(Package("acme.tools", "1.0.0", extra: new() { ["lib/helper.py"] = "x=1" }), directory);
            Check(File.Exists(Path.Combine(directory, "main.py")) && File.Exists(Path.Combine(directory, "lib", "helper.py")), "A package is extracted with its sub folders.");
            var escaped = false;
            try { PluginPackage.Extract(Package("acme.tools", "1.0.0", extra: new() { ["..\\..\\evil.txt"] = "x" }), Path.Combine(directory, "x")); }
            catch (PluginPackageException) { escaped = true; }
            Check(escaped && !File.Exists(Path.Combine(directory, "..", "evil.txt")), "Extraction never writes outside its folder.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        Console.WriteLine("PASS: plugin packages are validated and extracted safely (zip slip, runtime, entrypoint, namespace, limits).");
    }

    private static (PluginStoreService Store, FlowComponentRegistry Registry) Create(string directory)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PluginStore:Directory"] = directory,
            ["ToolStore:Directory"] = Path.Combine(directory, "tools")
        }).Build();
        var registry = StoreCatalogFixture.Create();
        var tools = new ToolStoreService(configuration, NullLogger<ToolStoreService>.Instance);
        return (new PluginStoreService(configuration, registry, tools, NullLogger<PluginStoreService>.Instance), registry);
    }

    private static async Task InstallLifecycle()
    {
        var directory = Path.Combine(Path.GetTempPath(), "encoding-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (store, registry) = Create(directory);
            var v1 = Package("acme.tools", "1.0.0");
            var installed = await store.ImportAsync(v1, "sideload");
            var plugin = registry.Plugins.Single(item => item.Id == "acme.tools");
            Check(installed.Version == "1.0.0" && plugin.Components.Count == 2, "An imported package is registered without restart.");
            var oldReference = registry.FindReference("acme-tools-tag-file", "acme.tools")!;
            Check(registry.Resolve(oldReference) is not null, "Its components resolve.");
            Check((await store.ImportAsync(v1, "sideload")).Sha256 == installed.Sha256, "Importing the same archive again is idempotent.");

            async Task<string> Refused(byte[] archive, string source = "sideload", bool official = false)
            {
                try { await store.ImportAsync(archive, source, allowOfficial: official); }
                catch (PluginPackageException exception) { return exception.Message; }
                return "";
            }
            Check((await Refused(Package("acme.tools", "1.0.0", m => m["name"] = "Modifié"))).Contains("immuable"), "A published version cannot be replaced by different content.");
            Check((await Refused(Package("official.sneaky", "1.0.0"))).Contains("official"), "The official namespace is reserved to the official repository.");
            Check((await Refused(Package("plex", "1.0.0"))).Length > 0 && (await Refused(Package("acme.tools", "0.9.0"))).Contains("plus récente"), "Downgrades and official historical ids are refused.");
            Check((await Refused(Package("acme.other", "1.0.0", m => m["components"]![0]!["id"] = "acme-tools-tag-file"))).Contains("existe déjà"), "A component id cannot shadow another plugin's component.");

            var v2 = Package("acme.tools", "1.1.0");
            await store.ImportAsync(v2, "sideload");
            Check(registry.Plugins.Single(item => item.Id == "acme.tools").Version == "1.1.0", "An update replaces the installed version.");
            Check(registry.Resolve(oldReference) is not null && registry.RuntimePluginVersion(oldReference) == "1.1.0", "Flows pinned to the previous version still resolve and run on the new one.");
            Check(!File.Exists(store.PackagePath("acme.tools", "1.0.0")) && File.Exists(store.PackagePath("acme.tools", "1.1.0")), "The superseded archive is deleted.");

            var (restarted, freshRegistry) = Create(directory);
            restarted.LoadInstalled();
            Check(freshRegistry.Plugins.Any(item => item.Id == "acme.tools" && item.Version == "1.1.0") && freshRegistry.Rejected.Count == 0, "Installed packages are reloaded at startup.");

            File.WriteAllBytes(restarted.PackagePath("acme.tools", "1.1.0"), Package("acme.tools", "1.1.0", m => m["name"] = "Tampered"));
            var (_, tamperedRegistry) = Create(directory);
            var tamperedConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PluginStore:Directory"] = directory,
                ["ToolStore:Directory"] = Path.Combine(directory, "tools")
            }).Build();
            var tamperedTools = new ToolStoreService(tamperedConfiguration, NullLogger<ToolStoreService>.Instance);
            var tamperedStore = new PluginStoreService(tamperedConfiguration, tamperedRegistry, tamperedTools, NullLogger<PluginStoreService>.Instance);
            tamperedStore.LoadInstalled();
            Check(tamperedRegistry.Rejected.Any(item => item.File.StartsWith("acme.tools@")) && tamperedRegistry.Plugins.All(item => item.Id != "acme.tools"), "A package altered on disk is reported and not loaded.");

            Check(await store.RemoveAsync("acme.tools") && registry.Plugins.All(item => item.Id != "acme.tools") && registry.Resolve(oldReference) is null, "Removing a package unregisters it.");
            Check(registry.Uninstall("plex") && registry.Plugins.All(item => item.Id != "plex"), "A Store-installed plugin can be uninstalled.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        Console.WriteLine("PASS: store lifecycle (hot install, idempotence, immutability, namespaces, update compatibility, reload, tamper detection, removal).");
    }

    private static async Task RepositoryRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "encoding-repository-" + Guid.NewGuid().ToString("N"));
        var archive = Package("acme.tools", "1.2.0");
        var sha = PluginPackage.Hash(archive);
        var served = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        string baseUrl = "";
        app.MapGet("/repository.json", () => Results.Json(new
        {
            schemaVersion = "1.0.0", name = "Dépôt test", description = "d",
            categories = new[] { new { id = "utilities", name = "Utilitaires" } },
            plugins = new object[]
            {
                new { id = "acme.tools", name = "Acme", summary = "Outils", category = "utilities", author = "Acme", latestVersion = "1.2.0", requiredTools = new[] { "ffmpeg" },
                    versions = new[] { new { version = "1.2.0", downloadUrl = baseUrl + "/acme-1.2.0.zip", sha256 = sha, releaseDate = "2026-10-15T12:00:00Z", changelog = "c" } } },
                new { id = "acme.bad-hash", name = "Bad", summary = "", category = "utilities", latestVersion = "1.0.0",
                    versions = new[] { new { version = "1.0.0", downloadUrl = baseUrl + "/acme-1.2.0.zip", sha256 = new string('0', 64), releaseDate = (string?)null, changelog = (string?)null } } },
                new { id = "official.fake", name = "Usurpation", summary = "", category = "utilities", latestVersion = "1.0.0",
                    versions = new[] { new { version = "1.0.0", downloadUrl = baseUrl + "/acme-1.2.0.zip", sha256 = sha, releaseDate = (string?)null, changelog = (string?)null } } },
                new { id = "no-namespace", name = "x", summary = "", category = "utilities", latestVersion = "1.0.0", versions = Array.Empty<object>() }
            }
        }));
        app.MapGet("/acme-1.2.0.zip", () => { served++; return Results.File(archive, "application/zip"); });
        app.MapGet("/broken.json", () => Results.Text("{ nope", "application/json"));
        app.MapGet("/future.json", () => Results.Json(new { schemaVersion = "2.0.0", plugins = Array.Empty<object>() }));
        await app.StartAsync();
        baseUrl = app.Urls.Single();
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PluginStore:Directory"] = directory,
                ["ToolStore:Directory"] = Path.Combine(directory, "tools")
            }).Build();
            var registry = StoreCatalogFixture.Create();
            var tools = new ToolStoreService(configuration, NullLogger<ToolStoreService>.Instance);
            var store = new PluginStoreService(configuration, registry, tools, NullLogger<PluginStoreService>.Instance);
            var repositories = new PluginRepositoryService(configuration, new Factory(), store, tools, NullLogger<PluginRepositoryService>.Instance);

            async Task<string> Fails(Func<Task> action)
            {
                try { await action(); }
                catch (PluginRepositoryException exception) { return exception.Message; }
                return "";
            }
            Check((await Fails(() => repositories.AddAsync("ftp://x/y.json", CancellationToken.None))).Contains("HTTP"), "Only HTTP(S) repositories are accepted.");
            Check((await Fails(() => repositories.AddAsync("https://user:pw@example.com/r.json", CancellationToken.None))).Contains("identifiant"), "Credentials in a repository URL are refused.");
            Check((await Fails(() => repositories.AddAsync(baseUrl + "/broken.json", CancellationToken.None))).Contains("JSON"), "An unreadable index is refused.");
            Check((await Fails(() => repositories.AddAsync(baseUrl + "/future.json", CancellationToken.None))).Contains("schéma"), "An unsupported schema major version is refused.");
            Check(repositories.List().Count == 0, "A failed repository is not saved.");

            var added = await repositories.AddAsync(baseUrl + "/repository.json", CancellationToken.None);
            Check(added.Name == "Dépôt test" && added.Packages.Select(p => p.Id).SequenceEqual(["acme.tools", "acme.bad-hash"]), "Invalid and impersonating entries are dropped, valid ones listed.");
            Check(added.Packages[0].CategoryLabel == "Utilitaires" && added.LastError is not null, "Categories are resolved and ignored entries are reported.");
            Check((await Fails(() => repositories.AddAsync(baseUrl + "/repository.json", CancellationToken.None))).Contains("déjà"), "A repository is added once.");

            Check((await Fails(() => repositories.InstallAsync(added.Id, "acme.bad-hash", null, CancellationToken.None))).Contains("SHA-256"), "An archive whose hash differs from the index is refused.");
            Check(registry.Plugins.All(p => p.Id != "acme.bad-hash") && !Directory.Exists(Path.Combine(directory, "packages", "acme.bad-hash")), "Nothing is installed after a hash mismatch.");
            var installed = await repositories.InstallAsync(added.Id, "acme.tools", null, CancellationToken.None);
            Check(installed.Version == "1.2.0" && installed.Source == "repo:" + added.Id && registry.Plugins.Any(p => p.Id == "acme.tools") && served == 2, "A package is downloaded, verified and installed from a repository.");
            await repositories.SetEnabledAsync(added.Id, false, CancellationToken.None);
            Check((await Fails(() => repositories.InstallAsync(added.Id, "acme.tools", null, CancellationToken.None))).Contains("désactivé"), "A disabled repository cannot install.");
            Check(await repositories.RemoveAsync(added.Id, CancellationToken.None) && repositories.List().Count == 0, "A repository can be removed.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        Console.WriteLine("PASS: repositories (index validation, impersonation, SHA-256 check, install, disable, removal) against a local HTTP server.");
    }

    private static void WorkerCatalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "encoding-custom-" + Guid.NewGuid().ToString("N"));
        try
        {
            var archive = Package("acme.tools", "1.0.0");
            var target = Path.Combine(root, "acme.tools", "1.0.0");
            PluginPackage.Extract(archive, target);
            Check(CustomPluginCatalog.Scan(root).Count == 0, "A package without the verification marker is ignored.");
            File.WriteAllText(Path.Combine(target, CustomPluginCatalog.VerifiedMarker), PluginPackage.Hash(archive));
            var launches = CustomPluginCatalog.Scan(root);
            if (ProcessToolRunner.Find("python3") is null) { Console.WriteLine("SKIP: python3 absent, worker plugin launch not checked."); return; }
            var launch = launches.Single();
            Check(launch.Id == "acme.tools" && launch.Version == "1.0.0" && launch.Arguments.SequenceEqual(["-B", Path.Combine(target, "main.py")]) && launch.ReadOnlyPaths.SequenceEqual([target]),
                "A verified package becomes a sandboxed launch of its runtime restricted to its own folder.");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Runtime:CustomPluginDirectory"] = root }).Build();
            var host = new PluginProcessHost(new ProcessToolRunner(), configuration, new NodeAgentOptions("t", [Encoding.Contracts.Nodes.NodeRole.Worker], 1, Path.Combine(root, "node"), null, false, TimeSpan.FromSeconds(10), 8081));
            Check(host.Installed.Any(item => item.Id == "acme.tools"), "The worker host lists the downloaded plugins.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("PASS: the worker discovers verified Store packages and ignores incomplete ones.");
    }
}
