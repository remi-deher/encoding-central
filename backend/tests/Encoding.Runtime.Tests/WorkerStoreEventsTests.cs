using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Plugins;
using Encoding.Api.Workers;
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
    [InlineData("v1.-1", 3, StoreEventKind.Snapshot)]
    [InlineData("v1.9", 3, StoreEventKind.Snapshot)]      // curseur postérieur au central (base restaurée)
    [InlineData("v1.3", 3, StoreEventKind.None)]          // déjà à jour
    [InlineData("v1.1", 3, StoreEventKind.DesiredState)]  // changements manqués
    public void InitialEventFollowsTheCursor(string? lastEventId, long current, StoreEventKind expected) =>
        Assert.Equal(expected, StoreEventStream.Initial(lastEventId, current));

    private sealed class Rig : IAsyncDisposable
    {
        public required TestDatabase Database; public required WebApplication App; public required WorkerStoreStateRepository Repository;
        public required WorkerStoreStateService Service; public required Guid First; public required Guid Second; public required HttpClient Http;
        public async ValueTask DisposeAsync() { Http.Dispose(); await App.DisposeAsync(); await Database.DisposeAsync(); WorkerStoreStateEndpoints.HeartbeatInterval = TimeSpan.FromSeconds(20); }
    }

    private static async Task<Rig> CreateAsync()
    {
        var database = await TestDatabase.CreateSqliteAsync("store-events");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        for (var i = 0; i < 2; i++)
        {
            await using var insert = database.Db.CreateCommand("INSERT INTO workers(id,name,roles,max_concurrent_tasks,capabilities,last_seen_at,worker_token_hash) VALUES(@id,'w','worker',1,'[]',(strftime('%Y-%m-%d %H:%M:%f', 'now') || '000Z'),@hash)");
            insert.Parameters.AddWithValue("id", ids[i]);
            insert.Parameters.AddWithValue("hash", SqlKind.Bytes, SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("token-" + i)));
            await insert.ExecuteNonQueryAsync();
        }
        var directory = Path.Combine(Path.GetTempPath(), "encoding-store-events-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["PluginStore:Directory"] = directory, ["ToolStore:Directory"] = Path.Combine(directory, "tools") }).Build();
        var tools = new ToolStoreService(configuration, NullLogger<ToolStoreService>.Instance);
        var plugins = new PluginStoreService(configuration, StoreCatalogFixture.Create(), tools, NullLogger<PluginStoreService>.Instance);
        var repositories = new PluginRepositoryService(configuration, new HttpClientFactoryStub(), plugins, tools, NullLogger<PluginRepositoryService>.Instance);
        var repository = new WorkerStoreStateRepository(database.Db);
        var service = new WorkerStoreStateService(repository, repositories, new StoreStateBroadcaster(), new WorkerRepository(database.Db));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(service);
        var app = builder.Build();
        app.MapWorkerStoreStateEndpoints();
        await app.StartAsync();
        return new Rig { Database = database, App = app, Repository = repository, Service = service, First = ids[0], Second = ids[1],
            Http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(30) } };
    }

    private sealed class HttpClientFactoryStub : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    private static StoreDesiredItem Item(string version) => new("plugin", "official.sample", version, null, Sha);

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
    public async Task OpensWithAnEmptySnapshotThenPushesEachChangeToTheRightWorkerOnly()
    {
        await using var rig = await CreateAsync();
        var (response, reader) = await OpenAsync(rig, "token-0");
        Assert.StartsWith("text/event-stream", response.Content.Headers.ContentType!.ToString());
        var snapshot = Parse((await ReadFrameAsync(reader))!);
        Assert.Equal("snapshot", snapshot.Event); Assert.Equal("v1.0", snapshot.Id);
        Assert.Equal(0, snapshot.Data.GetProperty("revision").GetInt64()); Assert.Equal(0, snapshot.Data.GetProperty("items").GetArrayLength());

        await rig.Repository.ReplaceDesiredAsync(rig.Second, [Item("1.0.0")], CancellationToken.None); // autre worker : pas d'événement ici
        rig.Service.Changes.Notify();
        await rig.Repository.ReplaceDesiredAsync(rig.First, [Item("1.1.0")], CancellationToken.None);
        rig.Service.Changes.Notify();
        var change = Parse((await ReadFrameAsync(reader))!);
        Assert.Equal("desired-state", change.Event); Assert.Equal("v1.1", change.Id);
        Assert.Equal("1.1.0", change.Data.GetProperty("items")[0].GetProperty("version").GetString());
        Assert.Equal(Sha, change.Data.GetProperty("items")[0].GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task ReconnectionReplaysMissedChangesAndFallsBackToSnapshotForInvalidCursor()
    {
        await using var rig = await CreateAsync();
        for (var i = 1; i <= 3; i++) await rig.Repository.ReplaceDesiredAsync(rig.First, [Item($"1.{i}.0")], CancellationToken.None);

        var (_, missed) = await OpenAsync(rig, "token-0", "v1.1");
        var replay = Parse((await ReadFrameAsync(missed))!);
        Assert.Equal(("desired-state", "v1.3"), (replay.Event, replay.Id)); // l'état courant couvre les révisions 2 et 3

        var (_, expired) = await OpenAsync(rig, "token-0", "opaque-perime");
        Assert.Equal("snapshot", Parse((await ReadFrameAsync(expired))!).Event);
        var (_, future) = await OpenAsync(rig, "token-0", "v1.99");
        Assert.Equal("snapshot", Parse((await ReadFrameAsync(future))!).Event);

        // Déjà à jour : aucun événement de données, seulement le battement (sans id, donc sans effet sur la révision).
        var (_, current) = await OpenAsync(rig, "token-0", "v1.3");
        var heartbeat = (await ReadFrameAsync(current))!;
        Assert.StartsWith(":", heartbeat); Assert.DoesNotContain("id:", heartbeat);
        // Rejouer le même snapshot est sans effet côté central : il ne modifie jamais l'état.
        Assert.Equal(3, (await rig.Service.GetAsync(rig.First, CancellationToken.None))!.Desired.Revision);
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
