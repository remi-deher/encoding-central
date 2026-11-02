using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class ClusterPeerSecurityTests
{
    [Fact]
    public async Task ApprovedPeerReceivesClusterKeyAndSignedTrustChain()
    {
        var root = Path.Combine(Path.GetTempPath(), "encoding-peer-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var source = await TestDatabase.CreateSqliteAsync("peer-security-source");
            await using var joining = await TestDatabase.CreateSqliteAsync("peer-security-joining");
            var migrations = Harness.RepositoryRoot();
            await DatabaseMigrator.ApplyAsync(source.Db, migrations, CancellationToken.None);
            await DatabaseMigrator.ApplyAsync(joining.Db, migrations, CancellationToken.None);

            var clusterId = Guid.NewGuid();
            var nodeId = Guid.NewGuid();
            await new ClusterIdentityRepository(source.Db).AdoptAsync(clusterId, "Media", CancellationToken.None);
            await new ClusterIdentityRepository(joining.Db).AdoptAsync(Guid.NewGuid(), "Autonome", CancellationToken.None);

            using var sourceIdentity = CreateIdentity(Path.Combine(root, "source-keys"));
            using var joiningIdentity = CreateIdentity(Path.Combine(root, "joining-keys"));
            var sharedKey = RandomNumberGenerator.GetBytes(32);
            using var sourceDataKey = new ClusterDataKeyStore(clusterId, sharedKey);
            var joiningDataKey = new ClusterDataKeyStore(Configuration(Path.Combine(root, "joining-keys")), new EphemeralDataProtectionProvider());
            var sourceAdmissions = new ClusterPeerAdmissionRepository(source.Db, new ClusterIdentityRepository(source.Db), sourceIdentity, sourceDataKey);
            var joiningAdmissions = new ClusterPeerAdmissionRepository(joining.Db, new ClusterIdentityRepository(joining.Db), joiningIdentity, joiningDataKey);

            await sourceAdmissions.EnsureRootAdmissionAsync(clusterId, CancellationToken.None);
            await sourceAdmissions.CreateDirectApprovalGrantAsync(nodeId, joiningIdentity.PeerId, joiningIdentity.PublicKey, CancellationToken.None);
            var bootstrap = await sourceAdmissions.GetBootstrapForNodeAsync(nodeId, CancellationToken.None);
            Assert.NotNull(bootstrap);
            Assert.DoesNotContain(Convert.ToBase64String(sharedKey), JsonSerializer.Serialize(bootstrap));

            var ownGrant = Assert.Single(bootstrap.Grants, grant => grant.PeerId == joiningIdentity.PeerId);
            var invalidGrant = ownGrant with { Signature = Convert.ToBase64String(RandomNumberGenerator.GetBytes(384)) };
            var forged = bootstrap with { Grants = bootstrap.Grants.Select(grant => grant.PeerId == ownGrant.PeerId ? invalidGrant : grant).ToArray() };
            await Assert.ThrowsAsync<ArgumentException>(() => joiningAdmissions.ImportBootstrapAsync(forged, nodeId, CancellationToken.None));

            Assert.NotNull(bootstrap.UserNames);
            var forgedUsers = bootstrap with { UserNames = bootstrap.UserNames with { Users = [new(Guid.NewGuid(), "forged")] } };
            await Assert.ThrowsAsync<ArgumentException>(() => joiningAdmissions.ImportBootstrapAsync(forgedUsers, nodeId, CancellationToken.None));
            await joiningAdmissions.ImportBootstrapAsync(bootstrap, nodeId, CancellationToken.None);
            await new ClusterIdentityRepository(joining.Db).AdoptAsync(clusterId, "Media", CancellationToken.None);

            Assert.Equal(sharedKey, joiningDataKey.GetRequired(clusterId));
            Assert.Equal(sourceIdentity.PublicKey,
                await joiningAdmissions.GetActivePublicKeyAsync(clusterId, sourceIdentity.PeerId, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void KeyDirectoryDefaultsToNodeDataDirectoryWhenPluginKeyDirectoryIsOmitted()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "encoding-node-data-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Node:DataDirectory"] = dataDirectory })
                .Build();
            var resolved = ClusterPeerIdentityStore.ResolveKeyDirectory(configuration);
            Assert.Equal(Path.Combine(Path.GetFullPath(dataDirectory), "plugin-keys"), resolved);

            var protection = DataProtectionProvider.Create(new DirectoryInfo(resolved));
            Guid firstPeerId;
            byte[] firstClusterKey;
            var clusterId = Guid.NewGuid();
            using (var identity = new ClusterPeerIdentityStore(configuration, protection))
            using (var keyStore = new ClusterDataKeyStore(configuration, protection))
            {
                firstPeerId = identity.PeerId;
                firstClusterKey = keyStore.GetOrCreate(clusterId);
            }

            // Simulates recreating the container while keeping only Node:DataDirectory mounted.
            var protectionAfterRestart = DataProtectionProvider.Create(new DirectoryInfo(resolved));
            using var restartedIdentity = new ClusterPeerIdentityStore(configuration, protectionAfterRestart);
            using var restartedKeyStore = new ClusterDataKeyStore(configuration, protectionAfterRestart);
            Assert.Equal(firstPeerId, restartedIdentity.PeerId);
            Assert.Equal(firstClusterKey, restartedKeyStore.GetRequired(clusterId));
        }
        finally
        {
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SingleFounderCanReanchorRootAdmissionAndApproveJoiningNodeAfterEphemeralKeyLoss()
    {
        var root = Path.Combine(Path.GetTempPath(), "encoding-peer-reanchor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var founderDb = await TestDatabase.CreateSqliteAsync("peer-reanchor-founder");
            await using var joiningDb = await TestDatabase.CreateSqliteAsync("peer-reanchor-joining");
            var migrations = Harness.RepositoryRoot();
            await DatabaseMigrator.ApplyAsync(founderDb.Db, migrations, CancellationToken.None);
            await DatabaseMigrator.ApplyAsync(joiningDb.Db, migrations, CancellationToken.None);

            var founderIdentityRepo = new ClusterIdentityRepository(founderDb.Db);
            var initialIdentity = await founderIdentityRepo.GetAsync(CancellationToken.None);
            var clusterId = initialIdentity.Id;

            // 1. First container run: creates cluster "MAISON" and writes root admission with ephemeral keys.
            using (var ephemeralIdentity = CreateIdentity(Path.Combine(root, "ephemeral-keys-1")))
            using (var ephemeralKeyStore = new ClusterDataKeyStore(Configuration(Path.Combine(root, "ephemeral-keys-1")), new EphemeralDataProtectionProvider()))
            {
                var firstAdmissions = new ClusterPeerAdmissionRepository(founderDb.Db, founderIdentityRepo, ephemeralIdentity, ephemeralKeyStore);
                await firstAdmissions.EnsureRootAdmissionAsync(clusterId, CancellationToken.None);
                Assert.True(await founderIdentityRepo.CreateAsync("MAISON", CancellationToken.None));
            }

            // Verify cluster is now established (Standalone == false), as on .51.
            var afterCreate = await founderIdentityRepo.GetAsync(CancellationToken.None);
            Assert.False(afterCreate.Standalone);
            Assert.Equal("MAISON", afterCreate.Name);

            // 2. Container recreated with fresh key directory (simulating lost /app/api/data/plugin-keys on .51).
            var persistentKeyDir = Path.Combine(root, "persistent-keys");
            var protection = new EphemeralDataProtectionProvider();
            using var newFounderIdentity = new ClusterPeerIdentityStore(Configuration(persistentKeyDir), protection);
            using var newFounderKeyStore = new ClusterDataKeyStore(Configuration(persistentKeyDir), protection);
            var founderAdmissions = new ClusterPeerAdmissionRepository(founderDb.Db, founderIdentityRepo, newFounderIdentity, newFounderKeyStore);
            var nodeRepo = new Encoding.Api.Nodes.NodeRepository(founderDb.Db, founderAdmissions);

            // 3. Node .50 sends a pairing request and administrator approves it on .51.
            using var joiningIdentity = CreateIdentity(Path.Combine(root, "joining-keys"));
            using var joiningKeyStore = new ClusterDataKeyStore(Configuration(Path.Combine(root, "joining-keys")), new EphemeralDataProtectionProvider());
            var joiningNodeId = Guid.NewGuid();
            var joiningRequest = new Encoding.Contracts.Nodes.CreateNodeEnrollmentRequest(
                new Encoding.Contracts.Nodes.NodeEnrollmentRequest(
                    joiningNodeId,
                    "node-50",
                    [Encoding.Contracts.Nodes.NodeRole.Server, Encoding.Contracts.Nodes.NodeRole.Worker],
                    2,
                    [],
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
                    ClusterPeerId: joiningIdentity.PeerId,
                    ClusterPeerPublicKey: joiningIdentity.PublicKey,
                    ClusterPeerReconciliationUrl: "http://joining-node.test:8090/"),
                "0123456789ABCDEF");

            Assert.Equal(Encoding.Api.Nodes.PairingRequestResult.Created,
                await nodeRepo.CreatePairingRequestAsync(joiningRequest, 10, CancellationToken.None));

            Assert.DoesNotContain("http://joining-node.test:8090/", await new ClusterReconciliationRepository(founderDb.Db).GetPeerEndpointsAsync(default));
            // Approval must succeed without throwing "La clé de données du cluster n’a pas encore été installée sur ce serveur".
            var approved = await nodeRepo.ApprovePairingRequestAsync(joiningNodeId, CancellationToken.None);
            Assert.NotNull(approved);
            Assert.Contains("http://joining-node.test:8090/", await new ClusterReconciliationRepository(founderDb.Db).GetPeerEndpointsAsync(default));
            var forgedRequest = joiningRequest with { Node = joiningRequest.Node with { NodeToken = "unrelated-synthetic-token", ClusterPeerReconciliationUrl = "http://unapproved.test/" } };
            Assert.Equal(Encoding.Api.Nodes.PairingRequestResult.Conflict, await nodeRepo.CreatePairingRequestAsync(forgedRequest,10,default));
            Assert.DoesNotContain("http://unapproved.test/", await new ClusterReconciliationRepository(founderDb.Db).GetPeerEndpointsAsync(default));
            Assert.True(newFounderKeyStore.HasKey(clusterId));

            // 4. Node .50 fetches bootstrap and imports it into its local server.
            var bootstrap = await founderAdmissions.GetBootstrapForNodeAsync(joiningNodeId, CancellationToken.None);
            Assert.NotNull(bootstrap);
            var joiningAdmissions = new ClusterPeerAdmissionRepository(joiningDb.Db, new ClusterIdentityRepository(joiningDb.Db), joiningIdentity, joiningKeyStore);
            await joiningAdmissions.ImportBootstrapAsync(bootstrap, joiningNodeId, CancellationToken.None);
            await new ClusterIdentityRepository(joiningDb.Db).AdoptAsync(clusterId, bootstrap.ClusterName, CancellationToken.None);

            Assert.Equal(newFounderKeyStore.GetRequired(clusterId), joiningKeyStore.GetRequired(clusterId));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ClusterPeerIdentityStore CreateIdentity(string keyDirectory) =>
        new(Configuration(keyDirectory), new EphemeralDataProtectionProvider());

    private static IConfiguration Configuration(string keyDirectory) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["PluginSettings:KeyDirectory"] = keyDirectory })
        .Build();
}
