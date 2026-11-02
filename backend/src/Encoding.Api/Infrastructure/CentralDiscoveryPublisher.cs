using Haukcode.Mdns;

namespace Encoding.Api.Infrastructure;

public sealed class CentralDiscoveryPublisher(
    CentralIdentityRepository identityRepository,
    IConfiguration configuration,
    ILogger<CentralDiscoveryPublisher> logger) : BackgroundService
{
    private MdnsAdvertiser? advertiser;
    private string? announcedIdentity;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var identity = await identityRepository.GetAsync(stoppingToken);
                var configuredPort = configuration.GetValue("Central:DiscoveryPort", 8080);
                if (configuredPort is < 1 or > 65535) throw new InvalidOperationException("Central:DiscoveryPort doit être compris entre 1 et 65535.");
                var apiUrl = configuration["Central:AdvertisedApiBaseUrl"];
                var reconciliationApiUrl = configuration["Central:Reconciliation:AdvertisedApiBaseUrl"];
                var instanceName = configuration["Central:DiscoveryInstanceName"]?.Trim();
                if (string.IsNullOrWhiteSpace(instanceName)) instanceName = Environment.MachineName;
                var signature = $"{identity.Id:D}|{identity.Name}|{instanceName}|{configuredPort}|{apiUrl}|{reconciliationApiUrl}";
                if (!string.Equals(signature, announcedIdentity, StringComparison.Ordinal))
                {
                    advertiser?.Dispose();
                    var properties = new Dictionary<string, string>
                    {
                        ["clusterId"] = identity.Id.ToString("D"),
                        ["clusterName"] = identity.Name,
                        ["roles"] = "server"
                    };
                    if (!string.IsNullOrWhiteSpace(apiUrl)) properties["apiUrl"] = apiUrl;
                    if (!string.IsNullOrWhiteSpace(reconciliationApiUrl)) properties["reconciliationApiUrl"] = reconciliationApiUrl;
                    advertiser = new MdnsAdvertiser(new ServiceProfile(instanceName, "_encoding._tcp", (ushort)configuredPort, properties));
                    advertiser.Start();
                    announcedIdentity = signature;
                    logger.LogInformation("Le central {ClusterId} est annoncé sur le réseau local via DNS-SD.", identity.Id);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                advertiser?.Dispose();
                advertiser = null;
                announcedIdentity = null;
                logger.LogWarning(exception, "La découverte DNS-SD est indisponible ; les workers peuvent utiliser l’adresse directe du serveur.");
            }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    public override void Dispose()
    {
        advertiser?.Dispose();
        advertiser = null;
        base.Dispose();
    }
}
