using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Contracts.Plugins;
using Microsoft.Extensions.Configuration;

namespace Encoding.Plugins.Tests;

/// <summary>Explicit test fixture for Store manifests; these descriptions are not loaded by production.</summary>
internal static class StoreCatalogFixture
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Native = new(StringComparer.Ordinal)
        { "base", "nodes", "conditions", "video", "audio", "subtitles", "mkv", "images", "probe", "ai" };

    public static FlowComponentRegistry Create(IConfiguration? configuration = null)
    {
        var registry = new FlowComponentRegistry(configuration ?? new ConfigurationBuilder().Build());
        var directory = Path.Combine(Support.RepositoryRoot(), "backend", "tests", "Fixtures", "StoreCatalog");
        // example.json is the separately embedded third-party example used by plugin tests,
        // not a package from the Store catalog.
        foreach (var file in Directory.EnumerateFiles(directory, "*.json")
                     .Where(file => !string.Equals(Path.GetFileName(file), "example.json", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(file), Json)
                ?? throw new InvalidOperationException($"Manifeste de fixture illisible : {Path.GetFileName(file)}");
            if (!registry.TryInstall(manifest, out var reason, Native.Contains(manifest.Id)))
                throw new InvalidOperationException($"Manifeste de fixture refusé : {Path.GetFileName(file)} — {reason}");
        }
        return registry;
    }
}
