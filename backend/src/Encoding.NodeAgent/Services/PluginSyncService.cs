using System.Net.Http.Json;
using System.Text.Json;
using Encoding.Core.Plugins;
using Encoding.NodeAgent.Runtime;

namespace Encoding.NodeAgent.Services;

/// <summary>
/// Aligne les plugins du Store de ce worker sur ceux du serveur auquel il est rattaché (0063) : télécharge ce qui manque, contrôle
/// l'empreinte SHA-256 annoncée, extrait dans un dossier temporaire puis le renomme, et retire les versions que le serveur ne
/// propose plus. Une panne réseau ne retire rien : le worker garde ce qu'il a.
/// </summary>
public sealed class PluginSyncService(NodeAgentOptions options, WorkerIdentityStore identityStore, IConfiguration configuration,
    ToolCapabilityProbe capabilityProbe, ILogger<PluginSyncService> logger) : BackgroundService
{
    private sealed record Listed(string Id, string Version, string Sha256);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(2) };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Roles.Contains(Encoding.Contracts.Nodes.NodeRole.Worker)) return;
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SyncOnceAsync(stoppingToken); }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or JsonException
                && !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("La synchronisation des plugins a échoué : {Reason}", exception.Message);
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }

    public async Task SyncOnceAsync(CancellationToken ct)
    {
        var identity = await identityStore.GetAsync(ct);
        if (!identity.HasCredentials || identity.ApiBaseUrl is null) return;
        var root = CustomPluginCatalog.DirectoryFor(configuration);
        List<Listed>? listed = null;
        string? activeServerUrl = null;
        var candidates = identity.IsEnrolled ? identity.GetCandidateServerUrls(options) : [identity.ApiBaseUrl];
        foreach (var serverUrl in candidates)
        {
            try
            {
                listed = await Get<List<Listed>>(identity, serverUrl, "api/v1/store/installed", ct) ?? [];
                activeServerUrl = serverUrl;
                break;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested) { }
        }
        if (listed is null || activeServerUrl is null)
            throw new HttpRequestException("Aucun serveur du cluster n'est joignable pour synchroniser les plugins.");
        Directory.CreateDirectory(root);
        var changed = false;
        foreach (var plugin in listed)
        {
            var target = Path.Combine(root, plugin.Id, plugin.Version);
            if (File.Exists(Path.Combine(target, CustomPluginCatalog.VerifiedMarker))
                && File.ReadAllText(Path.Combine(target, CustomPluginCatalog.VerifiedMarker)).Trim() == plugin.Sha256) continue;
            try { await InstallAsync(identity, activeServerUrl, plugin, target, ct); changed = true; }
            catch (PluginPackageException exception) { logger.LogError("Le plugin {PluginId} {Version} est refusé : {Reason}", plugin.Id, plugin.Version, exception.Message); }
            catch (Exception exception) when (exception is not OperationCanceledException) { logger.LogError(exception,
                "L'installation du plugin {PluginId} {Version} a échoué.", plugin.Id, plugin.Version); }
        }
        // Ménage : seulement quand la liste a bien été reçue, jamais sur une panne.
        foreach (var pluginDirectory in Directory.EnumerateDirectories(root))
            foreach (var versionDirectory in Directory.EnumerateDirectories(pluginDirectory))
                if (!listed.Any(item => item.Id == Path.GetFileName(pluginDirectory) && item.Version == Path.GetFileName(versionDirectory)))
                { TryDelete(versionDirectory); changed = true; }
        if (changed) await capabilityProbe.InvalidateCacheAsync(ct);
    }

    private async Task InstallAsync(WorkerIdentityState identity, string serverUrl, Listed plugin, string target, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(serverUrl), $"api/v1/store/packages/{plugin.Id}/{plugin.Version}/download"));
        request.Headers.Add("X-Node-Token", identity.NodeToken);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var archive = await response.Content.ReadAsByteArrayAsync(ct);
        if (!string.Equals(PluginPackage.Hash(archive), plugin.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new PluginPackageException("L'archive reçue ne correspond pas à l'empreinte annoncée par le serveur.");
        var info = PluginPackage.Inspect(archive);
        if (info.Manifest.Id != plugin.Id || info.Manifest.Version != plugin.Version) throw new PluginPackageException("L'archive ne contient pas le plugin annoncé.");
        var temporary = target + ".partial";
        TryDelete(temporary);
        PluginPackage.Extract(archive, temporary);
        File.WriteAllText(Path.Combine(temporary, CustomPluginCatalog.VerifiedMarker), plugin.Sha256);
        TryDelete(target);
        Directory.Move(temporary, target);
        logger.LogInformation("Plugin {PluginId} {Version} installé sur ce worker.", plugin.Id, plugin.Version);
    }

    private async Task<T?> Get<T>(WorkerIdentityState identity, string serverUrl, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(serverUrl), path));
        request.Headers.Add("X-Node-Token", identity.NodeToken);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
