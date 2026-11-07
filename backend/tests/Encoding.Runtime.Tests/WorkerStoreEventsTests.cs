using System.Security.Cryptography;
using System.Text.Json;
using System.Net.Http.Json;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Plugins;
using Encoding.Api.Workers;
using Encoding.Contracts.Workers;
using Encoding.WorkerAgent;
using Encoding.WorkerAgent.Runtime;
using Encoding.WorkerAgent.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Issue #17, P2 : flux SSE /api/v1/store/events (snapshot, reprise Last-Event-ID, heartbeat, authentification par jeton).</summary>
public sealed class WorkerStoreEventsTests
{
    private static readonly string Sha = new string('d', 64);

    [Theory]
    [InlineData(null, 3, StoreEventKind.Snapshot)]
    [InlineData("", 3, StoreEventKind.Snapshot)]
    [InlineData("garbage", 3, StoreEventKind.Snapshot)]
    [InlineData("v2.-1", 3, StoreEventKind.Snapshot)]
    [InlineData("v2.9", 3, StoreEventKind.Snapshot)]      // curseur postérieur au central (base restaurée)
    [InlineData("v2.3", 3, StoreEventKind.Snapshot)]      // reconnexion : réconciliation complète
    [InlineData("v2.1", 3, StoreEventKind.DesiredState)]  // changements manqués
    [InlineData("v1.1", 3, StoreEventKind.Snapshot)]      // curseur de l'ancien état par worker : illisible, état complet
    [InlineData("v1.3", 3, StoreEventKind.Snapshot)]
    public void InitialEventFollowsTheCursor(string? lastEventId, long current, StoreEventKind expected) =>
        Assert.Equal(expected, StoreEventStream.Initial(lastEventId, current));

    private sealed class Rig : IAsyncDisposable
    {
        public required TestDatabase Database; public required WebApplication App; public required WorkerStoreStateRepository Repository;
        public required WorkerStoreStateService Service; public required CentralStoreStateService Central; public required CentralStoreStateTests.Rig Packages;
        public required Guid First; public required Guid Second; public required HttpClient Http;
        public async ValueTask DisposeAsync()
        {
            Http.Dispose(); await App.DisposeAsync(); await Packages.DisposeAsync(); await Database.DisposeAsync();
            WorkerStoreStateEndpoints.HeartbeatInterval = TimeSpan.FromSeconds(20);
        }

        /// <summary>Installe (index) ces plugins sur le central puis lit l'état : la révision globale augmente d'un cran si l'ensemble change.</summary>
        public async Task<StoreDesiredState> InstallAsync(params string[] versions)
        {
            Packages.WriteIndexes(versions.Select((version, index) => CentralStoreStateTests.Plugin(index == 0 ? "official.sample" : "official.other" + index, version, Sha)), []);
            var state = await Central.CurrentAsync(CancellationToken.None);
            Service.Changes.Notify();
            return state;
        }
    }

    private static async Task<Rig> CreateAsync()
    {
        var packages = await CentralStoreStateTests.CreateAsync();
        var database = packages.Database;
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        for (var i = 0; i < 2; i++)
        {
            await using var insert = database.Db.CreateCommand("INSERT INTO workers(id,name,roles,max_concurrent_tasks,capabilities,last_seen_at,worker_token_hash) VALUES(@id,'w','worker',1,'[]',(strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z'),@hash)");
            insert.Parameters.AddWithValue("id", ids[i]);
            insert.Parameters.AddWithValue("hash", SqlKind.Bytes, SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("token-" + i)));
            await insert.ExecuteNonQueryAsync();
        }
        var repository = new WorkerStoreStateRepository(database.Db);
        var broadcaster = new StoreStateBroadcaster();
        var central = new CentralStoreStateService(new CentralStoreStateRepository(database.Db), packages.Plugins, packages.Tools, broadcaster, NullLogger<CentralStoreStateService>.Instance);
        var service = new WorkerStoreStateService(repository, central, broadcaster, new WorkerRepository(database.Db));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(service);
        var app = builder.Build();
        app.MapWorkerStoreStateEndpoints();
        await app.StartAsync();
        return new Rig { Database = database, App = app, Repository = repository, Service = service, Central = central, Packages = packages, First = ids[0], Second = ids[1],
            Http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(30) } };
    }

    private sealed class HttpClientFactoryStub : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    private static async Task<(HttpResponseMessage Response, StreamReader Reader)> OpenAsync(Rig rig, string? token, string? lastEventId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/store/events");
        if (token is not null) request.Headers.Add("X-Node-Token", token);
        if (lastEventId is not null) request.Headers.Add("Last-Event-ID", lastEventId);
        var response = await rig.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return (response, response.IsSuccessStatusCode ? new StreamReader(await response.Content.ReadAsStreamAsync()) : new StreamReader(Stream.Null));
    }

    /// <summary>Lit un bloc SSE (jusqu'à la ligne vide) ; null si le flux est clos ou le délai écoulé.</summary>
    private static async Task<string?> ReadFrameAsync(StreamReader reader, int seconds = 10)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var lines = new List<string>();
        while (true)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null) return null;
            if (line.Length == 0) { if (lines.Count > 0) return string.Join('\n', lines); continue; }
            lines.Add(line);
        }
    }

    private static (string? Id, string? Event, JsonElement Data) Parse(string frame)
    {
        string? id = null, name = null; var data = default(JsonElement);
        foreach (var line in frame.Split('\n'))
        {
            if (line.StartsWith("id: ", StringComparison.Ordinal)) id = line[4..];
            else if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal)) data = JsonDocument.Parse(line[6..]).RootElement.Clone();
        }
        return (id, name, data);
    }

    [Fact]
    public async Task RefusesMissingOrUnknownToken()
    {
        await using var rig = await CreateAsync();
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await OpenAsync(rig, null)).Response.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await OpenAsync(rig, "token-inconnu")).Response.StatusCode);
    }

    [Fact]
    public async Task AppliedReportUsesTokenIdentityAndReturnsCurrentDesiredRevisionOnConflict()
    {
        await using var rig = await CreateAsync();
        await rig.InstallAsync("1.0.0"); // révision globale 1
        using var anonymous = await rig.Http.PutAsJsonAsync("/api/v1/store/applied-state", new { revision = 1, items = Array.Empty<object>() });
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var staleRequest = new HttpRequestMessage(HttpMethod.Put, "/api/v1/store/applied-state")
        { Content = System.Net.Http.Json.JsonContent.Create(new { revision = 2, items = Array.Empty<object>() }) };
        staleRequest.Headers.Add("X-Node-Token", "token-0");
        using var stale = await rig.Http.SendAsync(staleRequest);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("desiredRevision", await stale.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        using var acceptedRequest = new HttpRequestMessage(HttpMethod.Put, "/api/v1/store/applied-state")
        { Content = System.Net.Http.Json.JsonContent.Create(new { revision = 1, items = Array.Empty<object>() }) };
        acceptedRequest.Headers.Add("X-Node-Token", "token-0");
        using var accepted = await rig.Http.SendAsync(acceptedRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(1, (await rig.Repository.GetAppliedAsync(rig.First, CancellationToken.None))!.Revision);
        Assert.Equal(0, (await rig.Repository.GetAppliedAsync(rig.Second, CancellationToken.None))!.Revision);
    }

    [Fact]
    public async Task WorkerCoordinatorReportsAppliedFailedAndRemovedPackages()
    {
        await using var rig = await CreateAsync();
        var data = Path.Combine(Path.GetTempPath(), "encoding-store-worker-" + Guid.NewGuid().ToString("N"));
        var pluginRoot = Path.Combine(data, "plugins");
        var toolRoot = Path.Combine(data, "tools");
        Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(Path.Combine(data, "node-identity.json"), JsonSerializer.Serialize(new
        {
            id = rig.First, centralId = "test", centralName = "test", apiBaseUrl = rig.Http.BaseAddress!.ToString(),
            workerToken = "token-0", enrolled = true
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var options = new WorkerAgentOptions("worker", [WorkerRole.Worker], 1, data, rig.Http.BaseAddress.ToString(), false,
            TimeSpan.FromSeconds(30), 0);
        var identities = new WorkerIdentityStore(options);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Runtime:CustomPluginDirectory"] = pluginRoot,
            ["Runtime:ManagedToolDirectory"] = toolRoot
        }).Build();
        var probe = new ToolCapabilityProbe(null!, null!, options, NullLogger<ToolCapabilityProbe>.Instance);
        var plugins = new PluginSyncService(identities, config, probe, NullLogger<PluginSyncService>.Instance);
        var tools = new ToolPackageSyncService(identities, config, probe, NullLogger<ToolPackageSyncService>.Instance);
        var coordinator = new StoreSyncCoordinator(options, identities, plugins, tools, NullLogger<StoreSyncCoordinator>.Instance);
        var appliedDirectory = Path.Combine(pluginRoot, "official.sample", "1.0.0");
        Directory.CreateDirectory(appliedDirectory);
        await File.WriteAllTextAsync(Path.Combine(appliedDirectory, CustomPluginCatalog.VerifiedMarker), Sha);
        await rig.InstallAsync("1.0.0", "1.0.0"); // révision globale 1 : official.sample et official.other1
        var server = rig.Http.BaseAddress.ToString();
        await coordinator.ReconcileAsync(server, new WorkerStoreDesiredState(1,
            [new("plugin", "official.sample", "1.0.0", null, Sha), new("plugin", "official.missing", "1.0.0", null, Sha)]), CancellationToken.None);
        var reported = (await rig.Repository.GetAppliedAsync(rig.First, CancellationToken.None))!.Items;
        Assert.Contains(reported, item => item.Id == "official.sample" && item.Status == "applied");
        Assert.Contains(reported, item => item.Id == "official.missing" && item.Status == "failed" && item.ErrorCode == "install_failed");

        await rig.InstallAsync("1.0.0"); // révision globale 2 : official.other1 retiré du central
        await coordinator.ReconcileAsync(server, new WorkerStoreDesiredState(2,
            [new("plugin", "official.sample", "1.0.0", null, Sha)]), CancellationToken.None);
        reported = (await rig.Repository.GetAppliedAsync(rig.First, CancellationToken.None))!.Items;
        Assert.Contains(reported, item => item.Id == "official.missing" && item.Status == "removed");
        Directory.Delete(data, recursive: true);
    }

    [Fact]
    public async Task EveryWorkerReceivesTheSameGlobalStateAndEachChangeOnce()
    {
        await using var rig = await CreateAsync();
        await rig.InstallAsync("1.0.0"); // révision globale 1
        var (response, first) = await OpenAsync(rig, "token-0");
        var (_, second) = await OpenAsync(rig, "token-1");
        Assert.StartsWith("text/event-stream", response.Content.Headers.ContentType!.ToString());
        var snapshot = Parse((await ReadFrameAsync(first))!);
        Assert.Equal("snapshot", snapshot.Event); Assert.Equal("v2.1", snapshot.Id);
        Assert.Equal(1, snapshot.Data.GetProperty("revision").GetInt64());
        Assert.Equal("official.sample", snapshot.Data.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal(Sha, snapshot.Data.GetProperty("items")[0].GetProperty("sha256").GetString());
        var other = Parse((await ReadFrameAsync(second))!);
        Assert.Equal((snapshot.Id, snapshot.Event), (other.Id, other.Event)); // même état pour tous les workers : aucune liste par worker

        await rig.InstallAsync("1.1.0"); // mise à jour du plugin : révision globale 2
        var change = Parse((await ReadFrameAsync(first))!);
        Assert.Equal(("desired-state", "v2.2"), (change.Event, change.Id));
        Assert.Equal("1.1.0", change.Data.GetProperty("items")[0].GetProperty("version").GetString());
        var otherChange = Parse((await ReadFrameAsync(second))!);
        Assert.Equal("v2.2", otherChange.Id);
    }

    [Fact]
    public async Task FullStateCarriesInstalledPluginsAndToolsWithSha256()
    {
        await using var rig = await CreateAsync();
        rig.Packages.WriteIndexes([CentralStoreStateTests.Plugin("base", "1.10.3", Sha)], [CentralStoreStateTests.Tool("ffmpeg", "6.1.1", "linux-x64", new string('e', 64))]);
        var (_, reader) = await OpenAsync(rig, "token-0");
        var items = Parse((await ReadFrameAsync(reader))!).Data.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(("plugin", "base", "1.10.3", Sha), (items[0].GetProperty("kind").GetString(), items[0].GetProperty("id").GetString(), items[0].GetProperty("version").GetString(), items[0].GetProperty("sha256").GetString()));
        Assert.Equal(("tool", "linux-x64", new string('e', 64)), (items[1].GetProperty("kind").GetString(), items[1].GetProperty("platform").GetString(), items[1].GetProperty("sha256").GetString()));
    }

    [Fact]
    public async Task CentralRestartReplaysPersistedGlobalStateAsSnapshot()
    {
        await using var rig = await CreateAsync();
        await rig.InstallAsync("1.2.0");
        rig.Http.Dispose();
        await rig.App.StopAsync();
        await rig.App.DisposeAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(rig.Service);
        var restarted = builder.Build();
        restarted.MapWorkerStoreStateEndpoints();
        await restarted.StartAsync();
        rig.App = restarted;
        rig.Http = new HttpClient { BaseAddress = new Uri(restarted.Urls.Single()), Timeout = TimeSpan.FromSeconds(30) };

        var (_, reader) = await OpenAsync(rig, "token-0");
        var snapshot = Parse((await ReadFrameAsync(reader))!);
        Assert.Equal("snapshot", snapshot.Event);
        Assert.Equal(1, snapshot.Data.GetProperty("revision").GetInt64());
        Assert.Equal("1.2.0", snapshot.Data.GetProperty("items")[0].GetProperty("version").GetString());
    }

    [Fact]
    public async Task ReconnectionReplaysMissedChangesAndFallsBackToSnapshotForInvalidOrV1Cursor()
    {
        await using var rig = await CreateAsync();
        for (var i = 1; i <= 3; i++) await rig.InstallAsync($"1.{i}.0");

        var (_, missed) = await OpenAsync(rig, "token-0", "v2.1");
        var replay = Parse((await ReadFrameAsync(missed))!);
        Assert.Equal(("desired-state", "v2.3"), (replay.Event, replay.Id)); // l'état courant couvre les révisions 2 et 3

        var (_, expired) = await OpenAsync(rig, "token-0", "opaque-perime");
        Assert.Equal("snapshot", Parse((await ReadFrameAsync(expired))!).Event);
        var (_, future) = await OpenAsync(rig, "token-0", "v2.99");
        Assert.Equal("snapshot", Parse((await ReadFrameAsync(future))!).Event);

        // Un curseur v1 (ancien état par worker) est illisible : le worker reçoit l'état complet, même s'il pointe avant la révision courante.
        var (_, legacy) = await OpenAsync(rig, "token-0", "v1.1");
        var legacySnapshot = Parse((await ReadFrameAsync(legacy))!);
        Assert.Equal(("snapshot", "v2.3"), (legacySnapshot.Event, legacySnapshot.Id));
        Assert.Equal("1.3.0", legacySnapshot.Data.GetProperty("items")[0].GetProperty("version").GetString());

        // Même à jour, une reconnexion renvoie un snapshot complet pour vérifier la convergence locale.
        var (_, current) = await OpenAsync(rig, "token-0", "v2.3");
        var currentSnapshot = Parse((await ReadFrameAsync(current))!);
        Assert.Equal(("snapshot", "v2.3"), (currentSnapshot.Event, currentSnapshot.Id));
        // Rejouer le même snapshot est sans effet côté central : il ne modifie jamais l'état.
        Assert.Equal(3, (await rig.Service.GetAsync(rig.First, CancellationToken.None))!.Desired.Revision);
    }

    [Fact]
    public async Task IncompatibleWorkerGetsVersionIncompatibleAndNoStream()
    {
        await using var rig = await CreateAsync();
        await rig.InstallAsync("1.0.0");
        await using (var mark = rig.Database.Db.CreateCommand("UPDATE workers SET incompatible_reason = 'Version 0.0.1 différente de celle du central.' WHERE id = @id"))
        { mark.Parameters.AddWithValue("id", rig.Second); await mark.ExecuteNonQueryAsync(); }

        var (blocked, _) = await OpenAsync(rig, "token-1");
        Assert.Equal(System.Net.HttpStatusCode.Conflict, blocked.StatusCode);
        using var body = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        Assert.Equal("version_incompatible", body.RootElement.GetProperty("code").GetString());
        Assert.NotEqual("text/event-stream", blocked.Content.Headers.ContentType?.MediaType);

        // Un worker compatible du même central reçoit toujours son flux.
        var (ok, reader) = await OpenAsync(rig, "token-0");
        Assert.Equal(System.Net.HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("snapshot", Parse((await ReadFrameAsync(reader))!).Event);
    }

    [Fact]
    public async Task WorkerStateReportsGlobalRevisionAppliedAndGap()
    {
        await using var rig = await CreateAsync();
        await rig.InstallAsync("1.0.0");
        await rig.InstallAsync("1.1.0"); // révision globale 2
        using var report = new HttpRequestMessage(HttpMethod.Put, "/api/v1/store/applied-state")
        { Content = System.Net.Http.Json.JsonContent.Create(new { revision = 1, items = new[] { new { kind = "plugin", id = "official.sample", version = "1.0.0", platform = (string?)null, sha256 = Sha, status = "applied", errorCode = (string?)null } } }) };
        report.Headers.Add("X-Node-Token", "token-0");
        Assert.Equal(System.Net.HttpStatusCode.OK, (await rig.Http.SendAsync(report)).StatusCode);

        var state = (await rig.Service.GetAsync(rig.First, CancellationToken.None))!;
        Assert.Equal(2, state.Desired.Revision); Assert.Equal(1, state.Applied.Revision);
        Assert.Equal(1, state.Gap.RevisionLag); Assert.False(state.Gap.InSync);
        var gap = Assert.Single(state.Gap.Items);
        Assert.Equal(("version_mismatch", "1.1.0", "1.0.0"), (gap.Reason, gap.DesiredVersion, gap.AppliedVersion));
        Assert.Equal(2, (await rig.Service.GetAsync(rig.Second, CancellationToken.None))!.Desired.Revision); // même révision globale pour un worker qui n'a rien rapporté
        Assert.Equal("missing", Assert.Single((await rig.Service.GetAsync(rig.Second, CancellationToken.None))!.Gap.Items).Reason);
    }

    [Fact]
    public async Task HeartbeatsDoNotCarryARevision()
    {
        await using var rig = await CreateAsync();
        WorkerStoreStateEndpoints.HeartbeatInterval = TimeSpan.FromMilliseconds(200);
        var (_, reader) = await OpenAsync(rig, "token-0");
        Assert.Equal("snapshot", Parse((await ReadFrameAsync(reader))!).Event);
        var ping = (await ReadFrameAsync(reader))!;
        Assert.Equal(": ping", ping);
    }
}
