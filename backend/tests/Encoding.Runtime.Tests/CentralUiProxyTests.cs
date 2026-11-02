using System.Net;
using Encoding.NodeAgent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Le relais de l'interface du worker ne doit jamais prêter la session d'une personne à une autre.</summary>
public sealed class CentralUiProxyTests
{
    private static async Task<WebApplication> StartCentralAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var central = builder.Build();
        central.MapPost("/api/v1/auth/login", (HttpContext http) =>
        {
            http.Response.Cookies.Append("encoding_session", "secret-session");
            return Results.NoContent();
        });
        central.MapGet("/api/v1/me", (HttpContext http) =>
            http.Request.Cookies["encoding_session"] == "secret-session" ? Results.Ok(new { user = "admin" }) : Results.Unauthorized());
        await central.StartAsync();
        return central;
    }

    private static async Task<WebApplication> StartWorkerUiAsync(string centralUrl)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddHttpClient(CentralUiProxy.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(CentralUiProxy.CreateHandler);
        var worker = builder.Build();
        CentralUiProxy.Use(worker, _ => Task.FromResult<string?>(centralUrl), 0, null);
        await worker.StartAsync();
        return worker;
    }

    [Fact]
    public async Task ASessionOpenedThroughTheProxyIsNotReusedByAnotherVisitor()
    {
        await using var central = await StartCentralAsync();
        await using var worker = await StartWorkerUiAsync(central.Urls.Single());
        var address = new Uri(worker.Urls.Single());

        // Chaque visiteur a son propre conteneur de cookies, comme deux navigateurs.
        var first = new CookieContainer();
        using var firstClient = new HttpClient(new HttpClientHandler { CookieContainer = first }) { BaseAddress = address };
        using var secondClient = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = address };

        (await firstClient.PostAsync("/api/v1/auth/login", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await firstClient.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await secondClient.GetAsync("/api/v1/me")).StatusCode);
    }
}
