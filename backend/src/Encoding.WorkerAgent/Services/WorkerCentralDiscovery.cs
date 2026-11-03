using System.Collections.Concurrent;
using System.Net.Http.Json;
using Haukcode.Mdns;

namespace Encoding.WorkerAgent.Services;

/// <summary>
/// Cluster connu de ce worker. <paramref name="Source"/> : <c>network</c> (DNS-SD), <c>manual</c> (adresse saisie, mémorisée) ou <c>configured</c> (serveur du conteneur).
/// <paramref name="Reachable"/> passe à faux quand la dernière sonde d'une adresse mémorisée a échoué : la ligne reste visible plutôt que de disparaître sans explication.
/// </summary>
public sealed record DiscoveredCentral(Guid Id, string Name, string ApiBaseUrl, DateTimeOffset DiscoveredAt, string Source = "network", bool Reachable = true);

public sealed class WorkerCentralDiscovery(
    WorkerAgentOptions options,
    ILogger<WorkerCentralDiscovery> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, DiscoveredCentral> centrals = new();
    private readonly object knownAddressesLock = new();
    private MdnsBrowser? browser;

    /// <summary>
    /// Ajoute un central par son adresse quand la découverte DNS-SD ne traverse pas le réseau (conteneurs, sous-réseaux distincts).
    /// L'adresse doit répondre sur <c>api/v1/central</c> avec l'identité d'un central Encoding.
    /// </summary>
    public async Task<DiscoveredCentral> AddByAddressAsync(string address, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("L'adresse doit être une URL HTTP ou HTTPS absolue, par exemple http://192.168.1.51:8090.");
        var baseUrl = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
        var central = await ProbeAsync(baseUrl, cancellationToken);
        RememberAddress(baseUrl.AbsoluteUri);
        return central;
    }

    private async Task<DiscoveredCentral> ProbeAsync(Uri baseUrl, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        CentralIdentity? identity;
        try { identity = await client.GetFromJsonAsync<CentralIdentity>(new Uri(baseUrl, "api/v1/central"), cancellationToken); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { throw new HttpRequestException($"Aucun central Encoding ne répond à {baseUrl} : {exception.Message}", exception); }
        if (identity is null || identity.Id == Guid.Empty)
            throw new HttpRequestException($"{baseUrl} ne répond pas comme un central Encoding.");
        var central = new DiscoveredCentral(identity.Id, identity.Name, baseUrl.AbsoluteUri, DateTimeOffset.UtcNow, "manual");
        centrals[identity.Id] = central;
        logger.LogInformation("Cluster ajouté par adresse : {ClusterName} ({ClusterId}) à {ApiBaseUrl}.", central.Name, central.Id, central.ApiBaseUrl);
        return central;
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
            logger.LogWarning(exception, "La liste des adresses de centraux mémorisées est illisible ; elle est ignorée.");
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
                logger.LogWarning(exception, "L'adresse du central {ApiBaseUrl} n'a pas pu être mémorisée.", baseUrl);
            }
        }
    }

    /// <summary>Relit chaque adresse mémorisée : un central injoignable au démarrage réapparaît dès qu'il répond.</summary>
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
                logger.LogDebug("Le central mémorisé {ApiBaseUrl} ne répond pas.", address);
                MarkUnreachable(baseUrl.AbsoluteUri);
            }
        }
    }

    private void MarkUnreachable(string baseUrl)
    {
        foreach (var (id, central) in centrals)
            if (central.Source == "manual" && string.Equals(central.ApiBaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase))
                centrals[id] = central with { Reachable = false };
    }

    /// <summary>
    /// Oublie un central ajouté par adresse : il disparaît de la liste et n'est plus sondé. Retourne faux si aucune adresse mémorisée ne correspond
    /// (un central détecté sur le réseau ou le serveur du conteneur ne s'oublie pas : ils reviendraient aussitôt).
    /// </summary>
    public bool Forget(Guid centralId)
    {
        if (!centrals.TryGetValue(centralId, out var central)) return false;
        lock (knownAddressesLock)
        {
            var known = LoadKnownAddresses();
            var remaining = known.Where(address => !string.Equals(address, central.ApiBaseUrl, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (remaining.Length == known.Length) return false;
            try { File.WriteAllText(KnownAddressesPath, System.Text.Json.JsonSerializer.Serialize(remaining)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "L'adresse du central {ApiBaseUrl} n'a pas pu être oubliée.", central.ApiBaseUrl);
                return false;
            }
        }
        centrals.TryRemove(centralId, out _);
        return true;
    }

    public IReadOnlyList<DiscoveredCentral> List() => centrals.Values.OrderBy(central => central.Name).ToArray();

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
                logger.LogInformation("Découverte des centraux Encoding démarrée via DNS-SD.");
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "La découverte DNS-SD est indisponible sur cette interface réseau.");
            }
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            while (!stoppingToken.IsCancellationRequested)
            {
                await AddConfiguredCentralAsync(client, stoppingToken);
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

    private async Task AddConfiguredCentralAsync(HttpClient client, CancellationToken cancellationToken)
    {
        if (options.CentralApiBaseUrl is null) return;
        try
        {
            var identity = await client.GetFromJsonAsync<CentralIdentity>(
                new Uri(new Uri(options.CentralApiBaseUrl), "api/v1/central"), cancellationToken);
            if (identity is null || identity.Id == Guid.Empty) return;
            centrals[identity.Id] = new DiscoveredCentral(identity.Id, identity.Name, options.CentralApiBaseUrl, DateTimeOffset.UtcNow, "configured");
            logger.LogInformation("Cluster configuré découvert : {ClusterName} ({ClusterId}).", identity.Name, identity.Id);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug("Le point de découverte de central configuré n’est pas encore disponible.");
        }
    }

    private void OnServiceFound(ServiceProfile service)
    {
        if (!service.Properties.TryGetValue("centralId", out var idValue)
            || !Guid.TryParse(idValue, out var centralId)) return;

        var apiBaseUrl = service.Properties.TryGetValue("apiUrl", out var configuredUrl)
            ? configuredUrl
            : $"http://{service.Hostname.TrimEnd('.') }:{service.Port}/";
        if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;

        var clusterName = service.Properties.TryGetValue("centralName", out var configuredName) && !string.IsNullOrWhiteSpace(configuredName)
            ? configuredName
            : service.InstanceName;
        var central = new DiscoveredCentral(centralId, clusterName, uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : uri.AbsoluteUri + '/', DateTimeOffset.UtcNow);
        centrals.AddOrUpdate(centralId, central, (_, existing) => existing.Source == "configured" ? existing : central);
        logger.LogInformation("Cluster découvert : {ClusterName} ({ClusterId}).", central.Name, central.Id);
    }

    private void OnServiceLost(ServiceProfile service)
    {
        if (service.Properties.TryGetValue("centralId", out var idValue) && Guid.TryParse(idValue, out var centralId))
            centrals.TryRemove(centralId, out _);
    }

    public override void Dispose()
    {
        browser?.Dispose();
        browser = null;
        base.Dispose();
    }

    private sealed record CentralIdentity(Guid Id, string Name);
}
