using System.Net;
using Encoding.Core.Workers;
using Encoding.WorkerAgent;
using Encoding.WorkerAgent.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Seules deux routes de l'agent sont ouvertes sans secret, et seulement avant le rattachement de l'image worker.</summary>
public sealed class WorkerAgentGateTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "worker-gate-" + Guid.NewGuid().ToString("N"));
    private WorkerIdentityStore identities = null!;
    private string secretFile = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        secretFile = Path.Combine(directory, LocalEnrollment.FileName);
        LocalEnrollment.Create(secretFile);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(directory, true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private async Task<(WebApplication App, HttpClient Client)> StartAsync(bool workerImage)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Worker:DataDirectory"] = directory,
            ["Worker:WorkerImage"] = workerImage ? "true" : "false",
            ["Worker:Roles"] = workerImage ? "worker" : "server,worker",
            [LocalEnrollment.SecretFileSetting] = secretFile,
        }).Build();
        var options = WorkerAgentOptions.FromConfiguration(configuration);
        identities = new WorkerIdentityStore(options);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(identities);
        var app = builder.Build();
        WorkerAgentGate.Use(app, options);
        foreach (var path in new[] { WorkerAgentGate.HardwarePath, "/api/v1/worker/status", WorkerAgentGate.ClustersPath })
        {
            app.MapGet(path, () => Results.Ok(new { path }));
        }
        app.MapPost(WorkerAgentGate.ClustersPath, () => Results.Ok());
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.Single()) });
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, HttpMethod method, string path, string? secret = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (secret is not null) request.Headers.Add(LocalEnrollment.HeaderName, secret);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task AnUnenrolledWorkerImageOpensOnlyItsHardwareAndTheChoiceOfCentral()
    {
        var (app, client) = await StartAsync(workerImage: true);
        await using var _ = app;
        using var __ = client;

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Get, WorkerAgentGate.HardwarePath));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Post, WorkerAgentGate.ClustersPath));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, HttpMethod.Get, "/api/v1/worker/status"));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, HttpMethod.Get, WorkerAgentGate.ClustersPath));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Get, "/api/v1/worker/status", File.ReadAllText(secretFile).Trim()));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, HttpMethod.Get, "/api/v1/worker/status", "mauvais-secret"));
    }

    [Fact]
    public async Task OnceTheCentralIsKnownTheChoiceIsClosedAndOnceEnrolledSoIsTheHardware()
    {
        var (app, client) = await StartAsync(workerImage: true);
        await using var _ = app;
        using var __ = client;

        await identities.SaveEnrollmentAsync(Guid.NewGuid().ToString("D"), "Maison", "http://server:8080/", CancellationToken.None);
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, HttpMethod.Post, WorkerAgentGate.ClustersPath));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, HttpMethod.Get, WorkerAgentGate.HardwarePath));

        await identities.MarkEnrolledAsync(CancellationToken.None);
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, HttpMethod.Get, WorkerAgentGate.HardwarePath));
    }

    [Fact]
    public async Task ACentralsEmbeddedAgentNeverOpensAnyRouteWithoutTheSecret()
    {
        var (app, client) = await StartAsync(workerImage: false);
        await using var _ = app;
        using var __ = client;

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, HttpMethod.Get, WorkerAgentGate.HardwarePath));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, HttpMethod.Post, WorkerAgentGate.ClustersPath));
    }

    [Theory]
    [InlineData("abcDEF123-_.~+/=%", true)]
    [InlineData("", false)]
    [InlineData("a; Path=/", false)]
    [InlineData("a\r\nX-Evil: 1", false)]
    public void OnlyASafeSessionValueIsCopiedIntoAHeader(string value, bool expected) =>
        Assert.Equal(expected, WorkerEnrollmentService.IsSessionValue(value));
}
