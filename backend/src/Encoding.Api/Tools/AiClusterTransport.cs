using Encoding.Api.Infrastructure;

namespace Encoding.Api.Tools;

/// <summary>
/// Identité locale du service IA. Le central est l'unique serveur ([ADR 0036](../../../../docs/architecture/adr/0036-separation-stricte-central-worker.md)) :
/// il n'existe plus de pair auquel déléguer une inférence, et aucune route d'échange n'est exposée.
/// </summary>
public sealed class AiClusterTransport(ClusterPeerIdentityStore identity)
{
    public Guid LocalId => identity.PeerId;

    /// <summary>Aucun autre serveur : la liste est toujours vide.</summary>
    public Task<IReadOnlyList<string>> PeersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>Aucun pair ne peut répondre sous un autre identifiant.</summary>
    public Task<string?> FindNodeUrlAsync(Guid nodeId, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<T> CallAsync<T>(string url, string operation, object value, TimeSpan timeout, CancellationToken ct) =>
        throw new InvalidOperationException("Aucun autre serveur n'est disponible : le central est l'unique serveur.");

    public Task<T> CallNodeAsync<T>(Guid nodeId, string operation, object value, TimeSpan timeout, CancellationToken ct) =>
        throw new InvalidOperationException("Le coordinateur de cette conversation est indisponible : le central est l'unique serveur.");

    private const int MaxRpcBytes = 16 * 1024 * 1024;

    /// <summary>Lecture bornée d'un flux : un fournisseur ne peut pas faire allouer une réponse sans limite.</summary>
    internal static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > MaxRpcBytes) throw new ArgumentException("Message IA trop volumineux.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return output.ToArray();
    }
}
