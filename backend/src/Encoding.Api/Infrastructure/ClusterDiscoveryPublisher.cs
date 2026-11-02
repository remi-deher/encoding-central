using Haukcode.Mdns;
using Encoding.Core.Nodes;

namespace Encoding.Api.Infrastructure;

public sealed class ClusterDiscoveryPublisher(
    ClusterIdentityRepository identityRepository,
    IConfiguration configuration,
    ILogger<ClusterDiscoveryPublisher> logger,
    LauncherControlClient? launcher = null) : BackgroundService
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
                var mode = launcher is null
                    ? NodeMode.Parse(configuration["Node:Roles"])
                    : await launcher.GetAsync(stoppingToken);
                if (mode is null || !mode.Server)
                {
                    advertiser?.Dispose();
                    advertiser = null;
                    announcedIdentity = null;
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                    continue;
                }
                var configuredPort = configuration.GetValue("Cluster:DiscoveryPort", 8080);
                if (configuredPort is < 1 or > 65535) throw new InvalidOperationException("Cluster:DiscoveryPort doit être compris entre 1 et 65535.");
                var apiUrl = configuration["Cluster:AdvertisedApiBaseUrl"];
                var reconciliationApiUrl = configuration["Cluster:Reconciliation:AdvertisedApiBaseUrl"];
                var instanceName = configuration["Cluster:DiscoveryInstanceName"]?.Trim();
                if (string.IsNullOrWhiteSpace(instanceName)) instanceName = Environment.MachineName;
                var signature = $"{identity.Id:D}|{identity.Name}|{instanceName}|{configuredPort}|{apiUrl}|{reconciliationApiUrl}|{mode.Server}";
                if (!string.Equals(signature, announcedIdentity, StringComparison.Ordinal))
                {
                    advertiser?.Dispose();
                    var properties = new Dictionary<string, string>
                    {
                        ["clusterId"] = identity.Id.ToString("D"),
                        ["clusterName"] = identity.Name,
                        ["roles"] = mode.ToRoles()
                    };
                    if (!string.IsNullOrWhiteSpace(apiUrl)) properties["apiUrl"] = apiUrl;
                    if (!string.IsNullOrWhiteSpace(reconciliationApiUrl)) properties["reconciliationApiUrl"] = reconciliationApiUrl;
                    advertiser = new MdnsAdvertiser(new ServiceProfile(instanceName, "_encoding._tcp", (ushort)configuredPort, properties));
                    advertiser.Start();
                    announcedIdentity = signature;
                    logger.LogInformation("Le cluster {ClusterId} est annoncé sur le réseau local via DNS-SD.", identity.Id);
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
