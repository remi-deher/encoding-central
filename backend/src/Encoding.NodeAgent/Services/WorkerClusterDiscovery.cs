using System.Collections.Concurrent;
using System.Net.Http.Json;
using Haukcode.Mdns;

namespace Encoding.NodeAgent.Services;

/// <summary>
/// Cluster connu de ce nœud. <paramref name="Source"/> : <c>network</c> (DNS-SD), <c>manual</c> (adresse saisie, mémorisée) ou <c>configured</c> (serveur du conteneur).
/// <paramref name="Reachable"/> passe à faux quand la dernière sonde d'une adresse mémorisée a échoué : la ligne reste visible plutôt que de disparaître sans explication.
/// </summary>
public sealed record DiscoveredCluster(Guid Id, string Name, string ApiBaseUrl, DateTimeOffset DiscoveredAt, string Source = "network", bool Reachable = true);

public sealed class WorkerClusterDiscovery(
    NodeAgentOptions options,
    ILogger<WorkerClusterDiscovery> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, DiscoveredCluster> clusters = new();
    private readonly object knownAddressesLock = new();
    private MdnsBrowser? browser;

    /// <summary>
    /// Ajoute un cluster par son adresse quand la découverte DNS-SD ne traverse pas le réseau (conteneurs, sous-réseaux distincts).
    /// L'adresse doit répondre sur <c>api/v1/cluster</c> avec l'identité d'un cluster Encoding.
    /// </summary>
    public async Task<DiscoveredCluster> AddByAddressAsync(string address, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("L'adresse doit être une URL HTTP ou HTTPS absolue, par exemple http://192.168.1.51:8090.");
        var baseUrl = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
        var cluster = await ProbeAsync(baseUrl, cancellationToken);
        RememberAddress(baseUrl.AbsoluteUri);
        return cluster;
    }

    private async Task<DiscoveredCluster> ProbeAsync(Uri baseUrl, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        ClusterIdentity? identity;
        try { identity = await client.GetFromJsonAsync<ClusterIdentity>(new Uri(baseUrl, "api/v1/cluster"), cancellationToken); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { throw new HttpRequestException($"Aucun cluster Encoding ne répond à {baseUrl} : {exception.Message}", exception); }
        if (identity is null || identity.Id == Guid.Empty)
            throw new HttpRequestException($"{baseUrl} ne répond pas comme un cluster Encoding.");
        var cluster = new DiscoveredCluster(identity.Id, identity.Name, baseUrl.AbsoluteUri, DateTimeOffset.UtcNow, "manual");
        clusters[identity.Id] = cluster;
        logger.LogInformation("Cluster ajouté par adresse : {ClusterName} ({ClusterId}) à {ApiBaseUrl}.", cluster.Name, cluster.Id, cluster.ApiBaseUrl);
        return cluster;
    }

    private string KnownAddressesPath => Path.Combine(options.DataDirectory, "known-clusters.json");

    private string[] LoadKnownAddresses()
    {
        try
        {
            return File.Exists(KnownAddressesPath)
                ? System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(KnownAddressesPath)) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            logger.LogWarning(exception, "La liste des adresses de clusters mémorisées est illisible ; elle est ignorée.");
            return [];
        }
    }

    /// <summary>Mémorise l'adresse dans le dossier de données de l'agent pour la retrouver après un redémarrage ou un redéploiement.</summary>
    private void RememberAddress(string baseUrl)
    {
        lock (knownAddressesLock)
        {
            var known = LoadKnownAddresses();
            if (known.Contains(baseUrl, StringComparer.OrdinalIgnoreCase)) return;
            try
            {
                Directory.CreateDirectory(options.DataDirectory);
                File.WriteAllText(KnownAddressesPath, System.Text.Json.JsonSerializer.Serialize(known.Append(baseUrl).ToArray()));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "L'adresse du cluster {ApiBaseUrl} n'a pas pu être mémorisée.", baseUrl);
            }
        }
    }

    /// <summary>Relit chaque adresse mémorisée : un cluster injoignable au démarrage réapparaît dès qu'il répond.</summary>
    private async Task RefreshKnownAddressesAsync(CancellationToken cancellationToken)
    {
        string[] known;
        lock (knownAddressesLock) known = LoadKnownAddresses();
        foreach (var address in known)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var baseUrl)) continue;
            try { await ProbeAsync(baseUrl, cancellationToken); }
            catch (HttpRequestException)
            {
                logger.LogDebug("Le cluster mémorisé {ApiBaseUrl} ne répond pas.", address);
                MarkUnreachable(baseUrl.AbsoluteUri);
            }
        }
    }

    private void MarkUnreachable(string baseUrl)
    {
        foreach (var (id, cluster) in clusters)
            if (cluster.Source == "manual" && string.Equals(cluster.ApiBaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase))
                clusters[id] = cluster with { Reachable = false };
    }

    /// <summary>
    /// Oublie un cluster ajouté par adresse : il disparaît de la liste et n'est plus sondé. Retourne faux si aucune adresse mémorisée ne correspond
    /// (un cluster détecté sur le réseau ou le serveur du conteneur ne s'oublie pas : ils reviendraient aussitôt).
    /// </summary>
    public bool Forget(Guid clusterId)
    {
        if (!clusters.TryGetValue(clusterId, out var cluster)) return false;
        lock (knownAddressesLock)
        {
            var known = LoadKnownAddresses();
            var remaining = known.Where(address => !string.Equals(address, cluster.ApiBaseUrl, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (remaining.Length == known.Length) return false;
            try { File.WriteAllText(KnownAddressesPath, System.Text.Json.JsonSerializer.Serialize(remaining)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "L'adresse du cluster {ApiBaseUrl} n'a pas pu être oubliée.", cluster.ApiBaseUrl);
                return false;
            }
        }
        clusters.TryRemove(clusterId, out _);
        return true;
    }

    public IReadOnlyList<DiscoveredCluster> List() => clusters.Values.OrderBy(cluster => cluster.Name).ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            try
            {
                browser = new MdnsBrowser("_encoding._tcp");
                browser.ServiceFound += OnServiceFound;
                browser.ServiceLost += OnServiceLost;
                browser.Start();
                logger.LogInformation("Découverte des clusters Encoding démarrée via DNS-SD.");
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "La découverte DNS-SD est indisponible sur cette interface réseau.");
            }
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            while (!stoppingToken.IsCancellationRequested)
            {
                await AddConfiguredClusterAsync(client, stoppingToken);
                await RefreshKnownAddressesAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "La découverte DNS-SD est indisponible sur cette interface réseau.");
        }
    }

    private async Task AddConfiguredClusterAsync(HttpClient client, CancellationToken cancellationToken)
    {
        if (options.ClusterApiBaseUrl is null) return;
        try
        {
            var identity = await client.GetFromJsonAsync<ClusterIdentity>(
                new Uri(new Uri(options.ClusterApiBaseUrl), "api/v1/cluster"), cancellationToken);
            if (identity is null || identity.Id == Guid.Empty) return;
            clusters[identity.Id] = new DiscoveredCluster(identity.Id, identity.Name, options.ClusterApiBaseUrl, DateTimeOffset.UtcNow, "configured");
            logger.LogInformation("Cluster configuré découvert : {ClusterName} ({ClusterId}).", identity.Name, identity.Id);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug("Le point de découverte de cluster configuré n’est pas encore disponible.");
        }
    }

    private void OnServiceFound(ServiceProfile service)
    {
        if (!service.Properties.TryGetValue("clusterId", out var idValue)
            || !Guid.TryParse(idValue, out var clusterId)) return;

        var apiBaseUrl = service.Properties.TryGetValue("apiUrl", out var configuredUrl)
            ? configuredUrl
            : $"http://{service.Hostname.TrimEnd('.') }:{service.Port}/";
        if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;

        var clusterName = service.Properties.TryGetValue("clusterName", out var configuredName) && !string.IsNullOrWhiteSpace(configuredName)
            ? configuredName
            : service.InstanceName;
        var cluster = new DiscoveredCluster(clusterId, clusterName, uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : uri.AbsoluteUri + '/', DateTimeOffset.UtcNow);
        clusters.AddOrUpdate(clusterId, cluster, (_, existing) => existing.Source == "configured" ? existing : cluster);
        logger.LogInformation("Cluster découvert : {ClusterName} ({ClusterId}).", cluster.Name, cluster.Id);
    }

    private void OnServiceLost(ServiceProfile service)
    {
        if (service.Properties.TryGetValue("clusterId", out var idValue) && Guid.TryParse(idValue, out var clusterId))
            clusters.TryRemove(clusterId, out _);
    }

    public override void Dispose()
    {
        browser?.Dispose();
        browser = null;
        base.Dispose();
    }

    private sealed record ClusterIdentity(Guid Id, string Name);
}
