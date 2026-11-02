using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Contracts.Plugins;

namespace Encoding.Core.Plugins;

/// <summary>Paquet refusé : le message est destiné à l'administrateur qui l'importe.</summary>
public sealed class PluginPackageException(string message) : Exception(message);

/// <summary>Runtime déclaré par <c>plugin.json</c> : <c>python3</c> ou <c>node</c> avec un point d'entrée, ou <c>native</c> sans processus pour une famille de composants moteur autorisée.</summary>
public sealed record PluginRuntimeSpec(string Type, string Entrypoint);

public sealed record PluginPackageInfo(PluginManifest Manifest, PluginRuntimeSpec Runtime, string Sha256, int Entries, long UncompressedBytes);

/// <summary>
/// Paquet de plugin <c>.zip</c> (0063) : <c>plugin.json</c> à la racine (un <see cref="PluginManifest"/> étendu de
/// <c>runtime</c>), code et assets. L'inspection ne lit que les métadonnées ; l'extraction refuse tout chemin qui sortirait
/// du dossier cible et borne le nombre et la taille des fichiers (archive piégée).
/// </summary>
public static class PluginPackage
{
    public const string ManifestFile = "plugin.json";
    public const long MaxArchiveBytes = 32L * 1024 * 1024;
    public const long MaxUncompressedBytes = 128L * 1024 * 1024;
    public const int MaxEntries = 500;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Runtimes = ["python3", "node", "native"];
    public static readonly IReadOnlySet<string> NativeComponentPluginIds = new HashSet<string>(StringComparer.Ordinal)
        { "base", "nodes", "conditions", "video", "audio", "subtitles", "mkv", "images", "probe", "ai" };
    public static readonly IReadOnlySet<string> HistoricalOfficialPluginIds = new HashSet<string>(StringComparer.Ordinal)
        { "plex", "sonarr", "radarr", "jellyfin" };

    public static string Hash(byte[] archive) => Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();

    public static PluginPackageInfo Inspect(byte[] archive)
    {
        if (archive.Length == 0 || archive.Length > MaxArchiveBytes) throw new PluginPackageException($"L'archive doit peser entre 1 octet et {MaxArchiveBytes / 1024 / 1024} Mo.");
        using var zip = Open(archive);
        var entries = zip.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToList();
        if (entries.Count > MaxEntries) throw new PluginPackageException($"L'archive contient plus de {MaxEntries} fichiers.");
        var total = 0L;
        foreach (var entry in zip.Entries)
        {
            SafeRelativePath(entry.FullName);
            total += entry.Length;
            if (total > MaxUncompressedBytes) throw new PluginPackageException($"L'archive décompressée dépasse {MaxUncompressedBytes / 1024 / 1024} Mo.");
        }
        var manifestEntry = zip.GetEntry(ManifestFile) ?? throw new PluginPackageException("plugin.json est absent à la racine de l'archive.");
        if (manifestEntry.Length > 1024 * 1024) throw new PluginPackageException("plugin.json dépasse 1 Mo.");
        byte[] raw;
        using (var stream = manifestEntry.Open())
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            raw = memory.ToArray();
        }
        PluginManifest manifest;
        PluginRuntimeSpec runtime;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(raw, Json) ?? throw new PluginPackageException("plugin.json est vide.");
            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("runtime", out var runtimeElement) || runtimeElement.ValueKind != JsonValueKind.Object)
                throw new PluginPackageException("plugin.json doit déclarer « runtime » ({ \"type\": \"python3\" | \"node\", \"entrypoint\": \"main.py\" }, ou { \"type\": \"native\", \"entrypoint\": \"\" }).");
            runtime = new PluginRuntimeSpec(
                runtimeElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString()! : "",
                runtimeElement.TryGetProperty("entrypoint", out var entry) && entry.ValueKind == JsonValueKind.String ? entry.GetString()! : "");
        }
        catch (JsonException exception) { throw new PluginPackageException($"plugin.json est illisible : {exception.Message}"); }
        if (!Runtimes.Contains(runtime.Type)) throw new PluginPackageException($"runtime.type « {runtime.Type} » non pris en charge ({string.Join(", ", Runtimes)}).");
        if (runtime.Type == "native")
        {
            if (!NativeComponentPluginIds.Contains(manifest.Id) || !string.IsNullOrEmpty(runtime.Entrypoint))
                throw new PluginPackageException("Le runtime natif est réservé aux familles de composants moteur autorisées et ne déclare pas de point d'entrée.");
        }
        else
        {
            var entrypoint = SafeRelativePath(runtime.Entrypoint);
            if (entrypoint.Length == 0 || zip.GetEntry(entrypoint) is null) throw new PluginPackageException($"Le point d'entrée « {runtime.Entrypoint} » est absent de l'archive.");
            runtime = runtime with { Entrypoint = entrypoint };
        }
        if (manifest.Id is null || (runtime.Type == "native"
                ? !NativeComponentPluginIds.Contains(manifest.Id)
                : !PluginIdentity.IsQualifiedPluginId(manifest.Id) && !HistoricalOfficialPluginIds.Contains(manifest.Id)))
            throw new PluginPackageException($"Identifiant « {manifest.Id} » invalide pour le runtime {runtime.Type}.");
        var toolDependencies = manifest.RequiredToolPackages ?? [];
        if (toolDependencies.Any(item => item is null || string.IsNullOrWhiteSpace(item.ToolPackageId)
            || !System.Text.RegularExpressions.Regex.IsMatch(item.ToolPackageId, "^[a-z0-9][a-z0-9._-]{0,127}$")
            || string.IsNullOrWhiteSpace(item.MinimumVersion)
            || !System.Text.RegularExpressions.Regex.IsMatch(item.MinimumVersion, @"^\d+\.\d+\.\d+$")))
            throw new PluginPackageException("Chaque dépendance d'outil doit déclarer un identifiant de paquet et une version minimale valide.");
        if (toolDependencies.Select(item => item.ToolPackageId).Distinct(StringComparer.Ordinal).Count() != toolDependencies.Count)
            throw new PluginPackageException("Une dépendance vers un même paquet d'outil ne peut apparaître qu'une fois.");
        foreach (var dependency in toolDependencies)
            if (dependency.MaximumVersionExclusive is { } maximum
                && !System.Text.RegularExpressions.Regex.IsMatch(maximum, @"^\d+\.\d+\.\d+$"))
                throw new PluginPackageException($"Version maximale invalide pour l'outil « {dependency.ToolPackageId} ».");
        return new PluginPackageInfo(manifest, runtime, Hash(archive), entries.Count, total);
    }

    /// <summary>Extrait l'archive dans <paramref name="directory"/> (créé s'il n'existe pas). Aucun fichier ne sort du dossier.</summary>
    public static void Extract(byte[] archive, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(directory);
        using var zip = Open(archive);
        var written = 0L;
        foreach (var entry in zip.Entries)
        {
            var relative = SafeRelativePath(entry.FullName);
            if (relative.Length == 0) continue;
            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root, StringComparison.Ordinal)) throw new PluginPackageException($"Chemin refusé dans l'archive : {entry.FullName}");
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > MaxUncompressedBytes) throw new PluginPackageException("L'archive décompressée dépasse la limite autorisée.");
                output.Write(buffer, 0, read);
            }
            if (entry.Name.EndsWith(".py", StringComparison.Ordinal) || entry.Name.EndsWith(".mjs", StringComparison.Ordinal) || entry.Name.EndsWith(".js", StringComparison.Ordinal))
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private static ZipArchive Open(byte[] archive)
    {
        try { return new ZipArchive(new MemoryStream(archive, writable: false), ZipArchiveMode.Read); }
        catch (InvalidDataException) { throw new PluginPackageException("Le fichier n'est pas une archive .zip valide."); }
    }

    /// <summary>Chemin relatif normalisé (séparateur « / »), ou lève si absolu, avec « .. » ou caractère interdit.</summary>
    public static string SafeRelativePath(string name)
    {
        var normalized = name.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':') || normalized.Contains('\0') || normalized.Split('/').Any(part => part == ".."))
            throw new PluginPackageException($"Chemin refusé dans l'archive : {name}");
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized;
    }
}
