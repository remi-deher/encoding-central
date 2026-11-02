using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Encoding.Contracts.Cluster;
using Encoding.Api.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;

namespace Encoding.Api.Infrastructure;

/// <summary>Stores the cluster-wide data key encrypted with this server's local Data Protection key ring.</summary>
public sealed class ClusterDataKeyStore : IDisposable
{
    private sealed record ProtectedKey(Guid ClusterId, string Key);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector protector;
    private readonly string filePath;
    private readonly object gate = new();
    private readonly Guid? memoryClusterId;
    private readonly byte[]? memoryKey;

    public ClusterDataKeyStore(Guid clusterId, byte[] key)
    {
        if (clusterId == Guid.Empty || key.Length != 32) throw new ArgumentException("La clé de données du cluster est invalide.");
        memoryClusterId = clusterId;
        memoryKey = key.ToArray();
        protector = null!;
        filePath = string.Empty;
    }

    public ClusterDataKeyStore(IConfiguration configuration, IDataProtectionProvider protection)
    {
        var directory = Path.Combine(ClusterPeerIdentityStore.ResolveKeyDirectory(configuration), "cluster-reconciliation");
        Directory.CreateDirectory(directory);
        filePath = Path.Combine(directory, "cluster-data-key.json");
        protector = protection.CreateProtector("Encoding.ClusterDataKey.v1");
    }

    public bool HasKey(Guid clusterId)
    {
        if (memoryKey is not null) return memoryClusterId == clusterId;
        lock (gate)
        {
            var record = Read();
            return record?.ClusterId == clusterId;
        }
    }

    public byte[] GetOrCreate(Guid clusterId)
    {
        if (memoryKey is not null)
        {
            if (memoryClusterId != clusterId) throw new InvalidOperationException("La clé locale appartient à un autre cluster.");
            return memoryKey.ToArray();
        }
        lock (gate)
        {
            var existing = Read();
            if (existing is not null)
            {
                if (existing.ClusterId != clusterId) throw new InvalidOperationException("La clé locale appartient à un autre cluster.");
                return Convert.FromBase64String(existing.Key);
            }
            var key = RandomNumberGenerator.GetBytes(32);
            Write(new ProtectedKey(clusterId, Convert.ToBase64String(key)));
            return key;
        }
    }

    public byte[] GetRequired(Guid clusterId)
    {
        if (memoryKey is not null)
        {
            if (memoryClusterId != clusterId) throw new InvalidOperationException("La clé locale appartient à un autre cluster.");
            return memoryKey.ToArray();
        }
        lock (gate)
        {
            var record = Read();
            if (record is null || record.ClusterId != clusterId)
                throw new InvalidOperationException("La clé de données du cluster n’a pas encore été installée sur ce serveur.");
            return Convert.FromBase64String(record.Key);
        }
    }

    public void Install(Guid clusterId, ReadOnlySpan<byte> key)
    {
        if (clusterId == Guid.Empty || key.Length != 32) throw new ArgumentException("La clé de données du cluster est invalide.");
        if (memoryKey is not null)
        {
            if (memoryClusterId != clusterId || !CryptographicOperations.FixedTimeEquals(memoryKey, key))
                throw new InvalidOperationException("La clé mémoire diffère de la clé du cluster.");
            return;
        }
        lock (gate)
        {
            var current = Read();
            if (current?.ClusterId == clusterId && !CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(current.Key), key))
                throw new InvalidOperationException("Une clé différente est déjà installée pour ce cluster.");
            Write(new ProtectedKey(clusterId, Convert.ToBase64String(key)));
        }
    }

    internal void InstallVerified(Guid clusterId, ReadOnlySpan<byte> key)
    {
        if (memoryKey is not null)
        {
            Install(clusterId, key);
            return;
        }
        if (clusterId == Guid.Empty || key.Length != 32) throw new ArgumentException("La clé de données du cluster est invalide.");
        lock (gate) Write(new ProtectedKey(clusterId, Convert.ToBase64String(key)));
    }

    private ProtectedKey? Read()
    {
        if (!File.Exists(filePath)) return null;
        try
        {
            var protectedText = File.ReadAllText(filePath);
            var result = JsonSerializer.Deserialize<ProtectedKey>(protector.Unprotect(protectedText), JsonOptions);
            if (result is null || Convert.FromBase64String(result.Key).Length != 32) throw new InvalidOperationException();
            return result;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException or InvalidOperationException)
        {
            throw new InvalidOperationException("La clé de données locale est illisible. Restaurez la sauvegarde du volume de clés.", exception);
        }
    }

    private void Write(ProtectedKey key)
    {
        var temporaryPath = filePath + ".tmp";
        File.WriteAllText(temporaryPath, protector.Protect(JsonSerializer.Serialize(key, JsonOptions)));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporaryPath, filePath, overwrite: true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void Dispose() { }
}
