using System.Text.Json;
using Encoding.Contracts.Tools;
using Encoding.Core.Tools;

namespace Encoding.NodeAgent.Runtime;

/// <summary>Résout les rôles fournis par les paquets du Store déjà vérifiés sur ce nœud.</summary>
public static class ManagedToolCatalog
{
    public const string DefaultDirectory = "/opt/encoding/managed-tools";
    public const string DirectoryEnvironmentVariable = "ENCODING_MANAGED_TOOLS_DIR";
    private const string Marker = ".verified";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Root => Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable) is { Length: > 0 } configured
        ? configured : DefaultDirectory;

    public static IReadOnlyList<string> PackageDirectories()
    {
        if (!Directory.Exists(Root)) return [];
        return Directory.EnumerateFiles(Root, Marker, SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName).Where(path => path is not null).Cast<string>().ToArray();
    }

    public static IReadOnlyList<string> ExecutableDirectories()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in PackageDirectories())
        {
            try
            {
                if (VerifiedHash(directory) is null) continue;
                var manifest = JsonSerializer.Deserialize<ToolPackageManifest>(File.ReadAllText(Path.Combine(directory, ToolPackageArchive.ManifestFile)), Json);
                if (manifest?.Platform != ToolPackagePlatform.Current) continue;
                foreach (var executable in manifest.Executables)
                    result.Add(Path.GetDirectoryName(Path.GetFullPath(Path.Combine(directory, executable.Path))) ?? directory);
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
        }
        return result.ToArray();
    }

    public static string? Find(string toolId)
    {
        foreach (var directory in PackageDirectories())
        {
            try
            {
                if (VerifiedHash(directory) is null) continue;
                var manifest = JsonSerializer.Deserialize<ToolPackageManifest>(File.ReadAllText(Path.Combine(directory, ToolPackageArchive.ManifestFile)), Json);
                if (manifest?.Platform != ToolPackagePlatform.Current) continue;
                var executable = manifest?.Executables.SingleOrDefault(item => item.ToolId == toolId);
                if (executable is null) continue;
                var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var path = Path.GetFullPath(Path.Combine(root, executable.Path));
                if (path.StartsWith(root, StringComparison.Ordinal) && File.Exists(path)) return path;
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
        }
        return null;
    }

    public static string? PackageSha256ForCapability(string toolId)
    {
        var roles = toolId switch
        {
            "mkvtoolnix" => new[] { "mkvmerge", "mkvextract", "mkvpropedit" },
            "imagemagick" => new[] { "magick", "convert" },
            "python" => new[] { "python3", "python" },
            "javascript" => new[] { "node" },
            "codex-cli" => new[] { "codex", "codex-cli" },
            "claude-cli" => new[] { "claude", "claude-cli" },
            _ => new[] { toolId }
        };
        foreach (var directory in PackageDirectories())
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<ToolPackageManifest>(File.ReadAllText(Path.Combine(directory, ToolPackageArchive.ManifestFile)), Json);
                if (manifest?.Platform == ToolPackagePlatform.Current
                    && manifest.Executables.Any(item => roles.Contains(item.ToolId, StringComparer.Ordinal)))
                    return VerifiedHash(directory);
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        return null;
    }

    public static bool Satisfies(PluginToolDependency dependency)
    {
        foreach (var directory in PackageDirectories())
        {
            try
            {
                if (VerifiedHash(directory) is null) continue;
                var manifest = JsonSerializer.Deserialize<ToolPackageManifest>(File.ReadAllText(Path.Combine(directory, ToolPackageArchive.ManifestFile)), Json);
                if (manifest?.Id != dependency.ToolPackageId || manifest.Platform != ToolPackagePlatform.Current
                    || !VersionSatisfies(manifest.Version, dependency.MinimumVersion, dependency.MaximumVersionExclusive)) continue;
                var capabilities = (manifest.Capabilities ?? []).ToHashSet(StringComparer.Ordinal);
                if ((dependency.RequiredCapabilities ?? []).All(capabilities.Contains)) return true;
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return false;
    }

    private static bool VersionSatisfies(string actualText, string minimumText, string? maximumExclusive)
    {
        static bool Parse(string input, out Version version) => Version.TryParse(input, out version!);
        if (!Parse(actualText, out var actual) || !Parse(minimumText, out var minimum) || actual < minimum) return false;
        return maximumExclusive is null || Parse(maximumExclusive, out var maximum) && actual < maximum;
    }

    private static string? VerifiedHash(string directory)
    {
        try
        {
            var sha = File.ReadAllText(Path.Combine(directory, Marker)).Trim();
            return System.Text.RegularExpressions.Regex.IsMatch(sha, "^[0-9a-f]{64}$") ? sha : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }
}
