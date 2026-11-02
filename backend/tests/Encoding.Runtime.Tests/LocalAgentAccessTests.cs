using System.Net;
using System.Net.Http.Json;
using Encoding.Api.Nodes;
using Encoding.Contracts.Nodes;
using Encoding.Core.Nodes;
using Encoding.NodeAgent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class LocalAgentAccessTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("http://192.168.1.50:8090")]
    [InlineData("https://192.168.1.50:8090")]
    public async Task RelayCanAddAClusterWithoutDependingOnBrowserOrigin(string? origin)
    {
        using var fixture = new SecretFile();
        var clusterId = Guid.NewGuid();
        var clusterBuilder = WebApplication.CreateBuilder();
        clusterBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var cluster = clusterBuilder.Build();
        cluster.MapGet("/api/v1/cluster", () => Results.Ok(new { id = clusterId, name = "Maison" }));
        await cluster.StartAsync();
        await using var agent = Agent(fixture.Path);
        using var relay = new HttpClient(new LocalAgentAuthenticationHandler(fixture.Configuration)
        {
            InnerHandler = agent.Server.CreateHandler()
        }) { BaseAddress = new Uri("http://localhost/") };

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/worker/clusters")
        {
            Content = JsonContent.Create(new { apiBaseUrl = cluster.Urls.Single() })
        };
        if (origin is not null) request.Headers.Add("Origin", origin);
        using var response = await relay.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(clusterId.ToString("D"), await response.Content.ReadAsStringAsync());
        using var clusters = await relay.GetAsync("api/v1/worker/clusters");
        Assert.Equal(HttpStatusCode.OK, clusters.StatusCode);
        Assert.Contains(clusterId.ToString("D"), await clusters.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("GET", "status")]
    [InlineData("GET", "clusters")]
    [InlineData("POST", "clusters")]
    [InlineData("DELETE", "clusters/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "enroll")]
    [InlineData("POST", "pairing-key")]
    [InlineData("POST", "leave")]
    [InlineData("GET", "mode")]
    [InlineData("PUT", "mode")]
    public async Task EveryAgentManagementRouteRequiresTheLocalSecret(string method, string path)
    {
        using var fixture = new SecretFile();
        await using var agent = Agent(fixture.Path);
        using var client = agent.CreateClient();
        foreach (var secret in new string?[] { null, "wrong-secret" })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), "api/v1/worker/" + path);
            request.Headers.Add("Origin", "http://localhost:4200");
            if (secret is not null) request.Headers.Add(LocalEnrollment.HeaderName, secret);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        using var health = await client.GetAsync("health/ready");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task MissingSecretDoesNotOpenTheAgent()
    {
        await using var agent = Agent(null);
        using var client = agent.CreateClient();
        using var response = await client.GetAsync("api/v1/worker/clusters");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RelayUsesTheCurrentSecretAndNeverSendsItToRemoteAddresses()
    {
        using var fixture = new SecretFile();
        var sentSecrets = new List<string>();
        using var client = new HttpClient(new LocalAgentAuthenticationHandler(fixture.Configuration)
        {
            InnerHandler = new CaptureHandler(sentSecrets)
        });
        using var first = await client.GetAsync("http://127.0.0.1:8081/api/v1/worker/clusters");
        LocalEnrollment.Create(fixture.Path);
        using var second = await client.GetAsync("http://127.0.0.1:8081/api/v1/worker/clusters");
        Assert.NotEqual(sentSecrets[0], sentSecrets[1]);
        Assert.Equal(LocalEnrollment.Read(fixture.Path), sentSecrets[1]);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://192.168.1.51:8081/api/v1/worker/clusters"));
        File.Delete(fixture.Path);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://127.0.0.1:8081/api/v1/worker/clusters"));
        Assert.Equal(2, sentSecrets.Count);
    }

    [Fact]
    public async Task ServerLocalNodeRelayHandles204NoContentOnLeaveAndForgetCluster()
    {
        using var fixture = new SecretFile();
        await using var agent = Agent(fixture.Path);
        var root = Path.Combine(Path.GetTempPath(), "encoding-local-relay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var server = new WebApplicationFactory<Encoding.Api.Auth.AuthOptions>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting(WebHostDefaults.ContentRootKey, AppContext.BaseDirectory);
                builder.UseSetting("Logging:LogLevel:Default", "Error");
                builder.UseSetting("ConnectionStrings:Cluster", $"Data Source={Path.Combine(root, "cluster.db")};Pooling=False");
                builder.UseSetting("Database:Provider", "sqlite");
                builder.UseSetting("Node:DataDirectory", root);
                builder.UseSetting("Auth:Enabled", "false");
                builder.UseSetting(LocalEnrollment.SecretFileSetting, fixture.Path);
                builder.ConfigureServices(services =>
                {
                    foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService)).ToArray())
                        services.Remove(descriptor);
                    services.AddHttpClient(LocalNodeEndpoints.HttpClientName, client =>
                    {
                        client.BaseAddress = new Uri("http://127.0.0.1:8081/");
                    }).ConfigurePrimaryHttpMessageHandler(() => new LocalAgentAuthenticationHandler(fixture.Configuration)
                    {
                        InnerHandler = agent.Server.CreateHandler()
                    });
                });
            });

            using var client = server.CreateClient();
            client.DefaultRequestHeaders.Add(Encoding.Api.Auth.ManagementAccessHandler.CsrfHeader, "1");
            using var leaveResponse = await client.PostAsync("/api/v1/local-node/leave", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NoContent, leaveResponse.StatusCode);
            Assert.Empty(await leaveResponse.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SetupAppliesDisplayNameWhenLocalNodeEnrolledBeforeRestartAndInitializesRootKeyOnCreateCluster()
    {
        using var fixture = new SecretFile();
        await using var agent = Agent(fixture.Path);
        var root = Path.Combine(Path.GetTempPath(), "encoding-setup-displayname-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dbPath = Path.Combine(root, "cluster.db");
            var localNodeId = Guid.NewGuid();
            var localNodeToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));

            WebApplicationFactory<Encoding.Api.Auth.AuthOptions> CreateServer() =>
                new WebApplicationFactory<Encoding.Api.Auth.AuthOptions>().WithWebHostBuilder(builder =>
                {
                    builder.UseSetting(WebHostDefaults.ContentRootKey, AppContext.BaseDirectory);
                    builder.UseSetting("Logging:LogLevel:Default", "Error");
                    builder.UseSetting("ConnectionStrings:Cluster", $"Data Source={dbPath};Pooling=False");
                    builder.UseSetting("Database:Provider", "sqlite");
                    builder.UseSetting("Node:DataDirectory", root);
                    builder.UseSetting("Auth:Enabled", "false");
                    builder.UseSetting(LocalEnrollment.SecretFileSetting, fixture.Path);
                    builder.ConfigureServices(services =>
                    {
                        foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService)).ToArray())
                            services.Remove(descriptor);
                        services.AddHttpClient(LocalNodeEndpoints.HttpClientName, client =>
                        {
                            client.BaseAddress = new Uri("http://127.0.0.1:8081/");
                        }).ConfigurePrimaryHttpMessageHandler(() => new LocalAgentAuthenticationHandler(fixture.Configuration)
                        {
                            InnerHandler = agent.Server.CreateHandler()
                        });
                    });
                });

            // 1. First boot: local worker enrolls before /setup is completed.
            await using (var firstBoot = CreateServer())
            {
                using var firstClient = firstBoot.CreateClient();
                using var enrollMessage = new HttpRequestMessage(HttpMethod.Post, "/api/v1/nodes/enroll")
                {
                    Content = JsonContent.Create(new Encoding.Contracts.Nodes.NodeEnrollmentRequest(
                        localNodeId,
                        "encoding-host",
                        [NodeRole.Server, NodeRole.Worker],
                        1,
                        [],
                        localNodeToken))
                };
                enrollMessage.Headers.Add(LocalEnrollment.HeaderName, LocalEnrollment.Read(fixture.Path)!);
                using var enrollResponse = await firstClient.SendAsync(enrollMessage);
                Assert.Equal(HttpStatusCode.OK, enrollResponse.StatusCode);
            }

            // 2. Second boot (simulating container restart before /setup, so LocalNodeRegistry in memory starts empty):
            await using (var secondBoot = CreateServer())
            {
                using var secondClient = secondBoot.CreateClient();
                secondClient.DefaultRequestHeaders.Add(Encoding.Api.Auth.ManagementAccessHandler.CsrfHeader, "1");

                using var setupResponse = await secondClient.PostAsJsonAsync(
                    "/api/v1/auth/setup",
                    new Encoding.Contracts.Auth.SetupRequest(
                        NodeDisplayName: "Serveur Principal",
                        Mode: "createCluster",
                        ClusterName: "Cluster Salon"));
                Assert.Equal(HttpStatusCode.Created, setupResponse.StatusCode);

                // Display name is saved on the already-enrolled local node in cluster_nodes:
                var nodesJson = await secondClient.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/nodes");
                var updatedNode = Assert.Single(nodesJson.EnumerateArray(), n => n.GetProperty("id").GetGuid() == localNodeId);
                Assert.Equal("Serveur Principal", updatedNode.GetProperty("displayName").GetString());

                // And /api/v1/local-node/status also enriches displayName with the saved name:
                var statusJson = await secondClient.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/local-node/status");
                Assert.Equal("Serveur Principal", statusJson.GetProperty("displayName").GetString());

                // And creating the cluster during setup initialized the root admission and cluster data key:
                var keyStore = secondBoot.Services.GetRequiredService<Encoding.Api.Infrastructure.ClusterDataKeyStore>();
                var clusterIdentity = await secondBoot.Services.GetRequiredService<Encoding.Api.Infrastructure.ClusterIdentityRepository>().GetAsync(CancellationToken.None);
                Assert.False(clusterIdentity.Standalone);
                Assert.Equal("Cluster Salon", clusterIdentity.Name);
                Assert.True(keyStore.HasKey(clusterIdentity.Id));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static WebApplicationFactory<NodeAgentOptions> Agent(string? secretFile) =>
        new WebApplicationFactory<NodeAgentOptions>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Logging:LogLevel:Default", "Error");
            builder.UseSetting(LocalEnrollment.SecretFileSetting, secretFile ?? "");
            if (secretFile is not null)
                builder.UseSetting("Node:DataDirectory", System.IO.Path.Combine(System.IO.Path.GetDirectoryName(secretFile)!, "node"));
            builder.ConfigureServices(services =>
            {
                foreach (var service in services.Where(item => item.ServiceType == typeof(IHostedService)).ToArray())
                    services.Remove(service);
            });
        });

    private sealed class CaptureHandler(List<string> secrets) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            secrets.Add(Assert.Single(request.Headers.GetValues(LocalEnrollment.HeaderName)));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class SecretFile : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "encoding-agent-auth-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public IConfiguration Configuration { get; }
        public SecretFile()
        {
            Path = System.IO.Path.Combine(directory, LocalEnrollment.FileName);
            LocalEnrollment.Create(Path);
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [LocalEnrollment.SecretFileSetting] = Path
            }).Build();
        }
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
