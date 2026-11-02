using System.Net;
using Encoding.NodeAgent;
using Encoding.NodeAgent.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Ajout d'un cluster par son adresse dans la console worker (découverte DNS-SD indisponible entre conteneurs).</summary>
public sealed class WorkerClusterAddressTests
{
    private static WorkerClusterDiscovery Discovery(string? dataDirectory = null) =>
        new(NodeAgentOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(
            [new("Node:DataDirectory", dataDirectory ?? Path.Combine(Path.GetTempPath(), "encoding-cluster-address-" + Guid.NewGuid().ToString("N")))]).Build()),
            NullLogger<WorkerClusterDiscovery>.Instance);

    [Theory]
    [InlineData("")]
    [InlineData("pas une adresse")]
    [InlineData("ftp://192.168.1.51/")]
    [InlineData("http://user:secret@192.168.1.51:8090")]
    public async Task InvalidAddressesAreRefused(string address) =>
        await Assert.ThrowsAsync<ArgumentException>(() => Discovery().AddByAddressAsync(address, CancellationToken.None));

    [Fact]
    public async Task AddressOfAClusterIsListed()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var id = Guid.NewGuid();
        var serving = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var body = System.Text.Encoding.UTF8.GetBytes($"{{\"id\":\"{id}\",\"name\":\"Maison\"}}");
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        });

        var discovery = Discovery();
        var cluster = await discovery.AddByAddressAsync($"http://127.0.0.1:{port}/chemin/ignore", CancellationToken.None);
        await serving;

        Assert.Equal(id, cluster.Id);
        Assert.Equal($"http://127.0.0.1:{port}/", cluster.ApiBaseUrl);
        Assert.Contains(discovery.List(), found => found.Id == id && found.Name == "Maison");
    }

    [Fact]
    public async Task AddedAddressSurvivesARestart()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var id = Guid.NewGuid();
        var serving = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var body = System.Text.Encoding.UTF8.GetBytes($"{{\"id\":\"{id}\",\"name\":\"Maison\"}}");
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        });
        var directory = Path.Combine(Path.GetTempPath(), "encoding-cluster-address-" + Guid.NewGuid().ToString("N"));
        await Discovery(directory).AddByAddressAsync($"http://127.0.0.1:{port}/", CancellationToken.None);
        await serving;

        var restarted = Discovery(directory);
        Assert.Empty(restarted.List());
        var serving2 = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var body = System.Text.Encoding.UTF8.GetBytes($"{{\"id\":\"{id}\",\"name\":\"Maison\"}}");
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await restarted.StartAsync(cancellation.Token);
        while (restarted.List().Count == 0 && !cancellation.IsCancellationRequested) await Task.Delay(50);
        await restarted.StopAsync(CancellationToken.None);
        await serving2;

        Assert.Contains(restarted.List(), found => found.Id == id);
    }

    [Fact]
    public async Task UnreachableAddressIsReportedAsAnError() =>
        await Assert.ThrowsAsync<HttpRequestException>(() => Discovery().AddByAddressAsync($"http://127.0.0.1:{FreePort()}/", CancellationToken.None));

    private static int FreePort()
    {
        var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }
}
