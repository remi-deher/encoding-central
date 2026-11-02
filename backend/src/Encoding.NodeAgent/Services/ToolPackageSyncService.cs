using System.Net.Http.Json;
using System.Text.Json;
using Encoding.Contracts.Tools;
using Encoding.Core.Tools;
using Encoding.NodeAgent.Runtime;

namespace Encoding.NodeAgent.Services;

/// <summary>Synchronise les paquets d'outils gérés vers un worker et vérifie le hash avant extraction.</summary>
public sealed class ToolPackageSyncService(NodeAgentOptions options, WorkerIdentityStore identityStore, IConfiguration configuration,
    ToolCapabilityProbe capabilityProbe, ILogger<ToolPackageSyncService> logger) : BackgroundService
{
    private sealed record Listed(string Id, string Version, string Platform, string ArchiveSha256);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private string Root => configuration["Runtime:ManagedToolDirectory"] is { Length: > 0 } configured ? configured : ManagedToolCatalog.DefaultDirectory;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Roles.Contains(Encoding.Contracts.Nodes.NodeRole.Worker)) return;
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SyncOnceAsync(stoppingToken); }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or JsonException
                && !stoppingToken.IsCancellationRequested)
            { logger.LogWarning("La synchronisation des outils gérés a échoué : {Reason}", exception.Message); }
            await Task.Delay(Interval, stoppingToken);
        }
    }

    public async Task SyncOnceAsync(CancellationToken ct)
    {
        var identity = await identityStore.GetAsync(ct);
        if (!identity.HasCredentials || identity.ApiBaseUrl is null) return;
        List<Listed>? listed = null;
        string? activeServer = null;
        foreach (var server in identity.IsEnrolled ? identity.GetCandidateServerUrls(options) : [identity.ApiBaseUrl])
        {
            try { listed = await Get(identity, server, ct); activeServer = server; break; }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested) { }
        }
        if (listed is null || activeServer is null) throw new HttpRequestException("Aucun serveur du cluster n'est joignable pour synchroniser les outils.");

        Directory.CreateDirectory(Root);
        var platform = ToolPackagePlatform.Current;
        var desired = listed.Where(item => item.Platform == platform).ToArray();
        var changed = false;
        foreach (var item in desired)
        {
            var target = Path.Combine(Root, item.Id, item.Version, item.Platform);
            var marker = Path.Combine(target, ".verified");
            if (File.Exists(marker) && string.Equals(File.ReadAllText(marker).Trim(), item.ArchiveSha256, StringComparison.OrdinalIgnoreCase)) continue;
            try { await InstallAsync(identity, activeServer, item, target, ct); changed = true; }
            catch (ToolPackageException exception) { logger.LogError("Le paquet outil {ToolId} {Version} ({Platform}) est refusé : {Reason}", item.Id,
                item.Version, item.Platform, exception.Message); }
        }
        foreach (var toolDirectory in Directory.EnumerateDirectories(Root))
            foreach (var versionDirectory in Directory.EnumerateDirectories(toolDirectory))
                foreach (var platformDirectory in Directory.EnumerateDirectories(versionDirectory))
                {
                    var tuple = (Path.GetFileName(toolDirectory), Path.GetFileName(versionDirectory), Path.GetFileName(platformDirectory));
                    if (!desired.Any(item => (item.Id, item.Version, item.Platform) == tuple)) TryDelete(platformDirectory);
                }
        if (changed) await capabilityProbe.InvalidateCacheAsync(ct);
    }

    private async Task InstallAsync(WorkerIdentityState identity, string server, Listed item, string target, CancellationToken ct)
    {
        var path = $"api/v1/store/tools/packages/{Uri.EscapeDataString(item.Id)}/{Uri.EscapeDataString(item.Version)}/{Uri.EscapeDataString(item.Platform)}/download";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(server), path));
        request.Headers.Add("X-Node-Token", identity.NodeToken);
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

    private async Task<List<Listed>> Get(WorkerIdentityState identity, string server, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(server), "api/v1/store/tools/installed"));
        request.Headers.Add("X-Node-Token", identity.NodeToken);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<Listed>>(new JsonSerializerOptions(JsonSerializerDefaults.Web), ct) ?? [];
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
