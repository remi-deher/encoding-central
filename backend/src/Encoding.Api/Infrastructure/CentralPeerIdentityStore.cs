using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Encoding.Api.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;

namespace Encoding.Api.Infrastructure;

/// <summary>One persistent server identity per local API installation.</summary>
public sealed class CentralPeerIdentityStore : IDisposable
{
    private sealed record ProtectedIdentity(Guid PeerId, string PrivateKey);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector protector;
    private readonly string filePath;
    private readonly RSA privateKey;

    public static string ResolveKeyDirectory(IConfiguration configuration, string? defaultRoot = null)
    {
        if (configuration["PluginSettings:KeyDirectory"] is { Length: > 0 } configured)
            return Path.GetFullPath(configured);
        if (configuration["Worker:DataDirectory"] is { Length: > 0 } dataDirectory)
            return Path.Combine(Path.GetFullPath(dataDirectory), "plugin-keys");
        return Path.Combine(defaultRoot ?? AppContext.BaseDirectory, "data", "plugin-keys");
    }

    public CentralPeerIdentityStore(IConfiguration configuration, IDataProtectionProvider protection)
    {
        var directory = Path.Combine(ResolveKeyDirectory(configuration), "cluster-reconciliation");
        Directory.CreateDirectory(directory);
        filePath = Path.Combine(directory, "server-identity.json");
        protector = protection.CreateProtector("Encoding.ClusterPeerIdentity.v1");
        if (File.Exists(filePath))
        {
            var protectedText = File.ReadAllText(filePath);
            var identity = JsonSerializer.Deserialize<ProtectedIdentity>(protector.Unprotect(protectedText), JsonOptions)
                ?? throw new InvalidOperationException("L’identité locale de réconciliation est illisible.");
            PeerId = identity.PeerId;
            privateKey = RSA.Create();
            privateKey.ImportPkcs8PrivateKey(Convert.FromBase64String(identity.PrivateKey), out var consumed);
            if (consumed != Convert.FromBase64String(identity.PrivateKey).Length || privateKey.KeySize < 3072)
                throw new InvalidOperationException("La clé d’identité locale de réconciliation est invalide.");
        }
        else
        {
            PeerId = Guid.NewGuid();
            privateKey = RSA.Create(3072);
            Persist();
        }
    }

    public Guid PeerId { get; }
    public string PublicKey => Convert.ToBase64String(privateKey.ExportSubjectPublicKeyInfo());

    public byte[] Sign(ReadOnlySpan<byte> payload) => privateKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    public byte[] WrapDataKey(ReadOnlySpan<byte> dataKey) => privateKey.Encrypt(dataKey.ToArray(), RSAEncryptionPadding.OaepSHA256);
    public byte[] UnwrapDataKey(ReadOnlySpan<byte> wrapped) => privateKey.Decrypt(wrapped.ToArray(), RSAEncryptionPadding.OaepSHA256);

    public static bool Verify(string publicKey, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        try
        {
            using var rsa = ImportPublicKey(publicKey);
            return rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
    }

    public static RSA ImportPublicKey(string publicKey)
    {
        var encoded = Convert.FromBase64String(publicKey);
        var rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(encoded, out var consumed);
            if (consumed != encoded.Length || rsa.KeySize < 3072)
                throw new CryptographicException("La clé publique doit être une clé RSA d’au moins 3072 bits.");
            return rsa;
        }
        catch { rsa.Dispose(); throw; }
    }

    private void Persist()
    {
        var identity = new ProtectedIdentity(PeerId, Convert.ToBase64String(privateKey.ExportPkcs8PrivateKey()));
        var temporaryPath = filePath + ".tmp";
        File.WriteAllText(temporaryPath, protector.Protect(JsonSerializer.Serialize(identity, JsonOptions)));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporaryPath, filePath, overwrite: true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void Dispose() => privateKey.Dispose();
}
