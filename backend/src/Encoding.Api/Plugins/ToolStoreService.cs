using System.Text.Json;
using System.Collections.Concurrent;
using Encoding.Contracts.Tools;
using Encoding.Core.Tools;

namespace Encoding.Api.Plugins;

public sealed record InstalledToolPackage(
    string Id,
    string Version,
    string Platform,
    string ArchiveSha256,
    IReadOnlyList<ToolPackageExecutable> Executables,
    IReadOnlyList<string> Capabilities,
    string Source,
    DateTimeOffset InstalledAt);

/// <summary>Stocke sur chaque serveur l'unique binaire actif par rôle et plateforme, partagé par tous les plugins.</summary>
public sealed class ToolStoreService(IConfiguration configuration, ILogger<ToolStoreService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<(string Id, string Version, string Platform), byte> verified = new();
    public string Directory { get; } = configuration["ToolStore:Directory"]
        ?? Path.Combine(configuration["Node:DataDirectory"] is { Length: > 0 } data ? data : Path.Combine(AppContext.BaseDirectory, "Data"), "tool-store");
    private string IndexPath => Path.Combine(Directory, "installed.json");
    private string PackageDirectory(string id, string version, string platform) => Path.Combine(Directory, "packages", id, version, platform);
    private string ManagedDirectory(string id, string version, string platform) => Path.Combine(Directory, "managed", id, version, platform);
    public string PackagePath(string id, string version, string platform) => Path.Combine(PackageDirectory(id, version, platform), "package.zip");

    /// <summary>Résout un outil installé pour les opérations locales de l'API (par ex. import MediaInfo).</summary>
    public string? ResolveExecutable(string toolId)
    {
        foreach (var installed in Available())
        {
            try
            {
                var archive = File.ReadAllBytes(PackagePath(installed.Id, installed.Version, installed.Platform));
                var info = ToolPackageArchive.Inspect(archive, installed.Platform);
                if (info.ArchiveSha256 != installed.ArchiveSha256 || info.Manifest.Id != installed.Id || info.Manifest.Version != installed.Version) continue;
                var executable = info.Manifest.Executables.SingleOrDefault(item => item.ToolId == toolId);
                if (executable is null) continue;
                var directory = ManagedDirectory(installed.Id, installed.Version, installed.Platform);
                var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var path = Path.GetFullPath(Path.Combine(root, ToolPackageArchive.SafeRelativePath(executable.Path)));
                if (!path.StartsWith(root, StringComparison.Ordinal)) continue;
                var marker = Path.Combine(directory, ".verified");
                var extractedHash = File.Exists(path)
                    ? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))
                    : "";
                if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != installed.ArchiveSha256
                    || !string.Equals(extractedHash, executable.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(directory);
                    var staging = directory + ".extracting-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        ToolPackageArchive.Extract(archive, staging, info.Manifest);
                        File.WriteAllText(Path.Combine(staging, ".verified"), installed.ArchiveSha256);
                        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
                        System.IO.Directory.Move(staging, directory);
                    }
                    finally { TryDelete(staging); }
                }
                if (File.Exists(path)) return path;
            }
            catch (Exception exception) when (exception is ToolPackageException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                logger.LogWarning(exception, "L'outil {ToolId} du Store n'a pas pu être extrait pour un usage local.", toolId);
            }
        }
        return null;
    }

    public IReadOnlyList<InstalledToolPackage> Installed()
    {
        try { return File.Exists(IndexPath) ? JsonSerializer.Deserialize<List<InstalledToolPackage>>(File.ReadAllText(IndexPath), Json) ?? [] : []; }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            logger.LogError(exception, "L'index des outils installés est illisible : {Path}.", IndexPath);
            return [];
        }
    }

    public void LoadInstalled()
    {
        verified.Clear();
        foreach (var item in Installed())
        {
            try
            {
                var path = PackagePath(item.Id, item.Version, item.Platform);
                var archive = File.ReadAllBytes(path);
                var info = ToolPackageArchive.Inspect(archive, item.Platform);
                if (info.ArchiveSha256 != item.ArchiveSha256 || info.Manifest.Id != item.Id || info.Manifest.Version != item.Version
                    || info.Manifest.Platform != item.Platform
                    || !(info.Manifest.Executables ?? []).SequenceEqual(item.Executables ?? [])
                    || !(info.Manifest.Capabilities ?? []).Order(StringComparer.Ordinal).SequenceEqual((item.Capabilities ?? []).Order(StringComparer.Ordinal)))
                    throw new ToolPackageException("L'archive installée ne correspond plus à son enregistrement vérifié.");
                verified[(item.Id, item.Version, item.Platform)] = 0;
            }
            catch (Exception exception) when (exception is ToolPackageException or IOException or UnauthorizedAccessException)
            {
                logger.LogError(exception, "L'outil installé {ToolId} {Version} ({Platform}) est refusé.", item.Id, item.Version, item.Platform);
            }
        }
    }

    public IReadOnlyList<InstalledToolPackage> Available() => Installed()
        .Where(item => verified.ContainsKey((item.Id, item.Version, item.Platform))).ToArray();

    public async Task<InstalledToolPackage> ImportAsync(byte[] archive, string source, string? expectedSha256 = null, string? expectedId = null,
        string? expectedVersion = null, string? expectedPlatform = null, CancellationToken ct = default)
    {
        var info = ToolPackageArchive.Inspect(archive, expectedPlatform);
        var manifest = info.Manifest;
        if (expectedSha256 is not null && !string.Equals(expectedSha256, info.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new ToolPackageException("L'empreinte SHA-256 de l'archive ne correspond pas à celle annoncée par le dépôt.");
        if (expectedId is not null && expectedId != manifest.Id) throw new ToolPackageException($"L'archive contient « {manifest.Id} », pas « {expectedId} ».");
        if (expectedVersion is not null && expectedVersion != manifest.Version) throw new ToolPackageException($"L'archive contient la version {manifest.Version}, pas « {expectedVersion} ».");
        if (expectedPlatform is not null && expectedPlatform != manifest.Platform) throw new ToolPackageException("La plateforme de l'archive ne correspond pas à celle sélectionnée.");

        await gate.WaitAsync(ct);
        try
        {
            var current = Installed().ToList();
            var sameId = current.SingleOrDefault(item => item.Id == manifest.Id && item.Platform == manifest.Platform);
            if (sameId is not null && sameId.Version == manifest.Version)
            {
                if (sameId.ArchiveSha256 != info.ArchiveSha256)
                    throw new ToolPackageException("Cette version et plateforme sont déjà installées avec un contenu différent ; publiez une nouvelle version.");
                if (verified.ContainsKey((sameId.Id, sameId.Version, sameId.Platform))) return sameId;
            }
            var roles = manifest.Executables.Select(item => item.ToolId).ToHashSet(StringComparer.Ordinal);
            var collision = current.FirstOrDefault(item => !(item.Id == manifest.Id && item.Platform == manifest.Platform)
                && item.Platform == manifest.Platform && item.Executables.Any(executable => roles.Contains(executable.ToolId)));
            if (collision is not null)
                throw new ToolPackageException($"Le rôle d'outil « {collision.Executables.First(item => roles.Contains(item.ToolId)).ToolId} » est déjà fourni par {collision.Id}@{collision.Version} ({collision.ArchiveSha256}). Désinstallez ce paquet avant d'en choisir un autre.");

            var target = PackageDirectory(manifest.Id, manifest.Version, manifest.Platform);
            System.IO.Directory.CreateDirectory(target);
            var temporary = PackagePath(manifest.Id, manifest.Version, manifest.Platform) + ".tmp";
            await File.WriteAllBytesAsync(temporary, archive, ct);
            File.Move(temporary, PackagePath(manifest.Id, manifest.Version, manifest.Platform), overwrite: true);
            var record = new InstalledToolPackage(manifest.Id, manifest.Version, manifest.Platform, info.ArchiveSha256,
                manifest.Executables, manifest.Capabilities ?? [], source, DateTimeOffset.UtcNow);
            current.RemoveAll(item => item.Id == manifest.Id && item.Platform == manifest.Platform);
            current.Add(record);
            Save(current);
            verified[(record.Id, record.Version, record.Platform)] = 0;
            if (sameId is not null)
            {
                verified.TryRemove((sameId.Id, sameId.Version, sameId.Platform), out _);
                TryDelete(PackageDirectory(sameId.Id, sameId.Version, sameId.Platform));
                TryDelete(ManagedDirectory(sameId.Id, sameId.Version, sameId.Platform));
            }
            logger.LogInformation("Paquet d'outil {ToolId} {Version} ({Platform}) installé depuis {Source}, SHA-256 {Sha256}.",
                record.Id, record.Version, record.Platform, source, record.ArchiveSha256);
            return record;
        }
        finally { gate.Release(); }
    }

    public bool Satisfies(PluginToolDependency dependency, string platform)
    {
        var installed = Available().SingleOrDefault(item => item.Id == dependency.ToolPackageId && item.Platform == platform);
        if (installed is null || !VersionSatisfies(installed.Version, dependency.MinimumVersion, dependency.MaximumVersionExclusive)) return false;
        var capabilities = installed.Capabilities.ToHashSet(StringComparer.Ordinal);
        return (dependency.RequiredCapabilities ?? []).All(capabilities.Contains);
    }

    public static bool VersionSatisfies(string actualText, string minimumText, string? maximumExclusiveText = null)
    {
        if (!TryVersion(actualText, out var actual) || !TryVersion(minimumText, out var minimum) || actual < minimum) return false;
        return maximumExclusiveText is null || TryVersion(maximumExclusiveText, out var maximum) && actual < maximum;
    }

    public async Task<bool> RemoveAsync(string id, string platform, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var current = Installed().ToList();
            var target = current.SingleOrDefault(item => item.Id == id && item.Platform == platform);
            if (target is null) return false;
            current.Remove(target);
            Save(current);
            verified.TryRemove((target.Id, target.Version, target.Platform), out _);
            TryDelete(PackageDirectory(target.Id, target.Version, target.Platform));
            TryDelete(ManagedDirectory(target.Id, target.Version, target.Platform));
            logger.LogInformation("Paquet d'outil {ToolId} ({Platform}) retiré du Store.", id, platform);
            return true;
        }
        finally { gate.Release(); }
    }

    private void Save(List<InstalledToolPackage> current)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temporary = IndexPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(current, Json));
        File.Move(temporary, IndexPath, overwrite: true);
    }

    private static bool TryVersion(string value, out Version version)
    {
        return Version.TryParse(value, out version!);
    }

    private static void TryDelete(string path)
    {
        try { if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class ToolStoreStartup(ToolStoreService store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) { store.LoadInstalled(); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
