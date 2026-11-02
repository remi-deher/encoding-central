using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Encoding.Api.Auth;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure.Data.Sqlite;
using Encoding.Contracts.Cluster;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class ClusterReconciliationApiTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SignedExchangeAppliesAndReturnsEventsIdempotently()
    {
        var root = Path.Combine(Path.GetTempPath(), "encoding-mesh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "source.db");
        var targetPath = Path.Combine(root, "target.db");
        var sourceConnectionString = $"Data Source={sourcePath};Pooling=False";
        var targetConnectionString = $"Data Source={targetPath};Pooling=False";
        try
        {
            await using var source = new SqliteDataSource(sourceConnectionString);
            await using var target = new SqliteDataSource(targetConnectionString);
            var migrations = Harness.RepositoryRoot();
            await DatabaseMigrator.ApplyAsync(source, migrations, CancellationToken.None);
            await DatabaseMigrator.ApplyAsync(target, migrations, CancellationToken.None);
            var clusterId = Guid.NewGuid();
            await new ClusterIdentityRepository(source).AdoptAsync(clusterId, "Media", CancellationToken.None);
            await new ClusterIdentityRepository(target).AdoptAsync(clusterId, "Media", CancellationToken.None);
            var sourceProtection = new EphemeralDataProtectionProvider();
            var sourceConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PluginSettings:KeyDirectory"] = Path.Combine(root, "source-keys")
            }).Build();
            using var sourceIdentity = new ClusterPeerIdentityStore(sourceConfiguration, sourceProtection);
            await TrustPeerAsync(target, clusterId, sourceIdentity);
            var sourceJournal = new ClusterReconciliationRepository(source);
            await sourceJournal.SetRegisterAsync("cluster.name", JsonSerializer.Serialize("Maison"), CancellationToken.None);
            var changes = await sourceJournal.GetEventsAfterAsync(clusterId, new Dictionary<Guid, long>(), ClusterReconciliationRepository.MaximumBatchSize, CancellationToken.None);
            var request = new ClusterReconciliationExchangeRequest(1, clusterId, new Dictionary<Guid, long>(), changes);
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);

            await using var factory = CreateFactory(targetConnectionString, root);
            using var client = factory.CreateClient();
            using var response = await SendSignedAsync(client, requestBytes, sourceIdentity);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var responseBytes = await response.Content.ReadAsByteArrayAsync();
            var responsePeerId = Guid.Parse(response.Headers.GetValues(ClusterReconciliationSignature.PeerHeader).Single());
            var targetIdentity = factory.Services.GetRequiredService<ClusterPeerIdentityStore>();
            Assert.Equal(targetIdentity.PeerId, responsePeerId);
            Assert.True(ClusterReconciliationSignature.Verify(
                targetIdentity.PublicKey,
                responsePeerId,
                response.Headers.GetValues(ClusterReconciliationSignature.TimeHeader).Single(),
                response.Headers.GetValues(ClusterReconciliationSignature.SignatureHeader).Single(),
                responseBytes));
            Assert.Equal("Maison", await ClusterNameAsync(target));
            Assert.Equal(1L, await CountEventsAsync(target));

            using var duplicate = await SendSignedAsync(client, requestBytes, sourceIdentity);
            Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
            Assert.Equal(1L, await CountEventsAsync(target));

            var rogueConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PluginSettings:KeyDirectory"] = Path.Combine(root, "rogue-keys")
            }).Build();
            using var rogueIdentity = new ClusterPeerIdentityStore(rogueConfiguration, sourceProtection);
            using var forged = await SendSignedAsync(client, requestBytes, rogueIdentity);
            Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SignedExchangeFromAnIncompatibleProtocolOrAnotherClusterIsRefusedWithoutApplyingChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "encoding-mesh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourceConnectionString = $"Data Source={Path.Combine(root, "source.db")};Pooling=False";
        var targetConnectionString = $"Data Source={Path.Combine(root, "target.db")};Pooling=False";
        try
        {
            await using var source = new SqliteDataSource(sourceConnectionString);
            await using var target = new SqliteDataSource(targetConnectionString);
            var migrations = Harness.RepositoryRoot();
            await DatabaseMigrator.ApplyAsync(source, migrations, CancellationToken.None);
            await DatabaseMigrator.ApplyAsync(target, migrations, CancellationToken.None);
            var clusterId = Guid.NewGuid();
            await new ClusterIdentityRepository(source).AdoptAsync(clusterId, "Media", CancellationToken.None);
            await new ClusterIdentityRepository(target).AdoptAsync(clusterId, "Media", CancellationToken.None);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PluginSettings:KeyDirectory"] = Path.Combine(root, "source-keys")
            }).Build();
            using var sourceIdentity = new ClusterPeerIdentityStore(configuration, new EphemeralDataProtectionProvider());
            await TrustPeerAsync(target, clusterId, sourceIdentity);
            var journal = new ClusterReconciliationRepository(source);
            await journal.SetRegisterAsync("cluster.name", JsonSerializer.Serialize("Maison"), CancellationToken.None);
            var changes = await journal.GetEventsAfterAsync(clusterId, new Dictionary<Guid, long>(), ClusterReconciliationRepository.MaximumBatchSize, CancellationToken.None);
            var eventsBefore = await CountEventsAsync(target);

            await using var factory = CreateFactory(targetConnectionString, root);
            using var client = factory.CreateClient();
            foreach (var version in new[] { ClusterReconciliationEndpoints.ProtocolVersion + 1, ClusterReconciliationEndpoints.ProtocolVersion - 1 })
            {
                var request = new ClusterReconciliationExchangeRequest(version, clusterId, new Dictionary<Guid, long>(), changes);
                using var response = await SendSignedAsync(client, JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions), sourceIdentity);
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            }
            var otherCluster = new ClusterReconciliationExchangeRequest(ClusterReconciliationEndpoints.ProtocolVersion, Guid.NewGuid(), new Dictionary<Guid, long>(), changes);
            using (var response = await SendSignedAsync(client, JsonSerializer.SerializeToUtf8Bytes(otherCluster, JsonOptions), sourceIdentity))
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

            Assert.Equal(eventsBefore, await CountEventsAsync(target));
            Assert.Equal("Media", await ClusterNameAsync(target));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task ExchangeRegistersAdvertisedPeerAndExchangesKnownPeers()
    {
        var root = Path.Combine(Path.GetTempPath(), "encoding-mesh-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var targetPath = Path.Combine(root, "target.db");
        var targetConnectionString = $"Data Source={targetPath};Pooling=False";
        try
        {
            await using var target = new SqliteDataSource(targetConnectionString);
            var migrations = Harness.RepositoryRoot();
            await DatabaseMigrator.ApplyAsync(target, migrations, CancellationToken.None);
            var clusterId = Guid.NewGuid();
            await new ClusterIdentityRepository(target).AdoptAsync(clusterId, "Media", CancellationToken.None);
            var sourceProtection = new EphemeralDataProtectionProvider();
            var sourceConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PluginSettings:KeyDirectory"] = Path.Combine(root, "source-keys")
            }).Build();
            using var sourceIdentity = new ClusterPeerIdentityStore(sourceConfiguration, sourceProtection);
            await TrustPeerAsync(target, clusterId, sourceIdentity);

            var request = new ClusterReconciliationExchangeRequest(
                1,
                clusterId,
                new Dictionary<Guid, long>(),
                [],
                AdvertisedUrl: "http://peer-source:5080/",
                KnownPeers: ["http://peer-gossip-1:5080/"]);
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);

            await using var factory = CreateFactory(targetConnectionString, root);
            using var client = factory.CreateClient();
            using var response = await SendSignedAsync(client, requestBytes, sourceIdentity);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var repo = new ClusterReconciliationRepository(target);
            var endpoints = await repo.GetPeerEndpointsAsync(CancellationToken.None);
            Assert.Contains("http://peer-source:5080/", endpoints);
            Assert.Contains("http://peer-gossip-1:5080/", endpoints);

            var responseBytes = await response.Content.ReadAsByteArrayAsync();
            var exchange = JsonSerializer.Deserialize<ClusterReconciliationExchangeResponse>(responseBytes, JsonOptions);
            Assert.NotNull(exchange);
            Assert.Contains("http://peer-source:5080/", exchange.KnownPeers ?? []);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TwoServersJoinViaPairingKeyOrFirstLinkUsingSingleNodeDataDirectoryAndReconcileOverHttp()
    {
        var root = Path.Combine(Path.GetTempPath(), "encoding-two-node-join-" + Guid.NewGuid().ToString("N"));
        var dir51 = Path.Combine(root, "node-51");
        var dir50 = Path.Combine(root, "node-50");
        Directory.CreateDirectory(dir51);
        Directory.CreateDirectory(dir50);
        var secret51 = Path.Combine(dir51, Encoding.Core.Nodes.LocalEnrollment.FileName);
        var secret50 = Path.Combine(dir50, Encoding.Core.Nodes.LocalEnrollment.FileName);
        Encoding.Core.Nodes.LocalEnrollment.Create(secret51);
        Encoding.Core.Nodes.LocalEnrollment.Create(secret50);

        try
        {
            // Only configure Node:DataDirectory (no PluginSettings:KeyDirectory), matching single-volume Docker deployments.
            await using var server51 = CreateSingleVolumeFactory(dir51, secret51);
            await using var server50 = CreateSingleVolumeFactory(dir50, secret50);
            using var client51 = server51.CreateClient();
            using var client50 = server50.CreateClient();
            client51.DefaultRequestHeaders.Add(ManagementAccessHandler.CsrfHeader, "1");
            client50.DefaultRequestHeaders.Add(ManagementAccessHandler.CsrfHeader, "1");

            // 1. Server .51 creates cluster "MAISON".
            using var createResp = await client51.PostAsync(
                "/api/v1/cluster",
                new StringContent("{\"name\":\"MAISON\"}", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
            var clusterJson = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync()).RootElement;
            var clusterId = clusterJson.GetProperty("id").GetGuid();
            Assert.Equal("MAISON", clusterJson.GetProperty("name").GetString());
            Assert.False(clusterJson.GetProperty("standalone").GetBoolean());

            // 2. Simulate recreating container .51 on the same Node:DataDirectory volume: keys must still be present in dir51/plugin-keys.
            Assert.True(Directory.Exists(Path.Combine(dir51, "plugin-keys", "cluster-reconciliation")));
            Assert.True(File.Exists(Path.Combine(dir51, "plugin-keys", "cluster-reconciliation", "server-identity.json")));
            Assert.True(File.Exists(Path.Combine(dir51, "plugin-keys", "cluster-reconciliation", "cluster-data-key.json")));

            // 3. Server .50 reads its local peer identity (just as WorkerEnrollmentService does before requesting pairing).
            using var peerIdReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cluster/reconciliation/local-identity");
            peerIdReq.Headers.Add(Encoding.Core.Nodes.LocalEnrollment.HeaderName, Encoding.Core.Nodes.LocalEnrollment.Read(secret50));
            using var peerIdResp = await client50.SendAsync(peerIdReq);
            Assert.Equal(HttpStatusCode.OK, peerIdResp.StatusCode);
            var peer50 = JsonSerializer.Deserialize<ClusterPeerPublicIdentity>(await peerIdResp.Content.ReadAsStringAsync(), JsonOptions)!;

            // 4. Node .50 submits a pairing request to .51 with its server PeerId & PublicKey.
            var node50Id = Guid.NewGuid();
            var node50Token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
            var pairingKey = "A1B2C3D4E5F60718";
            var enrollmentReq = new Encoding.Contracts.Nodes.CreateNodeEnrollmentRequest(
                new Encoding.Contracts.Nodes.NodeEnrollmentRequest(
                    node50Id,
                    "node-50",
                    [Encoding.Contracts.Nodes.NodeRole.Server, Encoding.Contracts.Nodes.NodeRole.Worker],
                    2,
                    [],
                    node50Token,
                    ClusterPeerId: peer50.PeerId,
                    ClusterPeerPublicKey: peer50.PublicKey,
                    DisplayName: "Serveur 50"),
                pairingKey);
            using var pairResp = await client51.PostAsync(
                "/api/v1/nodes/enrollment-requests",
                new StringContent(JsonSerializer.Serialize(enrollmentReq, JsonOptions), System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Accepted, pairResp.StatusCode);

            // 5. Administrator on .51 approves .50's pending request.
            using var approveResp = await client51.PostAsync($"/api/v1/nodes/enrollment-requests/{node50Id:D}/approve", null);
            Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);

            // 6. Node .50 fetches its signed ClusterJoinBootstrap from .51 using its X-Node-Token.
            using var bootReq = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/nodes/{node50Id:D}/cluster-bootstrap");
            bootReq.Headers.Add("X-Node-Token", node50Token);
            using var bootResp = await client51.SendAsync(bootReq);
            Assert.Equal(HttpStatusCode.OK, bootResp.StatusCode);
            var bootstrap = JsonSerializer.Deserialize<ClusterJoinBootstrap>(await bootResp.Content.ReadAsStringAsync(), JsonOptions)!;
            Assert.Equal(clusterId, bootstrap.ClusterId);
            Assert.Equal("MAISON", bootstrap.ClusterName);

            // 7. Local agent on .50 instructs its local server (/api/v1/cluster/join) to adopt the cluster identity and key.
            using var joinReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cluster/join")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { clusterId = bootstrap.ClusterId, clusterName = bootstrap.ClusterName, nodeId = node50Id, bootstrap, peerUrl = "http://192.168.1.51:8090/" }, JsonOptions),
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
            joinReq.Headers.Add(Encoding.Core.Nodes.LocalEnrollment.HeaderName, Encoding.Core.Nodes.LocalEnrollment.Read(secret50));
            using var joinResp = await client50.SendAsync(joinReq);
            Assert.Equal(HttpStatusCode.OK, joinResp.StatusCode);

            // 8. Verify .50 now reports cluster "MAISON" with the same clusterId and can exchange signed reconciliation events with .51!
            var joinedClusterJson = JsonDocument.Parse(await client50.GetStringAsync("/api/v1/cluster")).RootElement;
            Assert.Equal(clusterId, joinedClusterJson.GetProperty("id").GetGuid());
            Assert.Equal("MAISON", joinedClusterJson.GetProperty("name").GetString());

            var identity50 = server50.Services.GetRequiredService<ClusterPeerIdentityStore>();
            var exchangeReq = new ClusterReconciliationExchangeRequest(1, clusterId, new Dictionary<Guid, long>(), []);
            using var exchResp = await SendSignedAsync(client51, JsonSerializer.SerializeToUtf8Bytes(exchangeReq, JsonOptions), identity50);
            Assert.Equal(HttpStatusCode.OK, exchResp.StatusCode);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static WebApplicationFactory<AuthOptions> CreateSingleVolumeFactory(string dataDirectory, string secretFile) =>
        new WebApplicationFactory<AuthOptions>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(WebHostDefaults.ContentRootKey, AppContext.BaseDirectory);
            builder.UseSetting("Logging:LogLevel:Default", "Error");
            builder.UseSetting("ConnectionStrings:Cluster", $"Data Source={Path.Combine(dataDirectory, "cluster.db")};Pooling=False");
            builder.UseSetting("Database:Provider", "sqlite");
            builder.UseSetting("Node:DataDirectory", dataDirectory);
            builder.UseSetting("Auth:Enabled", "false");
            builder.UseSetting(Encoding.Core.Nodes.LocalEnrollment.SecretFileSetting, secretFile);
            builder.ConfigureServices(services =>
            {
                foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService)).ToArray()) services.Remove(descriptor);
            });
        });

    private static WebApplicationFactory<AuthOptions> CreateFactory(string connectionString, string dataDirectory) =>
        new WebApplicationFactory<AuthOptions>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(WebHostDefaults.ContentRootKey, AppContext.BaseDirectory);
            builder.UseSetting("ConnectionStrings:Cluster", connectionString);
            builder.UseSetting("Database:Provider", "sqlite");
            builder.UseSetting("FlowBackups:Directory", Path.Combine(dataDirectory, "backups"));
            builder.UseSetting("Runtime:ArtifactDirectory", Path.Combine(dataDirectory, "artifacts"));
            builder.UseSetting("PluginSettings:KeyDirectory", Path.Combine(dataDirectory, "keys"));
            builder.ConfigureServices(services =>
            {
                foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService)).ToArray()) services.Remove(descriptor);
            });
        });

    private static async Task TrustPeerAsync(DbDataSource database, Guid clusterId, ClusterPeerIdentityStore peer)
    {
        await using var command = database.CreateCommand("""
            INSERT INTO cluster_peer_admissions (cluster_id, peer_id, public_key, approved_by_peer_id, wrapped_data_key, key_fingerprint, issued_at, signature)
            VALUES (@cluster_id, @peer_id, @public_key, @approved_by_peer_id, @wrapped_data_key, @key_fingerprint, @issued_at, @signature)
            """);
        command.Parameters.AddWithValue("cluster_id", clusterId);
        command.Parameters.AddWithValue("peer_id", peer.PeerId);
        command.Parameters.AddWithValue("public_key", peer.PublicKey);
        command.Parameters.AddWithValue("approved_by_peer_id", peer.PeerId);
        command.Parameters.AddWithValue("wrapped_data_key", "test-envelope");
        command.Parameters.AddWithValue("key_fingerprint", new string('0', 64));
        command.Parameters.AddWithValue("issued_at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("signature", "test-signature");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<HttpResponseMessage> SendSignedAsync(HttpClient client, byte[] body, ClusterPeerIdentityStore identity)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cluster/reconciliation/exchange")
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add(ClusterReconciliationSignature.PeerHeader, identity.PeerId.ToString("D"));
        request.Headers.Add(ClusterReconciliationSignature.TimeHeader, timestamp);
        request.Headers.Add(ClusterReconciliationSignature.SignatureHeader, ClusterReconciliationSignature.Create(identity, timestamp, body));
        return await client.SendAsync(request);
    }

    private static async Task<string> ClusterNameAsync(DbDataSource database)
    {
        await using var command = database.CreateCommand("SELECT name FROM cluster_identity WHERE singleton = true");
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountEventsAsync(DbDataSource database)
    {
        await using var command = database.CreateCommand("SELECT count(*) FROM cluster_change_log");
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
