using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Encoding.Contracts.Cluster;
using Encoding.Api.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;

namespace Encoding.Api.Infrastructure;

/// <summary>One persistent server identity per local API installation.</summary>
public sealed class ClusterPeerIdentityStore : IDisposable
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
        if (configuration["Node:DataDirectory"] is { Length: > 0 } dataDirectory)
            return Path.Combine(Path.GetFullPath(dataDirectory), "plugin-keys");
        return Path.Combine(defaultRoot ?? AppContext.BaseDirectory, "data", "plugin-keys");
    }

    public ClusterPeerIdentityStore(IConfiguration configuration, IDataProtectionProvider protection)
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

/// <summary>Creates grants, transfers keys, and provides the active trust set used by the reconciliation mesh.</summary>
public sealed class ClusterPeerAdmissionRepository(
    DbDataSource dataSource,
    ClusterIdentityRepository clusterIdentity,
    ClusterPeerIdentityStore localIdentity,
    ClusterDataKeyStore dataKeyStore)
{
    private sealed record UnsignedGrant(Guid ClusterId, Guid PeerId, Guid? NodeId, string PublicKey,
        Guid ApprovedByPeerId, string? WrappedDataKey, string KeyFingerprint, DateTimeOffset IssuedAt,
        DateTimeOffset? RevokedAt);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task EnsureRootAdmissionAsync(Guid clusterId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await EnsureRootAdmissionAsync(connection, transaction, clusterId, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task EnsureRootAdmissionAsync(DbConnection connection, DbTransaction transaction, Guid clusterId, CancellationToken ct)
    {
        var existing = await FindAsync(connection, transaction, clusterId, localIdentity.PeerId, ct);
        if (existing is not null)
        {
            if (existing.ClusterId != clusterId || existing.PublicKey != localIdentity.PublicKey || existing.ApprovedByPeerId != localIdentity.PeerId)
                throw new InvalidOperationException("L’identité de ce serveur ne correspond pas à son admission enregistrée dans le cluster.");
            if (string.IsNullOrWhiteSpace(existing.WrappedDataKey)
                || !ClusterPeerIdentityStore.Verify(localIdentity.PublicKey, GrantSigningBytes(existing), Convert.FromBase64String(existing.Signature)))
                throw new InvalidOperationException("L’admission racine du cluster est invalide.");
            var rootKey = localIdentity.UnwrapDataKey(Convert.FromBase64String(existing.WrappedDataKey));
            if (rootKey.Length != 32 || existing.KeyFingerprint != Fingerprint(rootKey))
                throw new InvalidOperationException("La clé de données de l’admission racine est invalide.");
            byte[]? installedKey = null;
            try { installedKey = dataKeyStore.GetRequired(clusterId); }
            catch (InvalidOperationException) { }
            if (installedKey is null || !CryptographicOperations.FixedTimeEquals(rootKey, installedKey))
                dataKeyStore.InstallVerified(clusterId, rootKey);
            return;
        }
        var existingGrants = await ListGrantsAsync(connection, transaction, clusterId, ct);
        if (existingGrants.Any(grant => grant.NodeId is not null && grant.RevokedAt is null))
            throw new InvalidOperationException("La clé de données du cluster n’a pas encore été installée sur ce serveur.");
        if (existingGrants.Count > 0)
        {
            await using var clearStale = Sql.Command("DELETE FROM cluster_peer_admissions WHERE cluster_id = @cluster_id", connection, transaction);
            clearStale.Parameters.AddWithValue("cluster_id", clusterId);
            await clearStale.ExecuteNonQueryAsync(ct);
        }
        var dataKey = dataKeyStore.GetOrCreate(clusterId);
        var rootEnvelope = Convert.ToBase64String(localIdentity.WrapDataKey(dataKey));
        var grant = CreateGrant(clusterId, localIdentity.PeerId, null, localIdentity.PublicKey,
            localIdentity.PeerId, rootEnvelope, Fingerprint(dataKey), revokedAt: null);
        await InsertGrantAsync(connection, transaction, grant, ct);
    }

    public async Task CreateApprovalGrantAsync(DbConnection connection, DbTransaction transaction,
        Guid nodeId, Guid peerId, string publicKey, CancellationToken ct)
    {
        if (peerId == Guid.Empty) throw new ArgumentException("L’identité de pair est vide.", nameof(peerId));
        if (peerId == localIdentity.PeerId) throw new ArgumentException("Un serveur ne peut pas demander son propre rattachement.", nameof(peerId));
        using var subjectKey = ClusterPeerIdentityStore.ImportPublicKey(publicKey);
        await using var identityCommand = Sql.Command("SELECT id FROM cluster_identity WHERE singleton = true", connection, transaction);
        var clusterIdValue = await identityCommand.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("L’identité du cluster est absente.");
        var clusterId = clusterIdValue is Guid id ? id : Guid.Parse(Convert.ToString(clusterIdValue, System.Globalization.CultureInfo.InvariantCulture)!);
        if (!dataKeyStore.HasKey(clusterId))
            await EnsureRootAdmissionAsync(connection, transaction, clusterId, ct);
        var dataKey = dataKeyStore.GetRequired(clusterId);
        var wrapped = Convert.ToBase64String(subjectKey.Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256));
        await EnsureApproverActiveAsync(connection, transaction, clusterId, ct);
        var grant = CreateGrant(clusterId, peerId, nodeId, publicKey, localIdentity.PeerId, wrapped, Fingerprint(dataKey), revokedAt: null);
        await InsertGrantAsync(connection, transaction, grant, ct);
    }

    public async Task RevokeNodeAdmissionAsync(DbConnection connection, DbTransaction transaction, Guid nodeId, CancellationToken ct)
    {
        await using var command = Sql.Command("""
            SELECT cluster_id, peer_id, node_id, public_key, approved_by_peer_id, wrapped_data_key,
                   key_fingerprint, issued_at, revoked_at, signature
            FROM cluster_peer_admissions
            WHERE node_id = @node_id AND revoked_at IS NULL
            """, connection, transaction);
        command.Parameters.AddWithValue("node_id", nodeId);
        var existing = new List<ClusterPeerAdmissionGrant>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) existing.Add(ReadGrant(reader));

        // A server cannot revoke its own API admission by deleting its worker record.
        existing.RemoveAll(grant => grant.PeerId == localIdentity.PeerId);
        if (existing.Count == 0) return;
        foreach (var grant in existing)
        {
            await EnsureApproverActiveAsync(connection, transaction, grant.ClusterId, ct);
            var dataKey = dataKeyStore.GetRequired(grant.ClusterId);
            using var subjectKey = ClusterPeerIdentityStore.ImportPublicKey(grant.PublicKey);
            var wrapped = Convert.ToBase64String(subjectKey.Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256));
            var revoked = CreateGrant(grant.ClusterId, grant.PeerId, grant.NodeId, grant.PublicKey,
                localIdentity.PeerId, wrapped, Fingerprint(dataKey), DateTimeOffset.UtcNow);
            await InsertGrantAsync(connection, transaction, revoked, ct);
        }
    }

    public async Task CreateDirectApprovalGrantAsync(Guid nodeId, Guid peerId, string publicKey, CancellationToken ct)
    {
        if (peerId == localIdentity.PeerId) return;
        var current = await FindAsync(peerId, ct);
        if (current is { RevokedAt: null } && current.NodeId == nodeId && current.PublicKey == publicKey) return;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await CreateApprovalGrantAsync(connection, transaction, nodeId, peerId, publicKey, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<string?> GetActivePublicKeyAsync(Guid clusterId, Guid peerId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("SELECT public_key FROM cluster_peer_admissions WHERE cluster_id = @cluster_id AND peer_id = @peer_id AND revoked_at IS NULL");
        command.Parameters.AddWithValue("cluster_id", clusterId);
        command.Parameters.AddWithValue("peer_id", peerId);
        return await command.ExecuteScalarAsync(ct) is string value ? value : null;
    }

    public async Task<ClusterJoinBootstrap?> GetBootstrapForNodeAsync(Guid nodeId, CancellationToken ct)
    {
        var cluster = await clusterIdentity.GetAsync(ct);
        var grants = await ListGrantsAsync(cluster.Id, ct);
        if (grants.FirstOrDefault(grant => grant.NodeId == nodeId && grant.RevokedAt is null) is not { } admission
            || string.IsNullOrWhiteSpace(admission.WrappedDataKey)) return null;
        var users = new List<ClusterUserName>();
        await using (var command = dataSource.CreateCommand("SELECT id,username FROM users ORDER BY id"))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) users.Add(new(reader.GetGuid(0), reader.GetString(1)));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var signature = ClusterReconciliationSignature.Create(localIdentity, timestamp, UserNameSigningBytes(cluster.Id, admission.PeerId, users));
        return new ClusterJoinBootstrap(cluster.Id, cluster.Name, admission.PeerId, grants,
            new(localIdentity.PeerId, timestamp, users, signature));
    }

    private static byte[] UserNameSigningBytes(Guid cluster, Guid recipient, IReadOnlyList<ClusterUserName> users) =>
        JsonSerializer.SerializeToUtf8Bytes(new { cluster, recipient, users }, JsonOptions);

    public void ValidateJoinBootstrap(ClusterJoinBootstrap bootstrap, Guid nodeId) => ValidateBootstrap(bootstrap, nodeId);

    public async Task ImportBootstrapAsync(ClusterJoinBootstrap bootstrap, Guid expectedNodeId, CancellationToken ct)
    {
        var dataKey = ValidateBootstrap(bootstrap, expectedNodeId);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var grant in bootstrap.Grants)
            await InsertGrantAsync(connection, transaction, grant, ct);
        await transaction.CommitAsync(ct);
        dataKeyStore.InstallVerified(bootstrap.ClusterId, dataKey);
    }

    public async Task<bool> RecoverLocalDataKeyAsync(CancellationToken ct)
    {
        var cluster = await clusterIdentity.GetAsync(ct);
        var grants = await ListGrantsAsync(cluster.Id, ct);
        if (grants.All(grant => grant.PeerId != localIdentity.PeerId || grant.RevokedAt is not null)) return false;
        var bootstrap = new ClusterJoinBootstrap(cluster.Id, cluster.Name, localIdentity.PeerId, grants);
        dataKeyStore.InstallVerified(cluster.Id, ValidateBootstrap(bootstrap, expectedNodeId: null));
        return true;
    }

    private byte[] ValidateBootstrap(ClusterJoinBootstrap bootstrap, Guid? expectedNodeId)
    {
        if (bootstrap.ClusterId == Guid.Empty || bootstrap.PeerId != localIdentity.PeerId
            || expectedNodeId == Guid.Empty
            || !ClusterIdentityRepository.IsValidName(bootstrap.ClusterName)
            || bootstrap.Grants.Count is 0 or > 4096)
            throw new ArgumentException("Les données de rattachement du cluster sont invalides.", nameof(bootstrap));

        var byPeer = bootstrap.Grants.GroupBy(grant => grant.PeerId).ToDictionary(group => group.Key, group => group.Single());
        if (byPeer.Count != bootstrap.Grants.Count) throw new ArgumentException("Le rattachement contient des identités de pair en doublon.", nameof(bootstrap));
        var roots = bootstrap.Grants.Where(grant => grant.PeerId == grant.ApprovedByPeerId).ToArray();
        if (roots.Length != 1 || roots[0].NodeId is not null || roots[0].RevokedAt is not null || string.IsNullOrWhiteSpace(roots[0].WrappedDataKey))
            throw new ArgumentException("Le rattachement doit contenir une seule identité racine valide.", nameof(bootstrap));
        var rootId = roots[0].PeerId;
        if (!IsFingerprint(roots[0].KeyFingerprint)
            || bootstrap.Grants.Any(grant => !string.Equals(grant.KeyFingerprint, roots[0].KeyFingerprint, StringComparison.Ordinal)))
            throw new ArgumentException("Les admissions ne partagent pas la même clé de cluster.", nameof(bootstrap));
        var trusted = new HashSet<Guid>();
        var remaining = bootstrap.Grants.ToList();
        var progressed = true;
        while (remaining.Count > 0 && progressed)
        {
            progressed = false;
            for (var index = remaining.Count - 1; index >= 0; index--)
            {
                var grant = remaining[index];
                if (grant.ClusterId != bootstrap.ClusterId || grant.PeerId == Guid.Empty || grant.ApprovedByPeerId == Guid.Empty) continue;
                if (grant.PeerId == grant.ApprovedByPeerId)
                {
                    if (grant.PeerId != rootId || grant.NodeId is not null || string.IsNullOrWhiteSpace(grant.WrappedDataKey)) continue;
                }
                else if (!trusted.Contains(grant.ApprovedByPeerId)) continue;
                else if (byPeer[grant.ApprovedByPeerId].RevokedAt is { } approverRevokedAt
                    && grant.IssuedAt > approverRevokedAt) continue;
                var approverKey = grant.PeerId == grant.ApprovedByPeerId
                    ? grant.PublicKey
                    : byPeer[grant.ApprovedByPeerId].PublicKey;
                if (!ClusterPeerIdentityStore.Verify(approverKey, GrantSigningBytes(grant), Convert.FromBase64String(grant.Signature))) continue;
                trusted.Add(grant.PeerId);
                remaining.RemoveAt(index);
                progressed = true;
            }
        }
        if (remaining.Count > 0 || !byPeer.TryGetValue(localIdentity.PeerId, out var ownGrant)
            || (ownGrant.PeerId != rootId && ownGrant.NodeId is null)
            || (expectedNodeId.HasValue && ownGrant.NodeId != expectedNodeId.Value) || ownGrant.RevokedAt is not null
            || string.IsNullOrWhiteSpace(ownGrant.WrappedDataKey))
            throw new ArgumentException("La chaîne de signatures du cluster est incomplète ou invalide.", nameof(bootstrap));
        if (bootstrap.UserNames is { } names)
        {
            if (names.Users is null || names.Users.Count > 10000
                || names.Users.Select(user => user.Id).Distinct().Count() != names.Users.Count
                || names.Users.Select(user => user.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Users.Count
                || names.Users.Any(user => user.Id == Guid.Empty || !Encoding.Api.Auth.AuthService.IsValidUsername(user.Username))
                || !byPeer.TryGetValue(names.IssuerPeerId, out var issuer) || issuer.RevokedAt is not null
                || !ClusterReconciliationSignature.Verify(issuer.PublicKey, names.IssuerPeerId, names.Timestamp, names.Signature,
                    UserNameSigningBytes(bootstrap.ClusterId, bootstrap.PeerId, names.Users)))
                throw new ArgumentException("La liste des identités du cluster n’est pas authentifiée.");
        }
        var dataKey = localIdentity.UnwrapDataKey(Convert.FromBase64String(ownGrant.WrappedDataKey));
        if (dataKey.Length != 32 || !string.Equals(Fingerprint(dataKey), ownGrant.KeyFingerprint, StringComparison.Ordinal))
            throw new ArgumentException("La clé de données reçue est invalide.", nameof(bootstrap));
        return dataKey;
    }

    public async Task<string?> FindPeerPublicKeyAsync(Guid peerId, CancellationToken ct)
    {
        var cluster = await clusterIdentity.GetAsync(ct);
        return await GetActivePublicKeyAsync(cluster.Id, peerId, ct);
    }

    public async Task<bool> VerifyIncomingGrantAsync(JsonObject row, CancellationToken ct)
    {
        try
        {
            if (!Guid.TryParse(row["cluster_id"]?.GetValue<string>(), out var clusterId)
                || !Guid.TryParse(row["peer_id"]?.GetValue<string>(), out var peerId)
                || !Guid.TryParse(row["approved_by_peer_id"]?.GetValue<string>(), out var approvedBy)
                || row["public_key"]?.GetValue<string>() is not { } publicKey
                || row["issued_at"]?.GetValue<string>() is not { } issuedAt
                || row["key_fingerprint"]?.GetValue<string>() is not { } keyFingerprint
                || row["signature"]?.GetValue<string>() is not { } signature) return false;
            if (clusterId != (await clusterIdentity.GetAsync(ct)).Id) return false;
            var revokedAt = row["revoked_at"]?.GetValue<string>();
            var grant = new ClusterPeerAdmissionGrant(clusterId, peerId,
                Guid.TryParse(row["node_id"]?.GetValue<string>(), out var nodeId) ? nodeId : null,
                publicKey, approvedBy,
                row["wrapped_data_key"]?.GetValue<string>(),
                keyFingerprint,
                DateTimeOffset.Parse(issuedAt, System.Globalization.CultureInfo.InvariantCulture),
                revokedAt is null ? null : DateTimeOffset.Parse(revokedAt, System.Globalization.CultureInfo.InvariantCulture),
                signature);
            using var _ = ClusterPeerIdentityStore.ImportPublicKey(publicKey);
            if (!IsFingerprint(grant.KeyFingerprint)) return false;
            if (grant.PeerId == grant.ApprovedByPeerId)
            {
                var currentRoot = await FindAsync(grant.PeerId, ct);
                return grant.NodeId is null && !string.IsNullOrWhiteSpace(grant.WrappedDataKey) && currentRoot is not null
                    && currentRoot.PublicKey == grant.PublicKey && currentRoot.WrappedDataKey == grant.WrappedDataKey
                    && currentRoot.KeyFingerprint == grant.KeyFingerprint
                    && currentRoot.Signature == grant.Signature
                    && ClusterPeerIdentityStore.Verify(grant.PublicKey, GrantSigningBytes(grant), Convert.FromBase64String(grant.Signature));
            }
            if (grant.NodeId is null || string.IsNullOrWhiteSpace(grant.WrappedDataKey)) return false;
            var approver = await FindAsync(grant.ApprovedByPeerId, ct);
            return approver is not null
                && (approver.RevokedAt is null || grant.IssuedAt <= approver.RevokedAt)
                && ClusterPeerIdentityStore.Verify(approver.PublicKey,
                GrantSigningBytes(grant), Convert.FromBase64String(grant.Signature));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException
            or System.Security.Cryptography.CryptographicException or JsonException or KeyNotFoundException)
        {
            return false;
        }
    }

    public static byte[] GrantSigningBytes(ClusterPeerAdmissionGrant grant) =>
        JsonSerializer.SerializeToUtf8Bytes(new UnsignedGrant(grant.ClusterId, grant.PeerId, grant.NodeId,
            grant.PublicKey, grant.ApprovedByPeerId, grant.WrappedDataKey, grant.KeyFingerprint, grant.IssuedAt, grant.RevokedAt), JsonOptions);

    private ClusterPeerAdmissionGrant CreateGrant(Guid clusterId, Guid peerId, Guid? nodeId, string publicKey,
        Guid approvedByPeerId, string? wrappedDataKey, string keyFingerprint, DateTimeOffset? revokedAt)
    {
        using var _ = ClusterPeerIdentityStore.ImportPublicKey(publicKey);
        var issuedAt = DateTimeOffset.UtcNow;
        var unsigned = new UnsignedGrant(clusterId, peerId, nodeId, publicKey, approvedByPeerId, wrappedDataKey,
            keyFingerprint, issuedAt, revokedAt);
        var signature = Convert.ToBase64String(localIdentity.Sign(JsonSerializer.SerializeToUtf8Bytes(unsigned, JsonOptions)));
        return new ClusterPeerAdmissionGrant(clusterId, peerId, nodeId, publicKey, approvedByPeerId, wrappedDataKey, keyFingerprint, issuedAt, revokedAt, signature);
    }

    private async Task EnsureApproverActiveAsync(DbConnection connection, DbTransaction transaction, Guid clusterId, CancellationToken ct)
    {
        await using var command = Sql.Command("SELECT public_key FROM cluster_peer_admissions WHERE cluster_id = @cluster_id AND peer_id = @peer_id AND revoked_at IS NULL", connection, transaction);
        command.Parameters.AddWithValue("cluster_id", clusterId);
        command.Parameters.AddWithValue("peer_id", localIdentity.PeerId);
        if (await command.ExecuteScalarAsync(ct) is not string publicKey || publicKey != localIdentity.PublicKey)
            throw new InvalidOperationException("Ce serveur n’est pas autorisé à approuver une jonction dans ce cluster.");
    }

    private async Task<ClusterPeerAdmissionGrant?> FindAsync(Guid peerId, CancellationToken ct)
    {
        var cluster = await clusterIdentity.GetAsync(ct);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await FindAsync(connection, transaction: null, cluster.Id, peerId, ct);
    }

    private static async Task<ClusterPeerAdmissionGrant?> FindAsync(DbConnection connection, DbTransaction? transaction, Guid clusterId, Guid peerId, CancellationToken ct)
    {
        await using var command = Sql.Command("SELECT cluster_id, peer_id, node_id, public_key, approved_by_peer_id, wrapped_data_key, key_fingerprint, issued_at, revoked_at, signature FROM cluster_peer_admissions WHERE cluster_id = @cluster_id AND peer_id = @peer_id",
            connection, transaction);
        command.Parameters.AddWithValue("cluster_id", clusterId);
        command.Parameters.AddWithValue("peer_id", peerId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadGrant(reader) : null;
    }

    private async Task<IReadOnlyList<ClusterPeerAdmissionGrant>> ListGrantsAsync(Guid clusterId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await ListGrantsAsync(connection, transaction: null, clusterId, ct);
    }

    private static async Task<IReadOnlyList<ClusterPeerAdmissionGrant>> ListGrantsAsync(DbConnection connection, DbTransaction? transaction, Guid clusterId, CancellationToken ct)
    {
        var grants = new List<ClusterPeerAdmissionGrant>();
        await using var command = Sql.Command("SELECT cluster_id, peer_id, node_id, public_key, approved_by_peer_id, wrapped_data_key, key_fingerprint, issued_at, revoked_at, signature FROM cluster_peer_admissions WHERE cluster_id = @cluster_id ORDER BY issued_at, peer_id",
            connection, transaction);
        command.Parameters.AddWithValue("cluster_id", clusterId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) grants.Add(ReadGrant(reader));
        return grants;
    }

    private async Task InsertGrantAsync(ClusterPeerAdmissionGrant grant, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await InsertGrantAsync(connection, transaction, grant, ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task InsertGrantAsync(DbConnection connection, DbTransaction transaction, ClusterPeerAdmissionGrant grant, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO cluster_peer_admissions (cluster_id, peer_id, node_id, public_key, approved_by_peer_id, wrapped_data_key, key_fingerprint, issued_at, revoked_at, signature)
            VALUES (@cluster_id, @peer_id, @node_id, @public_key, @approved_by_peer_id, @wrapped_data_key, @key_fingerprint, @issued_at, @revoked_at, @signature)
            ON CONFLICT (cluster_id, peer_id) DO UPDATE SET
                node_id = excluded.node_id,
                public_key = excluded.public_key,
                approved_by_peer_id = excluded.approved_by_peer_id,
                wrapped_data_key = excluded.wrapped_data_key,
                key_fingerprint = excluded.key_fingerprint,
                issued_at = excluded.issued_at,
                revoked_at = excluded.revoked_at,
                signature = excluded.signature
            WHERE cluster_peer_admissions.public_key = excluded.public_key
            """;
        await using var command = Sql.Command(sql, connection, transaction);
        command.Parameters.AddWithValue("cluster_id", grant.ClusterId);
        command.Parameters.AddWithValue("peer_id", grant.PeerId);
        command.Parameters.AddWithValue("node_id", (object?)grant.NodeId ?? DBNull.Value);
        command.Parameters.AddWithValue("public_key", grant.PublicKey);
        command.Parameters.AddWithValue("approved_by_peer_id", grant.ApprovedByPeerId);
        command.Parameters.AddWithValue("wrapped_data_key", (object?)grant.WrappedDataKey ?? DBNull.Value);
        command.Parameters.AddWithValue("key_fingerprint", grant.KeyFingerprint);
        command.Parameters.AddWithValue("issued_at", grant.IssuedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("revoked_at", grant.RevokedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("signature", grant.Signature);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static ClusterPeerAdmissionGrant ReadGrant(DbDataReader reader) => new(
        ReadGuid(reader, 0), ReadGuid(reader, 1), reader.IsDBNull(2) ? null : ReadGuid(reader, 2), reader.GetString(3),
        ReadGuid(reader, 4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), ReadDate(reader.GetValue(7)),
        reader.IsDBNull(8) ? null : ReadDate(reader.GetValue(8)), reader.GetString(9));

    private static string Fingerprint(ReadOnlySpan<byte> key) => Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant();
    private static bool IsFingerprint(string value) => value.Length == 64 && value.All(Uri.IsHexDigit) && value == value.ToLowerInvariant();

    private static Guid ReadGuid(DbDataReader reader, int ordinal) => reader.GetValue(ordinal) is Guid id
        ? id : Guid.Parse(Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture)!);

    private static DateTimeOffset ReadDate(object value) => value switch
    {
        DateTime date => new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)),
        _ => DateTimeOffset.Parse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!,
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal)
    };
}
