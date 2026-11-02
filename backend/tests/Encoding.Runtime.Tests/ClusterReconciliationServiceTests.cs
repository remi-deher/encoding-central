using System.Net;
using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Operations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class ClusterReconciliationServiceTests
{
    [Theory]
    [InlineData("server", true)]
    [InlineData("server,worker", true)]
    [InlineData("worker", false)]
    [InlineData("worker, worker", false)]
    [InlineData(null, false)]
    public void DiscoveryAcceptsOnlyExplicitServerAdvertisements(string? roles, bool expected)
    {
        var properties = roles is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["roles"] = roles };

        Assert.Equal(expected, ClusterReconciliationService.IsAdvertisedServer(properties));
    }

    [Fact]
    public async Task StartupCleanupRemovesPersistedMdnsEndpointsAndPreservesOtherSources()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Journal.AddPeerEndpointAsync("http://old-worker.local:8080/", "mdns", CancellationToken.None);
        await fixture.Journal.AddPeerEndpointAsync("http://server.local:8080/", "configured", CancellationToken.None);

        await fixture.Journal.RemovePeerEndpointsBySourceAsync("mdns", CancellationToken.None);

        Assert.Equal(["http://server.local:8080/"], await fixture.Journal.GetPeerEndpointsAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(70, 3)]
    [InlineData(300, ClusterReconciliationService.MaximumExchangesPerPeer)]
    public async Task OutgoingBacklogDrainsImmediatelyWithinTheExchangeBudget(int count, int expectedExchanges)
    {
        await using var fixture = await Fixture.CreateAsync();
        for (var i = 0; i < count; i++) await fixture.Journal.SetRegisterAsync("test." + i, "1", CancellationToken.None);
        var acknowledged = new Dictionary<Guid, long>();
        var sent = new HashSet<Guid>();
        var calls = 0;
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            calls++;
            var exchange = JsonSerializer.Deserialize<ClusterReconciliationExchangeRequest>(await request.Content!.ReadAsByteArrayAsync(ct), Fixture.Json)!;
            foreach (var change in exchange.Changes)
            {
                Assert.True(sent.Add(change.EventId));
                acknowledged[change.OriginId] = change.Sequence;
            }
            return fixture.Response(acknowledged);
        }));
        using var service = fixture.Service(client, new ManualClock());
        await service.ReconcilePeersAsync(fixture.ClusterId, ["http://peer.test/"], CancellationToken.None);
        Assert.Equal(expectedExchanges, calls);
        Assert.Equal(Math.Min(count, ClusterReconciliationRepository.MaximumBatchSize * expectedExchanges), sent.Count);
        // The next cycle resumes from the persisted acknowledgement, without resending a page.
        if (sent.Count < count)
        {
            await service.ReconcilePeersAsync(fixture.ClusterId, ["http://peer.test/"], CancellationToken.None);
            Assert.Equal(count, sent.Count);
        }
    }

    [Fact]
    public async Task IncomingBacklogDrainsUsingRemoteAndLocalVectors()
    {
        await using var fixture = await Fixture.CreateAsync();
        var origin = Guid.NewGuid();
        var changes = Enumerable.Range(1, 70).Select(i => new ClusterChangeEvent(Guid.NewGuid(), fixture.ClusterId,
            origin, i, "register.set", JsonSerializer.Serialize(new { key = "remote." + i, value = "1" }), DateTimeOffset.UtcNow)).ToArray();
        var calls = 0;
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            calls++;
            var exchange = JsonSerializer.Deserialize<ClusterReconciliationExchangeRequest>(await request.Content!.ReadAsByteArrayAsync(ct), Fixture.Json)!;
            var vector = new Dictionary<Guid, long>(exchange.KnownSequences) { [origin] = 70 };
            return fixture.Response(vector, changes.Where(change => change.Sequence > exchange.KnownSequences.GetValueOrDefault(origin)).Take(32).ToArray());
        }));
        using var service = fixture.Service(client, new ManualClock());
        await service.ReconcilePeersAsync(fixture.ClusterId, ["http://peer.test/"], CancellationToken.None);
        Assert.Equal(3, calls);
        Assert.Equal(70, (await fixture.Journal.GetVectorAsync(CancellationToken.None))[origin]);
    }

    [Fact]
    public async Task ByteShortenedPageStillDrainsTheRemainingEvents()
    {
        await using var fixture = await Fixture.CreateAsync();
        var value = JsonSerializer.Serialize(new string('x', 900_000));
        for (var i = 0; i < 20; i++) await fixture.Journal.SetRegisterAsync("large." + i, value, CancellationToken.None);
        var acknowledged = new Dictionary<Guid, long>();
        var pageSizes = new List<int>();
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            var body = await request.Content!.ReadAsByteArrayAsync(ct);
            Assert.True(body.Length <= ClusterReconciliationSignature.MaximumBodyBytes);
            var exchange = JsonSerializer.Deserialize<ClusterReconciliationExchangeRequest>(body, Fixture.Json)!;
            pageSizes.Add(exchange.Changes.Count);
            foreach (var change in exchange.Changes) acknowledged[change.OriginId] = change.Sequence;
            return fixture.Response(acknowledged);
        }));
        using var service = fixture.Service(client, new ManualClock());
        await service.ReconcilePeersAsync(fixture.ClusterId, ["http://peer.test/"], CancellationToken.None);
        Assert.True(pageSizes.Count > 1);
        Assert.InRange(pageSizes[0], 1, 19);
        Assert.Equal(20, pageSizes.Sum());
    }

    [Fact]
    public async Task UnchangingPeerDoesNotCauseAnExchangeLoop()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Journal.SetRegisterAsync("test", "1", CancellationToken.None);
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(fixture.Response()); }));
        using var service = fixture.Service(client, new ManualClock());
        await service.ReconcilePeersAsync(fixture.ClusterId, ["http://peer.test/"], CancellationToken.None);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ByteBudgetYieldsBeforeAnExchangeCouldExceedTheTransferLimit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var value = JsonSerializer.Serialize(new string('x', 900_000));
        for (var i = 0; i < 80; i++) await fixture.Journal.SetRegisterAsync("large." + i, value, CancellationToken.None);
        var acknowledged = new Dictionary<Guid, long>();
        long transferred = 0;
        var calls = 0;
        var sent = 0;
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            calls++;
            var body = await request.Content!.ReadAsByteArrayAsync(ct);
            var exchange = JsonSerializer.Deserialize<ClusterReconciliationExchangeRequest>(body, Fixture.Json)!;
            sent += exchange.Changes.Count;
            foreach (var change in exchange.Changes) acknowledged[change.OriginId] = change.Sequence;
            var response = fixture.Response(acknowledged);
            transferred += body.Length + (await response.Content.ReadAsByteArrayAsync(ct)).Length;
            return response;
        }));
        using var service = fixture.Service(client, new ManualClock());
        await service.ReconcilePeersAsync(fixture.ClusterId, ["http://peer.test/"], CancellationToken.None);
        Assert.InRange(calls, 1, ClusterReconciliationService.MaximumExchangesPerPeer - 1);
        Assert.InRange(sent, 1, 79);
        Assert.True(transferred <= ClusterReconciliationService.MaximumBytesPerPeer);
        Assert.True(transferred + 2L * ClusterReconciliationSignature.MaximumBodyBytes > ClusterReconciliationService.MaximumBytesPerPeer);
    }

    [Fact]
    public async Task TimeBudgetYieldsToTheNextPeer()
    {
        await using var fixture = await Fixture.CreateAsync();
        for (var i = 0; i < 70; i++) await fixture.Journal.SetRegisterAsync("test." + i, "1", CancellationToken.None);
        var clock = new ManualClock();
        var hosts = new List<string>();
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            hosts.Add(request.RequestUri!.Host);
            var exchange = JsonSerializer.Deserialize<ClusterReconciliationExchangeRequest>(await request.Content!.ReadAsByteArrayAsync(ct), Fixture.Json)!;
            var vector = exchange.Changes.GroupBy(change => change.OriginId).ToDictionary(group => group.Key, group => group.Max(change => change.Sequence));
            clock.Advance(ClusterReconciliationService.CatchUpBudget);
            return fixture.Response(vector);
        }));
        using var service = fixture.Service(client, clock);
        await service.ReconcilePeersAsync(fixture.ClusterId, ["http://first.test/", "http://second.test/"], CancellationToken.None);
        Assert.Equal(["first.test", "second.test"], hosts);
    }

    [Fact]
    public async Task TimeoutRecordsPeerFailureAndContinuesToTheNextPeerAndNextCycle()
    {
        await using var fixture = await Fixture.CreateAsync();
        var attempts = new List<string>();
        var timeout = true;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            attempts.Add(request.RequestUri!.Host);
            return request.RequestUri.Host == "slow.test" && timeout
                ? throw new TaskCanceledException("Simulated HttpClient timeout")
                : Task.FromResult(fixture.Response());
        }));
        using var service = fixture.Service(client);
        await service.ReconcilePeersAsync(fixture.ClusterId, ["http://slow.test/", "http://healthy.test/"], CancellationToken.None);
        Assert.Equal(["slow.test", "healthy.test"], attempts);
        var peers = fixture.Health.GetStatus().Peers;
        Assert.NotNull(peers.Single(peer => peer.Url == "http://slow.test/").LastError);
        Assert.NotNull(peers.Single(peer => peer.Url == "http://healthy.test/").LastSuccessAt);
        timeout = false;
        await service.ReconcilePeersAsync(fixture.ClusterId, ["http://slow.test/"], CancellationToken.None);
        Assert.Equal(3, attempts.Count);
        Assert.Null(fixture.Health.GetStatus().Peers.Single(peer => peer.Url == "http://slow.test/").LastError);
    }

    [Fact]
    public async Task ServiceCancellationStopsTheCycleWithoutRecordingAPeerFailure()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var stop = new CancellationTokenSource();
        var attempts = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            attempts++;
            stop.Cancel();
            throw new OperationCanceledException(stop.Token);
        }));
        using var service = fixture.Service(client);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReconcilePeersAsync(
            fixture.ClusterId, ["http://first.test/", "http://second.test/"], stop.Token));
        Assert.Equal(1, attempts);
        Assert.Null(Assert.Single(fixture.Health.GetStatus().Peers).LastError);
    }

    [Fact]
    public async Task UnexpectedServiceExitDisablesMeshAndDegradesDetailedHealth()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var remove = fixture.Database.Db.CreateCommand("DELETE FROM cluster_identity")) await remove.ExecuteNonQueryAsync();
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("No request expected")));
        using var service = fixture.Service(client);
        fixture.Health.Enable();
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        var status = fixture.Health.GetStatus();
        Assert.False(status.Enabled);
        Assert.Contains("arrêté", status.DisabledReason);
        var check = await new ClusterReconciliationHealthCheck(fixture.Health).CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Degraded, check.Status);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan elapsed) => timestamp += elapsed.Ticks;
    }

    [Fact]
    public async Task AuthenticatedSelfAliasIsNeverConsideredARemoteCatchUpPeer()
    {
        await using var fixture = await Fixture.CreateAsync();
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(fixture.Response(self: true)); }));
        using var service = fixture.Service(client);
        await service.ReconcilePeersAsync(fixture.ClusterId, ["http://self-alias/"], default);
        await service.ReconcilePeersAsync(fixture.ClusterId, ["http://self-alias/"], default);
        Assert.Equal(1, calls);
        Assert.Empty(fixture.Health.GetStatus().Peers);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly string root = Path.Combine(Path.GetTempPath(), "encoding-mesh-service-" + Guid.NewGuid().ToString("N"));
        private readonly EphemeralDataProtectionProvider protection = new();
        private IConfiguration configuration = null!;
        private ClusterPeerIdentityStore identity = null!;
        private ClusterPeerIdentityStore remote = null!;
        private ClusterDataKeyStore keys = null!;
        private ClusterPeerAdmissionRepository admissions = null!;
        public TestDatabase Database { get; private set; } = null!;
        public ClusterReconciliationRepository Journal { get; private set; } = null!;
        public ClusterReconciliationHealth Health { get; } = new();
        public Guid ClusterId { get; } = Guid.NewGuid();

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            fixture.Database = await TestDatabase.CreateSqliteAsync("mesh-service");
            await DatabaseMigrator.ApplyAsync(fixture.Database.Db, Harness.RepositoryRoot(), CancellationToken.None);
            var clusters = new ClusterIdentityRepository(fixture.Database.Db);
            await clusters.AdoptAsync(fixture.ClusterId, "Test", CancellationToken.None);
            fixture.configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PluginSettings:KeyDirectory"] = fixture.root
            }).Build();
            fixture.identity = new(fixture.configuration, fixture.protection);
            fixture.remote = new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
                ["PluginSettings:KeyDirectory"] = Path.Combine(fixture.root, "remote") }).Build(), fixture.protection);
            fixture.keys = new(fixture.configuration, fixture.protection);
            fixture.admissions = new(fixture.Database.Db, clusters, fixture.identity, fixture.keys);
            await fixture.admissions.EnsureRootAdmissionAsync(fixture.ClusterId, CancellationToken.None);
            await fixture.admissions.CreateDirectApprovalGrantAsync(Guid.NewGuid(), fixture.remote.PeerId, fixture.remote.PublicKey, CancellationToken.None);
            fixture.Journal = new(fixture.Database.Db);
            return fixture;
        }

        public ClusterReconciliationService Service(HttpClient client, TimeProvider? clock = null) => new(
            new(Database.Db), Journal,
            new(Database.Db, keys, protection, NullLogger<ClusterEntityReconciliation>.Instance),
            identity, admissions, keys, Health, configuration, new ClientFactory(client),
            NullLogger<ClusterReconciliationService>.Instance, clock);

        public HttpResponseMessage Response(IReadOnlyDictionary<Guid, long>? vector = null, IReadOnlyList<ClusterChangeEvent>? changes = null, bool self = false)
        {
            var signer = self ? identity : remote;
            var body = JsonSerializer.SerializeToUtf8Bytes(new ClusterReconciliationExchangeResponse(
                1, ClusterId, vector ?? new Dictionary<Guid, long>(), changes ?? []), Json);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            response.Headers.Add(ClusterReconciliationSignature.PeerHeader, signer.PeerId.ToString("D"));
            response.Headers.Add(ClusterReconciliationSignature.TimeHeader, timestamp);
            response.Headers.Add(ClusterReconciliationSignature.SignatureHeader, ClusterReconciliationSignature.Create(signer, timestamp, body));
            return response;
        }

        public async ValueTask DisposeAsync()
        {
            identity.Dispose();
            remote.Dispose();
            keys.Dispose();
            await Database.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
