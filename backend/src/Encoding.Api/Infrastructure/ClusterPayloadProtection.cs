using System.Security.Cryptography;

namespace Encoding.Api.Infrastructure;

/// <summary>AES-GCM cluster payloads, bound to their domain. Shared by mesh secrets and durable task checkpoints.</summary>
public static class ClusterPayloadProtection
{
    private static byte[] Key(byte[] key)=>HMACSHA256.HashData(key,System.Text.Encoding.UTF8.GetBytes("Encoding.ClusterReconciliation.Data.v2"));
    public static string Protect(byte[] key,byte[] data,string domain)
    {
        var nonce=RandomNumberGenerator.GetBytes(12);var tag=new byte[16];var cipher=new byte[data.Length];
        using var aes=new AesGcm(Key(key),16);aes.Encrypt(nonce,data,cipher,tag,System.Text.Encoding.UTF8.GetBytes(domain));
        return Convert.ToBase64String([..nonce,..tag,..cipher]);
    }
    public static byte[] Unprotect(byte[] key,string value,string domain)
    {
        var packed=Convert.FromBase64String(value);if(packed.Length<28) throw new CryptographicException("Charge chiffrée invalide.");
        var plain=new byte[packed.Length-28];using var aes=new AesGcm(Key(key),16);
        aes.Decrypt(packed.AsSpan(0,12),packed.AsSpan(28),packed.AsSpan(12,16),plain,System.Text.Encoding.UTF8.GetBytes(domain));return plain;
    }
}
