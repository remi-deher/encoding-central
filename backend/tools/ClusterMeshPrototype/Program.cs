using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

const string GroupAddress = "239.255.42.99";
const int DiscoveryPort = 43123;
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

if (args.Length > 0 && args[0] == "node")
{
    await RunNodeAsync(args);
    return;
}

if (args.Length > 0 && args[0] == "test")
{
    await RunThreeNodeScenarioAsync();
    return;
}

Console.WriteLine("Cluster mesh prototype\n  dotnet run -- test");

async Task RunNodeAsync(string[] arguments)
{
    var id = arguments[1];
    var port = int.Parse(arguments[2]);
    var clusterId = arguments[3];
    var secret = Convert.FromBase64String(arguments[4]);
    var endpoint = $"http://127.0.0.1:{port}";
    var peers = new ConcurrentDictionary<string, Peer>(StringComparer.Ordinal);
    var presence = new ConcurrentDictionary<string, Presence>(StringComparer.Ordinal);
    var nonceCache = new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal);
    using var stop = new CancellationTokenSource();

    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls(endpoint);
    var app = builder.Build();
    app.MapGet("/health", () => Results.Ok(new { id }));
    app.MapGet("/state", () => Results.Ok(new NodeState(id, peers.Keys.Order().ToArray(), presence.Values.OrderBy(p => p.NodeId).ToArray())));
    app.MapPost("/presence", async (HttpRequest request) =>
    {
        var authenticated = await AuthenticateAsync(request, secret, nonceCache);
        if (authenticated != AuthResult.Accepted) return Results.StatusCode(authenticated == AuthResult.Replay ? 409 : 401);
        var items = await JsonSerializer.DeserializeAsync<Presence[]>(request.Body, jsonOptions);
        if (items is null || items.Any(item => string.IsNullOrWhiteSpace(item.NodeId) || item.Sequence < 1)) return Results.BadRequest();
        foreach (var item in items) MergePresence(presence, item);
        return Results.Accepted();
    });

    var discovery = Task.Run(() => DiscoveryLoopAsync(id, clusterId, endpoint, secret, peers, stop.Token));
    var gossip = Task.Run(() => GossipLoopAsync(id, secret, peers, presence, stop.Token));
    app.Lifetime.ApplicationStopping.Register(() => stop.Cancel());
    Console.WriteLine($"READY {id} {endpoint}");
    await app.RunAsync();
    stop.Cancel();
    await Task.WhenAll(discovery, gossip);
}

async Task DiscoveryLoopAsync(string id, string clusterId, string endpoint, byte[] secret,
    ConcurrentDictionary<string, Peer> peers, CancellationToken cancellationToken)
{
    using var socket = new UdpClient(AddressFamily.InterNetwork);
    socket.ExclusiveAddressUse = false;
    socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    socket.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
    socket.JoinMulticastGroup(IPAddress.Parse(GroupAddress), IPAddress.Loopback);
    socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Loopback.GetAddressBytes());
    socket.MulticastLoopback = true;
    var receiver = Task.Run(async () =>
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var packet = await socket.ReceiveAsync(cancellationToken);
                var separator = Array.IndexOf(packet.Buffer, (byte)'\n');
                if (separator <= 0) continue;
                var body = packet.Buffer[..separator];
                var signature = Encoding.ASCII.GetString(packet.Buffer[(separator + 1)..]);
                if (!Crypto.FixedTimeEquals(signature, Crypto.Sign(secret, body))) continue;
                var announcement = JsonSerializer.Deserialize<Announcement>(body, jsonOptions);
                if (announcement is null || announcement.ClusterId != clusterId || announcement.NodeId == id) continue;
                if (!Uri.TryCreate(announcement.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "http") continue;
                peers[announcement.NodeId] = new Peer(announcement.NodeId, uri, DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }, cancellationToken);

    using var sender = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true, MulticastLoopback = true };
    sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Loopback.GetAddressBytes());
    var destination = new IPEndPoint(IPAddress.Parse(GroupAddress), DiscoveryPort);
    while (!cancellationToken.IsCancellationRequested)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new Announcement(clusterId, id, endpoint), jsonOptions);
        var packet = body.Concat(Encoding.ASCII.GetBytes("\n" + Crypto.Sign(secret, body))).ToArray();
        try { await sender.SendAsync(packet, destination, cancellationToken); }
        catch (OperationCanceledException) { break; }
        await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }
    socket.Dispose();
    await receiver;
}

async Task GossipLoopAsync(string id, byte[] secret, ConcurrentDictionary<string, Peer> peers,
    ConcurrentDictionary<string, Presence> presence, CancellationToken cancellationToken)
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
    while (!cancellationToken.IsCancellationRequested)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var old in presence.Where(p => now - p.Value.LastSeen > TimeSpan.FromSeconds(8)).Select(p => p.Key).ToArray())
            presence.TryRemove(old, out _);
        foreach (var peer in peers.Values)
        {
            try
            {
                var records = presence.Values.Where(p => now - p.LastSeen <= TimeSpan.FromSeconds(8)).ToArray();
                using var request = await Crypto.CreateRequestAsync(HttpMethod.Post, new Uri(peer.Endpoint, "/presence"), records, secret, jsonOptions);
                using var response = await client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) continue;
                foreach (var record in records) MergePresence(presence, record);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException) { }
        }
        await Task.Delay(200, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }
}

static void MergePresence(ConcurrentDictionary<string, Presence> presence, Presence incoming)
{
    presence.AddOrUpdate(incoming.NodeId, incoming, (_, existing) =>
        incoming.Incarnation > existing.Incarnation ||
        (incoming.Incarnation == existing.Incarnation && incoming.Sequence > existing.Sequence) ? incoming : existing);
}

static async Task<AuthResult> AuthenticateAsync(HttpRequest request, byte[] secret,
    ConcurrentDictionary<string, DateTimeOffset> nonceCache)
{
    if (!request.Headers.TryGetValue("X-Mesh-Timestamp", out var timestampValue)
        || !request.Headers.TryGetValue("X-Mesh-Nonce", out var nonceValue)
        || !request.Headers.TryGetValue("X-Mesh-Signature", out var signatureValue)
        || !long.TryParse(timestampValue, out var timestamp)) return AuthResult.Invalid;
    var sentAt = DateTimeOffset.FromUnixTimeSeconds(timestamp);
    if ((DateTimeOffset.UtcNow - sentAt).Duration() > TimeSpan.FromSeconds(30)) return AuthResult.Invalid;
    using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
    var body = await reader.ReadToEndAsync();
    request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
    var canonical = Encoding.UTF8.GetBytes($"{request.Method}\n{request.Path}\n{timestamp}\n{nonceValue}\n{body}");
    if (!Crypto.FixedTimeEquals(signatureValue!, Crypto.Sign(secret, canonical))) return AuthResult.Invalid;
    var now = DateTimeOffset.UtcNow;
    foreach (var expired in nonceCache.Where(n => now - n.Value > TimeSpan.FromMinutes(1)).Select(n => n.Key).ToArray()) nonceCache.TryRemove(expired, out _);
    return nonceCache.TryAdd(nonceValue!, now) ? AuthResult.Accepted : AuthResult.Replay;
}

async Task RunThreeNodeScenarioAsync()
{
    var assembly = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate process executable");
    var assemblyPath = Path.Combine(AppContext.BaseDirectory, "ClusterMeshPrototype.dll");
    var clusterId = Guid.NewGuid().ToString("D");
    var secret = RandomNumberGenerator.GetBytes(32);
    var secretArg = Convert.ToBase64String(secret);
    var ports = new[] { 6411, 6412, 6413 };
    var ids = new[] { "node-a", "node-b", "node-c" };
    var processes = new List<Process>();
    try
    {
        for (var i = 0; i < ids.Length; i++)
        {
            var process = Process.Start(new ProcessStartInfo("dotnet")
            {
                ArgumentList = { assemblyPath, "node", ids[i], ports[i].ToString(), clusterId, secretArg },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }) ?? throw new InvalidOperationException("Could not start node process");
            processes.Add(process);
        }
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        await WaitUntilAsync(async () =>
        {
            foreach (var port in ports)
                if (!await IsHealthyAsync(client, port)) return false;
            return true;
        }, TimeSpan.FromSeconds(15), "three node processes start");
        await WaitUntilAsync(async () =>
        {
            foreach (var port in ports)
            {
                var state = await client.GetFromJsonAsync<NodeState>($"http://127.0.0.1:{port}/state", jsonOptions);
                if (state?.Peers.Length != 2) return false;
            }
            return true;
        }, TimeSpan.FromSeconds(15), "multicast peer discovery");

        var initial = new Presence("node-a", 1, 1, DateTimeOffset.UtcNow);
        using (var request = await Crypto.CreateRequestAsync(HttpMethod.Post, new Uri($"http://127.0.0.1:{ports[0]}/presence"), new[] { initial }, secret, jsonOptions))
            Ensure((int)(await client.SendAsync(request)).StatusCode == 202, "authenticated presence accepted");

        var wrongSecretRequest = await Crypto.CreateRequestAsync(HttpMethod.Post,
            new Uri($"http://127.0.0.1:{ports[0]}/presence"), new[] { new Presence("intruder", 1, 1, DateTimeOffset.UtcNow) },
            RandomNumberGenerator.GetBytes(32), jsonOptions);
        using (wrongSecretRequest)
            Ensure((int)(await client.SendAsync(wrongSecretRequest)).StatusCode == 401, "invalid peer authentication rejected");

        using (var replay = await Crypto.CreateRequestAsync(HttpMethod.Post, new Uri($"http://127.0.0.1:{ports[0]}/presence"),
                   new[] { new Presence("node-a", 1, 2, DateTimeOffset.UtcNow) }, secret, jsonOptions))
        {
            using var replayCopy = await CloneRequestAsync(replay);
            var first = await client.SendAsync(replay);
            Ensure((int)first.StatusCode == 202, "first signed message accepted");
            var replayed = await client.SendAsync(replayCopy);
            Ensure((int)replayed.StatusCode == 409, "replayed signed message rejected");
        }

        foreach (var i in new[] { 1, 2 })
        {
            using var local = await Crypto.CreateRequestAsync(HttpMethod.Post,
                new Uri($"http://127.0.0.1:{ports[i]}/presence"),
                new[] { new Presence(ids[i], 1, 1, DateTimeOffset.UtcNow) }, secret, jsonOptions);
            Ensure((int)(await client.SendAsync(local)).StatusCode == 202, $"{ids[i]} publishes its presence");
        }

        await WaitUntilAsync(async () =>
        {
            foreach (var port in ports)
            {
                var state = await client.GetFromJsonAsync<NodeState>($"http://127.0.0.1:{port}/state", jsonOptions);
                if (state is null || state.Presence.Length != 3
                    || !state.Presence.Any(p => p.NodeId == "node-a" && p.Sequence == 2)) return false;
            }
            return true;
        }, TimeSpan.FromSeconds(10), "all three presence records converge on every node");

        Console.WriteLine("PASS: 3 process discovery (2 peers each), shared-key authentication, replay rejection, presence convergence.");
    }
    finally
    {
        foreach (var process in processes)
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        foreach (var process in processes) process.Dispose();
    }
}

async Task<bool> IsHealthyAsync(HttpClient client, int port)
{
    try { return (await client.GetAsync($"http://127.0.0.1:{port}/health")).IsSuccessStatusCode; }
    catch (HttpRequestException) { return false; }
    catch (TaskCanceledException) { return false; }
}

async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string description)
{
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed < timeout)
    {
        if (await condition()) return;
        await Task.Delay(100);
    }
    throw new TimeoutException($"Timed out waiting for {description}");
}

async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage original)
{
    var clone = new HttpRequestMessage(original.Method, original.RequestUri);
    foreach (var header in original.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
    clone.Content = new ByteArrayContent(await original.Content!.ReadAsByteArrayAsync());
    foreach (var header in original.Content.Headers) clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
    return clone;
}

static void Ensure(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {description}");
    Console.WriteLine($"PASS: {description}");
}

enum AuthResult { Accepted, Invalid, Replay }
record Announcement(string ClusterId, string NodeId, string Endpoint);
record Peer(string NodeId, Uri Endpoint, DateTimeOffset LastSeen);
record Presence(string NodeId, long Incarnation, long Sequence, DateTimeOffset LastSeen);
record NodeState(string NodeId, string[] Peers, Presence[] Presence);

static class Crypto
{
    public static string Sign(byte[] secret, byte[] value) => Convert.ToHexString(HMACSHA256.HashData(secret, value));
    public static bool FixedTimeEquals(string provided, string expected)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(provided), Convert.FromHexString(expected)); }
        catch (FormatException) { return false; }
    }
    public static async Task<HttpRequestMessage> CreateRequestAsync<T>(HttpMethod method, Uri uri, T value, byte[] secret, JsonSerializerOptions options)
    {
        var body = JsonSerializer.Serialize(value, options);
        var nonce = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var canonical = Encoding.UTF8.GetBytes($"{method.Method}\n{uri.AbsolutePath}\n{timestamp}\n{nonce}\n{body}");
        var request = new HttpRequestMessage(method, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Mesh-Timestamp", timestamp.ToString());
        request.Headers.Add("X-Mesh-Nonce", nonce);
        request.Headers.Add("X-Mesh-Signature", Sign(secret, canonical));
        await Task.CompletedTask;
        return request;
    }
}
