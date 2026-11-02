using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Encoding.Contracts.Tools;

namespace Encoding.Core.Tools;

public sealed class ToolPackageException(string message) : Exception(message);

public sealed record ToolPackageInfo(ToolPackageManifest Manifest, string ArchiveSha256, long UncompressedBytes, int Entries);

/// <summary>Inspecte et extrait des paquets d'outils bornés, sans permettre les traversées ni les liens symboliques.</summary>
public static partial class ToolPackageArchive
{
    public const string ManifestFile = "tool.json";
    public const long MaxArchiveBytes = 512L * 1024 * 1024;
    public const long MaxUncompressedBytes = 1024L * 1024 * 1024;
    public const int MaxEntries = 2048;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HashPattern();

    [GeneratedRegex("^\\d+\\.\\d+\\.\\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static ToolPackageInfo Inspect(byte[] archive, string? expectedPlatform = null)
    {
        if (archive.Length == 0 || archive.Length > MaxArchiveBytes)
            throw new ToolPackageException($"L'archive doit peser entre 1 octet et {MaxArchiveBytes / 1024 / 1024} Mio.");

        using var zip = Open(archive);
        var files = zip.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToArray();
        if (files.Length > MaxEntries) throw new ToolPackageException($"L'archive contient plus de {MaxEntries} fichiers.");
        long uncompressed = 0;
        foreach (var entry in zip.Entries)
        {
            SafeRelativePath(entry.FullName);
            if (IsSymbolicLink(entry)) throw new ToolPackageException($"Lien symbolique refusé dans l'archive : {entry.FullName}");
            uncompressed = checked(uncompressed + entry.Length);
            if (uncompressed > MaxUncompressedBytes) throw new ToolPackageException("L'archive décompressée dépasse la limite autorisée.");
        }

        var manifestEntry = zip.GetEntry(ManifestFile) ?? throw new ToolPackageException("tool.json est absent à la racine de l'archive.");
        if (manifestEntry.Length > 256 * 1024) throw new ToolPackageException("tool.json dépasse 256 Kio.");
        ToolPackageManifest manifest;
        try
        {
            using var stream = manifestEntry.Open();
            manifest = JsonSerializer.Deserialize<ToolPackageManifest>(stream, Json)
                ?? throw new ToolPackageException("tool.json est vide.");
        }
        catch (JsonException exception) { throw new ToolPackageException($"tool.json est illisible : {exception.Message}"); }

        ValidateManifest(manifest, expectedPlatform);
        foreach (var executable in manifest.Executables)
        {
            var entry = zip.GetEntry(SafeRelativePath(executable.Path))
                ?? throw new ToolPackageException($"Le binaire « {executable.Path} » n'est pas présent dans l'archive.");
            if (entry.Length == 0) throw new ToolPackageException($"Le binaire « {executable.Path} » est vide.");
            using var stream = entry.Open();
            var digest = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!string.Equals(digest, executable.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ToolPackageException($"Le binaire « {executable.ToolId} » ne correspond pas à son SHA-256 déclaré.");
        }

        return new(manifest, Hash(archive), uncompressed, files.Length);
    }

    public static void Extract(byte[] archive, string directory, ToolPackageManifest manifest)
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
            if (!target.StartsWith(root, StringComparison.Ordinal)) throw new ToolPackageException($"Chemin refusé dans l'archive : {entry.FullName}");
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                written = checked(written + count);
                if (written > MaxUncompressedBytes) throw new ToolPackageException("L'archive décompressée dépasse la limite autorisée.");
                output.Write(buffer, 0, count);
            }
        }

        if (!OperatingSystem.IsWindows())
            foreach (var executable in manifest.Executables)
                File.SetUnixFileMode(Path.Combine(root, executable.Path),
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    public static string SafeRelativePath(string name)
    {
        var normalized = name.Replace('\\', '/');
        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Contains(':') || normalized.Contains('\0')
            || normalized.Split('/').Any(part => part is ".." or "."))
            throw new ToolPackageException($"Chemin refusé dans l'archive : {name}");
        return normalized;
    }

    private static void ValidateManifest(ToolPackageManifest manifest, string? expectedPlatform)
    {
        if (manifest.ContractVersion != 1) throw new ToolPackageException("tool.json doit déclarer contractVersion 1.");
        if (manifest.Id is null || !IdPattern().IsMatch(manifest.Id)) throw new ToolPackageException("Identifiant de paquet d'outil invalide.");
        if (manifest.Version is null || !VersionPattern().IsMatch(manifest.Version)) throw new ToolPackageException("Version de paquet d'outil invalide.");
        if (string.IsNullOrWhiteSpace(manifest.Name)) throw new ToolPackageException("Le nom du paquet d'outil est obligatoire.");
        if (manifest.Platform is null || !IdPattern().IsMatch(manifest.Platform)) throw new ToolPackageException("Plateforme de paquet d'outil invalide.");
        if (expectedPlatform is not null && !string.Equals(manifest.Platform, expectedPlatform, StringComparison.Ordinal))
            throw new ToolPackageException($"Le paquet cible {manifest.Platform}, mais ce nœud utilise {expectedPlatform}.");
        if (manifest.Executables is null || manifest.Executables.Count == 0) throw new ToolPackageException("Le paquet doit fournir au moins un exécutable.");
        if (manifest.Executables.Any(item => item is null || string.IsNullOrWhiteSpace(item.ToolId) || !IdPattern().IsMatch(item.ToolId)
            || string.IsNullOrWhiteSpace(item.Path) || string.IsNullOrWhiteSpace(item.Sha256) || !HashPattern().IsMatch(item.Sha256)))
            throw new ToolPackageException("Chaque exécutable doit déclarer un rôle valide et son SHA-256.");
        if (manifest.Executables.Select(item => item.ToolId).Distinct(StringComparer.Ordinal).Count() != manifest.Executables.Count)
            throw new ToolPackageException("Un rôle d'outil ne peut apparaître qu'une fois dans un paquet.");
        var paths = manifest.Executables.Select(item => SafeRelativePath(item.Path)).ToArray();
        if (paths.Distinct(StringComparer.Ordinal).Count() != paths.Length) throw new ToolPackageException("Deux rôles ne peuvent partager le même chemin de binaire.");
        if (manifest.Capabilities?.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128) is true)
            throw new ToolPackageException("Capacité de paquet d'outil invalide.");
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry) => ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    private static ZipArchive Open(byte[] archive)
    {
        try { return new ZipArchive(new MemoryStream(archive, writable: false), ZipArchiveMode.Read); }
        catch (InvalidDataException) { throw new ToolPackageException("Le fichier n'est pas une archive .zip valide."); }
    }
}
