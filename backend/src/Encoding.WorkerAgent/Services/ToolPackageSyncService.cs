using Encoding.Contracts.Tools;
using Encoding.Core.Tools;
using Encoding.WorkerAgent.Runtime;

namespace Encoding.WorkerAgent.Services;

/// <summary>Synchronise les paquets d'outils gérés vers un worker et vérifie le hash avant extraction.</summary>
public sealed class ToolPackageSyncService(WorkerIdentityStore identityStore, IConfiguration configuration,
    ToolCapabilityProbe capabilityProbe, ILogger<ToolPackageSyncService> logger)
{
    private sealed record Listed(string Id, string Version, string Platform, string ArchiveSha256);
    private readonly HttpClient http = WorkerCentralHttp.Create(TimeSpan.FromMinutes(3));
    private string Root => configuration["Runtime:ManagedToolDirectory"] is { Length: > 0 } configured ? configured : ManagedToolCatalog.DefaultDirectory;

    internal async Task<IReadOnlyList<WorkerAppliedItem>> SyncDesiredAsync(string server, WorkerStoreDesiredState state, CancellationToken ct)
    {
        var identity = await identityStore.GetAsync(ct);
        if (!identity.HasCredentials || !identity.IsEnrolled) return [];
        var listed = state.Items.Where(item => item.Kind == "tool" && item.Platform is not null)
            .Select(item => new Listed(item.Id, item.Version, item.Platform!, item.Sha256)).ToArray();

        Directory.CreateDirectory(Root);
        var platform = ToolPackagePlatform.Current;
        var desired = listed.Where(item => item.Platform == platform).ToArray();
        var changed = false;
        var allApplied = true;
        var applied = new List<WorkerAppliedItem>();
        foreach (var item in desired)
        {
            var target = Path.Combine(Root, item.Id, item.Version, item.Platform);
            var marker = Path.Combine(target, ".verified");
            if (File.Exists(marker) && string.Equals(File.ReadAllText(marker).Trim(), item.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            { applied.Add(new("tool", item.Id, item.Version, item.Platform, item.ArchiveSha256, "applied", null)); continue; }
            try { await InstallAsync(identity, server, item, target, ct); changed = true; applied.Add(new("tool", item.Id, item.Version, item.Platform, item.ArchiveSha256, "applied", null)); }
            catch (ToolPackageException exception) { logger.LogError("Le paquet outil {ToolId} {Version} ({Platform}) est refusé : {Reason}", item.Id,
                item.Version, item.Platform, exception.Message); allApplied = false; applied.Add(new("tool", item.Id, item.Version, item.Platform, item.ArchiveSha256, "failed", "package_rejected")); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { logger.LogError(exception, "L'installation du paquet outil {ToolId} {Version} a échoué.", item.Id, item.Version); allApplied = false; applied.Add(new("tool", item.Id, item.Version, item.Platform, item.ArchiveSha256, "failed", "install_failed")); }
        }
        if (allApplied) foreach (var toolDirectory in Directory.EnumerateDirectories(Root))
            foreach (var versionDirectory in Directory.EnumerateDirectories(toolDirectory))
                foreach (var platformDirectory in Directory.EnumerateDirectories(versionDirectory))
                {
                    var tuple = (Path.GetFileName(toolDirectory), Path.GetFileName(versionDirectory), Path.GetFileName(platformDirectory));
                    if (!desired.Any(item => (item.Id, item.Version, item.Platform) == tuple)) TryDelete(platformDirectory);
                }
        if (changed) await capabilityProbe.InvalidateCacheAsync(ct);
        return applied;
    }

    private async Task InstallAsync(WorkerIdentityState identity, string server, Listed item, string target, CancellationToken ct)
    {
        var path = $"api/v1/store/tools/packages/{Uri.EscapeDataString(item.Id)}/{Uri.EscapeDataString(item.Version)}/{Uri.EscapeDataString(item.Platform)}/download";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(server), path));
        request.Headers.Add("X-Node-Token", identity.WorkerToken);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > ToolPackageArchive.MaxArchiveBytes) throw new ToolPackageException("L'archive dépasse 512 Mio.");
        var archive = await ReadLimitedAsync(response, ct);
        var info = ToolPackageArchive.Inspect(archive, item.Platform);
        if (!string.Equals(info.ArchiveSha256, item.ArchiveSha256, StringComparison.OrdinalIgnoreCase)
            || info.Manifest.Id != item.Id || info.Manifest.Version != item.Version)
            throw new ToolPackageException("L'archive reçue ne correspond pas au paquet et au SHA-256 annoncés.");
        var temporary = target + ".partial";
        TryDelete(temporary);
        ToolPackageArchive.Extract(archive, temporary, info.Manifest);
        File.WriteAllText(Path.Combine(temporary, ".verified"), info.ArchiveSha256);
        TryDelete(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(temporary, target);
        logger.LogInformation("Paquet outil {ToolId} {Version} ({Platform}) installé sur ce worker, SHA-256 {Sha256}.", item.Id, item.Version, item.Platform, item.ArchiveSha256);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > ToolPackageArchive.MaxArchiveBytes)
            throw new ToolPackageException("L'archive dépasse 512 Mio.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (memory.Length + read > ToolPackageArchive.MaxArchiveBytes)
                throw new ToolPackageException("L'archive dépasse 512 Mio.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
