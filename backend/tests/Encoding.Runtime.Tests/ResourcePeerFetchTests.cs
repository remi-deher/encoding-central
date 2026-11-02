using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using Encoding.Api.Auth;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure.Data.Sqlite;
using Encoding.Api.Resources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class ResourcePeerFetchTests
{
    private const string PeerUrl = "http://peer.test/";
    private static readonly byte[] Font = [0x00, 0x01, 0x00, 0x00, .. Enumerable.Range(0, 60000).Select(index => (byte)(index * 31))];

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Pair : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "encoding-resource-peer-" + Guid.NewGuid().ToString("N"));
        private WebApplicationFactory<AuthOptions> factory = null!;
        private HttpClient peerClient = null!;
        private SqliteDataSource serverDatabase = null!;
        private SqliteDataSource clientDatabase = null!;
        public ResourceBlobStore ServerBlobs { get; private set; } = null!;
        public ResourceRepository ServerResources { get; private set; } = null!;
        public DbDataSource ServerDatabase => serverDatabase;
        public ResourceBlobStore ClientBlobs { get; private set; } = null!;
        public ResourceBlobFetcher Fetcher { get; private set; } = null!;
        public ClusterPeerIdentityStore Identity { get; private set; } = null!;
        public HttpClient Client => peerClient;

        public static async Task<Pair> CreateAsync(bool admitted = true)
        {
            var pair = new Pair();
            Directory.CreateDirectory(pair.root);
            var serverConnection = $"Data Source={Path.Combine(pair.root, "server.db")};Pooling=False";
            pair.serverDatabase = new SqliteDataSource(serverConnection);
            pair.clientDatabase = new SqliteDataSource($"Data Source={Path.Combine(pair.root, "client.db")};Pooling=False");
            var migrations = Harness.RepositoryRoot();
            await DatabaseMigrator.ApplyAsync(pair.serverDatabase, migrations, CancellationToken.None);
            await DatabaseMigrator.ApplyAsync(pair.clientDatabase, migrations, CancellationToken.None);
            var clusterId = Guid.NewGuid();
            await new ClusterIdentityRepository(pair.serverDatabase).AdoptAsync(clusterId, "Media", CancellationToken.None);
            await new ClusterIdentityRepository(pair.clientDatabase).AdoptAsync(clusterId, "Media", CancellationToken.None);

            var clientConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PluginSettings:KeyDirectory"] = Path.Combine(pair.root, "client-keys"),
                ["Resources:Directory"] = Path.Combine(pair.root, "client-blobs")
            }).Build();
            pair.Identity = new ClusterPeerIdentityStore(clientConfiguration, new EphemeralDataProtectionProvider());
            if (admitted) await TrustPeerAsync(pair.serverDatabase, clusterId, pair.Identity);

            pair.factory = new WebApplicationFactory<AuthOptions>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting(WebHostDefaults.ContentRootKey, AppContext.BaseDirectory);
                builder.UseSetting("ConnectionStrings:Cluster", serverConnection);
                builder.UseSetting("Database:Provider", "sqlite");
                builder.UseSetting("FlowBackups:Directory", Path.Combine(pair.root, "backups"));
                builder.UseSetting("Runtime:ArtifactDirectory", Path.Combine(pair.root, "artifacts"));
                builder.UseSetting("PluginSettings:KeyDirectory", Path.Combine(pair.root, "server-keys"));
                builder.UseSetting("Resources:Directory", Path.Combine(pair.root, "server-blobs"));
                builder.ConfigureServices(services =>
                {
                    foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService)).ToArray()) services.Remove(descriptor);
                });
            });
            pair.peerClient = new HttpClient(pair.factory.Server.CreateHandler());
            pair.ServerBlobs = pair.factory.Services.GetRequiredService<ResourceBlobStore>();
            pair.ServerResources = pair.factory.Services.GetRequiredService<ResourceRepository>();
            pair.ClientBlobs = new ResourceBlobStore(clientConfiguration);
            var journal = new ClusterReconciliationRepository(pair.clientDatabase);
            await journal.AddPeerEndpointAsync(PeerUrl, "test", CancellationToken.None);
            pair.Fetcher = new ResourceBlobFetcher(pair.ClientBlobs, pair.Identity, journal,
                clientConfiguration, new FixedHttpClientFactory(pair.peerClient), NullLogger<ResourceBlobFetcher>.Instance);
            return pair;
        }

        public async ValueTask DisposeAsync()
        {
            peerClient.Dispose();
            await factory.DisposeAsync();
            Identity.Dispose();
            await serverDatabase.DisposeAsync();
            await clientDatabase.DisposeAsync();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }

        public HttpRequestMessage SignedGet(string digest, string? signedDigest = null)
        {
            var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var request = new HttpRequestMessage(HttpMethod.Get, PeerUrl + ResourceBlobFetcher.RoutePrefix.TrimStart('/') + digest);
            request.Headers.Add(ClusterReconciliationSignature.PeerHeader, Identity.PeerId.ToString("D"));
            request.Headers.Add(ClusterReconciliationSignature.TimeHeader, time);
            request.Headers.Add(ClusterReconciliationSignature.SignatureHeader,
                ClusterReconciliationSignature.Create(Identity, time, ResourceBlobFetcher.SignedRequest(signedDigest ?? digest)));
            return request;
        }
    }

    private static async Task<ResourceFile> StoreAsync(ResourceBlobStore store, byte[] bytes) =>
        await store.PutAsync(new MemoryStream(bytes), "Arial.ttf", "font", CancellationToken.None);

    [Fact]
    public async Task APeerServesTheFileAndTwoSimultaneousRequestsShareOneTransfer()
    {
        await using var pair = await Pair.CreateAsync();
        var file = await StoreAsync(pair.ServerBlobs, Font);

        var results = await Task.WhenAll(
            pair.Fetcher.EnsureAsync(file.Sha256, file.Bytes, CancellationToken.None),
            pair.Fetcher.EnsureAsync(file.Sha256, file.Bytes, CancellationToken.None));

        Assert.All(results, Assert.True);
        Assert.Equal(file.Sha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(pair.ClientBlobs.PathFor(file.Sha256)))));
    }

    [Fact]
    public async Task AnInterruptedTransferResumesAtTheReachedByte()
    {
        await using var pair = await Pair.CreateAsync();
        var file = await StoreAsync(pair.ServerBlobs, Font);
        var partial = pair.ClientBlobs.PartialPathFor(file.Sha256);
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        await File.WriteAllBytesAsync(partial, Font[..20000]);

        Assert.True(await pair.Fetcher.EnsureAsync(file.Sha256, file.Bytes, CancellationToken.None));
        Assert.Equal(Font, await File.ReadAllBytesAsync(pair.ClientBlobs.PathFor(file.Sha256)));
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public async Task AForgedOrTruncatedFileNeverReachesTheLocalStore()
    {
        await using var pair = await Pair.CreateAsync();
        var file = await StoreAsync(pair.ServerBlobs, Font);
        // Le pair sert d'autres octets sous la même empreinte.
        await File.WriteAllBytesAsync(pair.ServerBlobs.PathFor(file.Sha256), [.. Font.Take(Font.Length - 1), (byte)(Font[^1] ^ 0xFF)]);

        Assert.False(await pair.Fetcher.EnsureAsync(file.Sha256, file.Bytes, CancellationToken.None));
        Assert.False(pair.ClientBlobs.Exists(file.Sha256));
        Assert.False(File.Exists(pair.ClientBlobs.PartialPathFor(file.Sha256)));

        // Taille annoncée plus grande que celle du pair : rien n'est publié, la reprise reste possible.
        await File.WriteAllBytesAsync(pair.ServerBlobs.PathFor(file.Sha256), Font);
        Assert.False(await pair.Fetcher.EnsureAsync(file.Sha256, file.Bytes + 10, CancellationToken.None));
        Assert.False(pair.ClientBlobs.Exists(file.Sha256));
    }

    [Fact]
    public async Task OnlyAdmittedPeersWithAValidSignatureForThatFileAreServed()
    {
        await using var pair = await Pair.CreateAsync();
        var file = await StoreAsync(pair.ServerBlobs, Font);
        var other = await StoreAsync(pair.ServerBlobs, [.. Font, 1]);

        using (var ok = await pair.Client.SendAsync(pair.SignedGet(file.Sha256)))
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using (var wrongFile = await pair.Client.SendAsync(pair.SignedGet(other.Sha256, file.Sha256)))
            Assert.Equal(HttpStatusCode.Unauthorized, wrongFile.StatusCode);
        using (var anonymous = await pair.Client.GetAsync(PeerUrl + ResourceBlobFetcher.RoutePrefix.TrimStart('/') + file.Sha256))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var range = pair.SignedGet(file.Sha256))
        {
            range.Headers.Range = new RangeHeaderValue(100, 199);
            using var partial = await pair.Client.SendAsync(range);
            Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
            Assert.Equal(100, (await partial.Content.ReadAsByteArrayAsync()).Length);
        }

        await using var stranger = await Pair.CreateAsync(admitted: false);
        var hidden = await StoreAsync(stranger.ServerBlobs, Font);
        Assert.False(await stranger.Fetcher.EnsureAsync(hidden.Sha256, hidden.Bytes, CancellationToken.None));
        using var refused = await stranger.Client.SendAsync(stranger.SignedGet(hidden.Sha256));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task AWorkerReadsAVersionAndDownloadsItsFilesWithItsNodeToken()
    {
        await using var pair = await Pair.CreateAsync();
        var file = await StoreAsync(pair.ServerBlobs, Font);
        var resourceId = Guid.NewGuid();
        await pair.ServerResources.CreateAsync(resourceId, "font", "Crunchyroll", "", [new(file.Name, file.Sha256)], CancellationToken.None);
        var nodeId = Guid.NewGuid();
        const string token = "worker-token";
        await using (var insert = pair.ServerDatabase.CreateCommand("INSERT INTO cluster_nodes(id,name,roles,max_concurrent_tasks,capabilities,last_seen_at,node_token_hash) VALUES(@id,'worker',@roles,1,'[]',@seen,@hash)"))
        {
            insert.Parameters.AddWithValue("id", nodeId);
            insert.Parameters.AddWithValue("roles", "[\"worker\"]");
            insert.Parameters.AddWithValue("seen", DateTimeOffset.UtcNow);
            insert.Parameters.AddWithValue("hash", SqlKind.Bytes, SHA256.HashData("worker-token"u8));
            await insert.ExecuteNonQueryAsync();
        }

        async Task<HttpResponseMessage> GetAsync(string path, string? nodeToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{PeerUrl}api/v1/runtime/nodes/{nodeId:D}/{path}");
            if (nodeToken is not null) request.Headers.Add("X-Node-Token", nodeToken);
            return await pair.Client.SendAsync(request);
        }

        using (var version = await GetAsync($"resources/{resourceId:D}/versions/1", token))
        {
            Assert.Equal(HttpStatusCode.OK, version.StatusCode);
            var info = await version.Content.ReadFromJsonAsync<Encoding.Contracts.Flows.ResourceVersionInfo>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal(file.Sha256, Assert.Single(info!.Files).Sha256);
        }
        using (var blob = await GetAsync($"resources/blobs/{file.Sha256}", token))
            Assert.Equal(Font, await blob.Content.ReadAsByteArrayAsync());
        using (var unknown = await GetAsync($"resources/{resourceId:D}/versions/9", token))
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using (var absent = await GetAsync($"resources/blobs/{new string('b', 64)}", token))
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using (var wrongToken = await GetAsync($"resources/blobs/{file.Sha256}", "other"))
            Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);
        using (var noToken = await GetAsync($"resources/blobs/{file.Sha256}", null))
            Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
    }

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
}
