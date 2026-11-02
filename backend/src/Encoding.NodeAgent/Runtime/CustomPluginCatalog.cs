using System.Text.Json;
using Encoding.Core.Plugins;

namespace Encoding.NodeAgent.Runtime;

/// <summary>
/// Plugins du Store déjà téléchargés et vérifiés sur ce worker : <c>&lt;dossier&gt;/&lt;id&gt;/&lt;version&gt;/</c> contenant le paquet extrait
/// et un fichier <c>.verified</c> écrit en dernier (son absence signale une extraction interrompue). Le lancement est déduit
/// de <c>plugin.json</c> (runtime) : les mêmes restrictions Landlock que pour un plugin déclaré à la main s'appliquent.
/// </summary>
public static class CustomPluginCatalog
{
    public const string VerifiedMarker = ".verified";
    public const string DefaultDirectory = "/opt/encoding/custom-plugins";

    public static string DirectoryFor(IConfiguration configuration) =>
        configuration["Runtime:CustomPluginDirectory"] is { Length: > 0 } configured ? configured : DefaultDirectory;

    public static IReadOnlyList<PluginLaunch> Scan(string root)
    {
        var result = new List<PluginLaunch>();
        if (!Directory.Exists(root)) return result;
        foreach (var pluginDirectory in Directory.EnumerateDirectories(root))
            foreach (var versionDirectory in Directory.EnumerateDirectories(pluginDirectory))
            {
                try
                {
                    if (!File.Exists(Path.Combine(versionDirectory, VerifiedMarker))) continue;
                    var launch = Launch(versionDirectory);
                    if (launch is not null) result.Add(launch);
                }
                catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { }
            }
        return result;
    }

    public static PluginLaunch? Launch(string versionDirectory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(versionDirectory, PluginPackage.ManifestFile)));
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var id) || !root.TryGetProperty("version", out var version) || !root.TryGetProperty("runtime", out var runtime)) return null;
        var type = runtime.GetProperty("type").GetString();
        if (type == "native")
        {
            if (!PluginPackage.NativeComponentPluginIds.Contains(id.GetString() ?? "")) return null;
            return new PluginLaunch { Id = id.GetString() ?? "", Version = version.GetString() ?? "", IsNative = true,
                ReadOnlyPaths = [Path.GetFullPath(versionDirectory)] };
        }
        var entrypoint = Path.GetFullPath(Path.Combine(versionDirectory, PluginPackage.SafeRelativePath(runtime.GetProperty("entrypoint").GetString() ?? "")));
        var directory = Path.GetFullPath(versionDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!entrypoint.StartsWith(directory, StringComparison.Ordinal) || !File.Exists(entrypoint)) return null;
        var (executable, arguments) = type switch
        {
            "python3" => (ProcessToolRunner.Find("python3"), new[] { "-B", entrypoint }),
            "node" => (ProcessToolRunner.Find("node"), new[] { entrypoint }),
            _ => (null, Array.Empty<string>())
        };
        return executable is null ? null : new PluginLaunch
        {
            Id = id.GetString() ?? "", Version = version.GetString() ?? "", Executable = executable, Arguments = arguments,
            ReadOnlyPaths = [Path.GetFullPath(versionDirectory)]
        };
    }
}
