using System.Globalization;

namespace Encoding.Api.Infrastructure;

/// <summary>Authenticates the mesh exchange with each server's private signing key.</summary>
public static class ClusterReconciliationSignature
{
    public const string PeerHeader = "X-Encoding-Cluster-Reconciliation-Peer";
    public const string TimeHeader = "X-Encoding-Cluster-Reconciliation-Time";
    public const string SignatureHeader = "X-Encoding-Cluster-Reconciliation-Signature";
    public const int MaximumBodyBytes = 16 * 1024 * 1024;
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(2);

    public static string Create(ClusterPeerIdentityStore identity, string timestamp, ReadOnlySpan<byte> body) =>
        Convert.ToBase64String(identity.Sign(SigningBytes(identity.PeerId, timestamp, body)));

    public static bool Verify(string publicKey, Guid peerId, string? timestamp, string? signature,
        ReadOnlySpan<byte> body, DateTimeOffset? now = null)
    {
        if (peerId == Guid.Empty || string.IsNullOrWhiteSpace(timestamp) || string.IsNullOrWhiteSpace(signature)
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return false;
        DateTimeOffset requestTime;
        try { requestTime = DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return false; }
        if (Math.Abs(((now ?? DateTimeOffset.UtcNow) - requestTime).TotalSeconds) > MaximumClockSkew.TotalSeconds) return false;
        byte[] provided;
        try { provided = Convert.FromBase64String(signature); }
        catch (FormatException) { return false; }
        return ClusterPeerIdentityStore.Verify(publicKey, SigningBytes(peerId, timestamp, body), provided);
    }

    private static byte[] SigningBytes(Guid peerId, string timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = System.Text.Encoding.UTF8.GetBytes(peerId.ToString("D") + "\n" + timestamp + "\n");
        var result = new byte[prefix.Length + body.Length];
        prefix.CopyTo(result, 0);
        body.CopyTo(result.AsSpan(prefix.Length));
        return result;
    }
}
