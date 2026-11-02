using System.Text.Json;
using System.Text.Json.Nodes;
using Encoding.Api.Flows;
using Encoding.Api.Plugins;
using Encoding.Contracts.Plugins;
using Microsoft.Extensions.Configuration;
using static Encoding.Plugins.Tests.Support;

namespace Encoding.Plugins.Tests;

internal static class PluginTests
{
    public static void Run()
    {
        PathMappings();
        WebhookPayloads();
        CatalogAcceptsManifests();
        InvalidManifestsAreRejected();
        WebhookThrottleBlocksRepeatedFailures();
        ServerOnlySettingsStayOffWorkers();
        SandboxedProcessesGetAMinimalEnvironment();
        ManifestFingerprintDetectsContractDrift();
    }

    private static void PathMappings()
    {
        // Les vecteurs sont partagés avec les tests Python : les deux implémentations doivent concorder.
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "backend", "tests", "Fixtures", "path-mapping-vectors.json")));
        var root = vectors.RootElement;
        var libraries = root.GetProperty("libraries").EnumerateObject().ToDictionary(item => item.Name, item => Guid.Parse(item.Value.GetString()!));
        string Fill(string text) => libraries.Aggregate(text, (current, item) => current.Replace("{" + item.Key + "}", item.Value.ToString()));
        var mappings = LibraryPathMapping.Parse(Fill(root.GetProperty("mappings").GetString()!));
        Check(mappings.ToDictionary(item => item.LibraryId, item => item.RemoteRoot).SequenceEqual(
            root.GetProperty("roots").EnumerateObject().ToDictionary(item => libraries[item.Name], item => item.Value.GetString()!)), "Roots are normalized.");
        foreach (var vector in root.GetProperty("toRemote").EnumerateArray())
            Check(LibraryPathMapping.ToRemote(mappings, libraries[vector.GetProperty("library").GetString()!], vector.GetProperty("relative").GetString()!) == vector.GetProperty("remote").GetString(),
                "A library file maps to the service path.");
        Check(LibraryPathMapping.ToRemote(mappings, Guid.NewGuid(), "a.mkv") is null, "An unmapped library has no remote path.");
        foreach (var invalid in root.GetProperty("invalid").EnumerateArray())
        {
            var rejected = false;
            try { LibraryPathMapping.Parse(Fill(invalid.GetString()!)); } catch (FormatException) { rejected = true; }
            Check(rejected, "Invalid mapping rejected: " + invalid.GetString());
        }

        var tv = libraries["tv"];
        var movies = libraries["movies"];
        Check(LibraryPathMapping.ToLibrary(mappings, "/tv/Show/S01/e1.mkv") == (tv, "Show/S01/e1.mkv"), "A remote file maps to its library and relative path.");
        Check(LibraryPathMapping.ToLibrary(mappings, "/mnt/movies/4k/Film/film.mkv") == (movies, "Film/film.mkv"), "Windows separators are accepted.");
        Check(LibraryPathMapping.ToLibrary(mappings, "/tvshows/e1.mkv") is null, "A sibling directory is not inside the root.");
        Check(LibraryPathMapping.ToLibrary(mappings, "/tv/../etc/passwd") is null, "Traversal is rejected.");
        Check(LibraryPathMapping.ToLibrary(mappings, "/tv") is null, "The root itself is not a file.");
        var nested = LibraryPathMapping.Parse($"{tv}=/media\n{movies}=/media/movies");
        Check(LibraryPathMapping.ToLibrary(nested, "/media/movies/a.mkv")!.Value.LibraryId == movies, "The longest root wins.");
        Console.WriteLine("PASS: library path mappings (shared vectors, longest root, traversal, duplicates).");
    }

    private static void WebhookPayloads()
    {
        static ArrWebhookEvent Parse(string plugin, string json) { using var d = JsonDocument.Parse(json); return ArrWebhookEvent.Parse(plugin, d.RootElement); }
        Check(Parse("sonarr", """{"eventType":"Test"}""").Kind == ArrWebhookKind.Test, "Test events are recognised.");
        var sonarr = Parse("sonarr", """{"eventType":"Download","series":{"path":"/tv/Show"},"episodeFile":{"path":"/tv/Show/S01/e1.mkv"},"episodeFiles":[{"relativePath":"S01/e2.mkv"},{"path":"/tv/Show/S01/e1.mkv"}]}""");
        Check(sonarr.Kind == ArrWebhookKind.Import && sonarr.RemotePaths.SequenceEqual(["/tv/Show/S01/e1.mkv", "/tv/Show/S01/e2.mkv"]), "Sonarr paths are deduplicated and completed from the series folder.");
        var radarr = Parse("radarr", """{"eventType":"Download","isUpgrade":true,"movie":{"folderPath":"/movies/Film"},"movieFile":{"relativePath":"film.mkv"}}""");
        Check(radarr.Kind == ArrWebhookKind.Import && radarr.RemotePaths.Single() == "/movies/Film/film.mkv", "Radarr paths are completed from the movie folder.");
        Check(Parse("radarr", """{"eventType":"Grab"}""").Kind == ArrWebhookKind.Ignored, "Other events are ignored.");
        Check(Parse("sonarr", """{"eventType":"Download"}""").Kind == ArrWebhookKind.Ignored, "An import without a file is ignored.");
        var invalid = false;
        try { Parse("sonarr", "[]"); } catch (FormatException) { invalid = true; }
        Check(invalid, "A non-event body is rejected.");
        Console.WriteLine("PASS: Sonarr/Radarr webhook payload parsing.");
    }

    /// <summary>Le registre charge les manifestes de fixture du Store et celui d’un plugin tiers copié de l’exemple.</summary>
    private static void CatalogAcceptsManifests()
    {
        var plugins = Path.Combine(RepositoryRoot(), "backend", "tests", "Fixtures", "StoreCatalog");
        var directory = Path.Combine(Path.GetTempPath(), "encoding-plugin-manifests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.Copy(Path.Combine(plugins, "example.json"), Path.Combine(directory, "example.json"));
            var registry = StoreCatalogFixture.Create(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FlowPlugins:ManifestDirectory"] = directory }).Build());
            Check(registry.Plugins.Single(plugin => plugin.Id == "example").Components.Count == 2, "The server catalog accepts the third-party manifest.");
            Check(registry.Plugins.Where(plugin => plugin.Id != "example").SelectMany(plugin => plugin.Components)
                .Where(component => component.Category is not null)
                .All(component => FlowComponentCategories.Ids.Contains(component.Category!)),
                "Every declared Store component category is recognized.");
            foreach (var id in new[] { "sonarr", "radarr" })
            {
                var plugin = registry.Plugins.Single(item => item.Id == id);
                Check(plugin.ConfigurationSchema!.Fields.Any(field => field.Id == "webhookSecret" && field.Type == "secret")
                    && plugin.ConfigurationSchema.Fields.Any(field => field.Id == "pathMappings"), id + " exposes the trigger settings.");
            }
            foreach (var id in new[] { "plex", "jellyfin" })
                Check(registry.Plugins.Single(item => item.Id == id).ConfigurationSchema!.Fields.Any(field => field.Id == "pathMappings"), id + " exposes the path mapping.");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("PASS: store catalog fixtures and third-party plugin manifests are accepted.");
    }

    private static void InvalidManifestsAreRejected()
    {
        var source=Path.Combine(RepositoryRoot(),"backend","tests","Fixtures","StoreCatalog","example.json");
        var root=JsonNode.Parse(File.ReadAllText(source))!.AsObject();
        var duplicate=root.DeepClone().AsObject();
        duplicate["components"]!.AsArray().Add(duplicate["components"]![0]!.DeepClone());
        var unsupported=root.DeepClone().AsObject();
        unsupported["supportedHostContractVersions"] = new JsonArray(2);
        var badId=root.DeepClone().AsObject();
        badId["id"]="../evil";
        var shadowsBuiltIn=root.DeepClone().AsObject();
        shadowsBuiltIn["id"]="second-example";
        var badVersion=root.DeepClone().AsObject();
        badVersion["version"]="latest";
        var badSchema=root.DeepClone().AsObject();
        badSchema["components"]![0]!["configurationSchema"]!["fields"]![0]!["type"]="telepathy";
        var badCategory=root.DeepClone().AsObject();
        badCategory["components"]![0]!["category"]="miscellaneous";
        var cases=new Dictionary<string,string>
        {
            ["unsupported host contract"]=unsupported.ToJsonString(),
            ["duplicate component identity"]=duplicate.ToJsonString(),
            ["unsafe plugin id"]=badId.ToJsonString(),
            ["id of an already installed plugin"]=shadowsBuiltIn.ToJsonString(),
            ["non semantic version"]=badVersion.ToJsonString(),
            ["unknown field type"]=badSchema.ToJsonString(),
            ["unknown component category"]=badCategory.ToJsonString(),
            ["unreadable JSON"]="{ not json"
        };

        foreach(var (name,manifest) in cases)
        {
            var directory=Path.Combine(Path.GetTempPath(),"encoding-plugin-invalid-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory,"invalid.json"),manifest);
                var good=JsonNode.Parse(File.ReadAllText(source))!.AsObject();
                good["id"]="second-example";
                foreach(var component in good["components"]!.AsArray()) component!["id"]=component["id"]!.GetValue<string>().Replace("example-","second-");
                File.WriteAllText(Path.Combine(directory,"a-valid.json"),good.ToJsonString());
                var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["FlowPlugins:ManifestDirectory"]=directory }).Build();
                var registry=StoreCatalogFixture.Create(configuration);
                Check(registry.Rejected.Count==1 && registry.Rejected[0].File=="invalid.json" && registry.Rejected[0].Reason.Length>0,"The catalog reports the invalid manifest without failing: "+name);
                Check(registry.Plugins.All(plugin=>plugin.Id!="../evil") && registry.Plugins.Any(plugin=>plugin.Id=="second-example"),"A broken manifest does not hide a valid one: "+name);
                Check(registry.Plugins.Count(plugin=>plugin.Id=="second-example")==1,"A third-party manifest never replaces an installed plugin: "+name);
            }
            finally { Directory.Delete(directory,true); }
        }
        Console.WriteLine("PASS: an invalid third-party manifest is reported and skipped, never fatal.");
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static void WebhookThrottleBlocksRepeatedFailures()
    {
        var clock = new ManualClock();
        var throttle = new WebhookAuthThrottle(clock);
        for (var i = 0; i < WebhookAuthThrottle.MaxFailures - 1; i++) throttle.RecordFailure("sonarr|10.0.0.1");
        Check(!throttle.IsBlocked("sonarr|10.0.0.1"), "Below the limit the caller is still served.");
        throttle.RecordFailure("sonarr|10.0.0.1");
        Check(throttle.IsBlocked("sonarr|10.0.0.1"), "At the limit the caller is blocked.");
        Check(!throttle.IsBlocked("sonarr|10.0.0.2") && !throttle.IsBlocked("radarr|10.0.0.1"), "Other callers and plugins are unaffected.");
        clock.Now += WebhookAuthThrottle.Window + TimeSpan.FromSeconds(1);
        Check(!throttle.IsBlocked("sonarr|10.0.0.1"), "The block ends with the window.");
        Console.WriteLine("PASS: webhook authentication failures are throttled per caller and plugin.");
    }

    private static void ServerOnlySettingsStayOffWorkers()
    {
        var registry = StoreCatalogFixture.Create();
        var sonarr = registry.Plugins.Single(plugin => plugin.Id == "sonarr");
        Check(sonarr.ConfigurationSchema!.Fields.Single(field => field.Id == "webhookSecret").Scope == "server", "The webhook secret is declared server-only.");
        var values = new Dictionary<string, JsonElement>
        {
            ["baseUrl"] = JsonSerializer.SerializeToElement("http://sonarr"),
            ["webhookSecret"] = JsonSerializer.SerializeToElement("s3cret")
        };
        var filtered = registry.ForWorker(new Dictionary<string, IReadOnlyDictionary<string, JsonElement>> { ["sonarr"] = values, ["unknown"] = values });
        Check(filtered["sonarr"].ContainsKey("baseUrl") && !filtered["sonarr"].ContainsKey("webhookSecret"), "The worker receives the connection but not the webhook secret.");
        Check(filtered["unknown"].ContainsKey("webhookSecret"), "A plugin without a declared scope is passed through unchanged.");
        Console.WriteLine("PASS: server-only plugin settings are removed before reaching a worker.");
    }

    private static void SandboxedProcessesGetAMinimalEnvironment()
    {
        var current = new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin", ["LANG"] = "fr_FR.UTF-8",
            ["ConnectionStrings__Cluster"] = "Host=db;Password=hunter2", ["Node__Token"] = "t0ken", ["ANTHROPIC_API_KEY"] = "sk-x", ["HOME"] = "/root"
        };
        var minimal = Encoding.Core.Runtime.ProcessSandbox.MinimalEnvironment(current, "/work");
        Check(minimal["PATH"] == "/usr/bin" && minimal["LANG"] == "fr_FR.UTF-8", "Locale and PATH are kept.");
        Check(minimal["HOME"] == "/work" && minimal["TMPDIR"] == "/work", "HOME and TMPDIR point to the workspace.");
        Check(!minimal.Keys.Any(name => name.Contains("Connection") || name.Contains("Token") || name.Contains("KEY")), "No secret or node setting reaches the sandboxed process.");
        Check(Encoding.Core.Runtime.ProcessSandbox.MinimalEnvironment(new Dictionary<string, string?>(), "/work")["PATH"].Contains("/usr/bin"), "A default PATH is supplied.");
        Console.WriteLine("PASS: sandboxed plugins and scripts receive an allow-listed environment.");
    }

    private static void ManifestFingerprintDetectsContractDrift()
    {
        var registry = StoreCatalogFixture.Create();
        var plex = registry.Plugins.Single(plugin => plugin.Id == "plex");
        var fingerprint = PluginManifestFingerprint.Compute(plex);
        var relabelled = plex with { Name = "Autre libellé", Components = plex.Components.Select(c => c with { Name = c.Name + " (v2)", Description = "x", Category = "utilities" }).ToArray() };
        Check(PluginManifestFingerprint.Compute(relabelled) == fingerprint, "Cosmetic changes do not alter the contract fingerprint.");
        var firstOutput = plex.Components.First(c => c.Outputs.Count > 0);
        var drifted = plex with
        {
            Components = plex.Components.Select(c => c.Id != firstOutput.Id ? c : c with { Outputs = c.Outputs.Skip(1).ToArray() }).ToArray()
        };
        Check(PluginManifestFingerprint.Compute(drifted) != fingerprint, "A removed output changes the fingerprint.");
        Check(registry.FingerprintMatches("plex", fingerprint), "The server accepts the fingerprint of its own catalog.");
        Check(!registry.FingerprintMatches("plex", PluginManifestFingerprint.Compute(drifted)), "The server refuses a worker whose same-version manifest differs.");
        Check(registry.FingerprintMatches("plex", null), "A worker that announces no fingerprint stays accepted during a rolling upgrade.");
        Check(registry.FingerprintMatches("not-installed", "anything"), "An unknown plugin is not judged by the fingerprint.");
        Console.WriteLine("PASS: the manifest fingerprint detects same-version contract drift between server and worker.");
    }
}
