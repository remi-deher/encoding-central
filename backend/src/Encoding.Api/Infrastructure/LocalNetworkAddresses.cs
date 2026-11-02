using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Encoding.Contracts.Auth;

namespace Encoding.Api.Infrastructure;

/// <summary>
/// Adresses IPv4 et IPv6 de toutes les interfaces vues par ce processus.
/// Dans un conteneur en réseau « bridge », ce sont les adresses du conteneur, pas celle de l'hôte : l'information est
/// observée, jamais présentée comme l'adresse de joignabilité du nœud (celle-ci reste l'URL publique ou annoncée).
/// </summary>
public static class LocalNetworkAddresses
{
    public static IReadOnlyList<SetupNodeAddress> Current()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses.Select(unicast => (nic.Name, unicast.Address)));
            return Select(candidates);
        }
        catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
        {
            // Best-effort : l'absence d'adresse n'empêche pas l'installation.
            return [];
        }
    }

    /// <summary>Adresses assignées, dédoublonnées par interface et IP ; inclut boucle locale, lien local et interfaces virtuelles.</summary>
    public static IReadOnlyList<SetupNodeAddress> Select(IEnumerable<(string Interface, IPAddress Address)> candidates) =>
        candidates
            .Where(item => item.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .DistinctBy(item => (item.Interface, item.Address))
            .OrderBy(item => item.Interface, StringComparer.Ordinal)
            .ThenBy(item => item.Address.AddressFamily)
            .ThenBy(item => item.Address.ToString(), StringComparer.Ordinal)
            .Select(item => new SetupNodeAddress(item.Interface, item.Address.ToString()))
            .ToArray();
}
