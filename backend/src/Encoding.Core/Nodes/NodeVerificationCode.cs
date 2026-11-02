using System.Security.Cryptography;

namespace Encoding.Core.Nodes;

/// <summary>
/// Code court à comparer à l'œil lors d'un rattachement (0040) : le nœud et le cluster d'accueil le calculent chacun de leur côté
/// à partir de l'identifiant du nœud, de l'empreinte de son jeton et, pour un serveur, de sa clé publique de pair. Aucun secret ne circule.
/// </summary>
public static class NodeVerificationCode
{
    // Sans caractères ambigus (0/O, 1/I).
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int Length = 6;

    public static string FromToken(Guid nodeId, string token, string? clusterPeerPublicKey = null) =>
        FromTokenHash(nodeId, SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)), clusterPeerPublicKey);

    public static string FromTokenHash(Guid nodeId, byte[] tokenHash, string? clusterPeerPublicKey = null)
    {
        var peerKeyHash = string.IsNullOrWhiteSpace(clusterPeerPublicKey)
            ? []
            : SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(clusterPeerPublicKey));
        var digest = SHA256.HashData([.. nodeId.ToByteArray(), .. tokenHash, .. peerKeyHash]);
        var chars = new char[Length];
        for (var index = 0; index < Length; index++) chars[index] = Alphabet[digest[index] % Alphabet.Length];
        return new string(chars);
    }
}
