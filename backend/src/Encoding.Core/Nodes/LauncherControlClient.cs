using System.Net.Http.Json;

namespace Encoding.Core.Nodes;

/// <summary>
/// Client de l’API de contrôle locale du lanceur de l’image unique (voir l’architecture 0036). Le serveur et le worker
/// s’en servent pour lire et changer le mode de leur propre nœud ; chaque nœud ne pilote que lui-même.
/// </summary>
public sealed class LauncherControlClient(HttpClient client)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Retourne null quand le nœud n’est pas géré par un lanceur (installation sans image unique).</summary>
    public async Task<NodeMode?> GetAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetFromJsonAsync<NodeMode>("mode", cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public async Task<NodeMode> SetAsync(NodeMode mode, CancellationToken cancellationToken)
    {
        using var response = await client.PutAsJsonAsync("mode", mode, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await response.Content.ReadAsStringAsync(cancellationToken));
        return (await response.Content.ReadFromJsonAsync<NodeMode>(cancellationToken))!;
    }
}
