using System.Net;
using System.Net.Http.Json;
using Encoding.Core.Nodes;

namespace Encoding.Launcher.Tests;

internal static class EndpointTests
{
    public static async Task RunAsync()
    {
        // Faux lanceur : conserve le mode reçu.
        var current = NodeMode.Both;
        var launcher = WebApplication.CreateBuilder().Build();
        launcher.Urls.Add("http://127.0.0.1:0");
        launcher.MapGet("/mode", () => Results.Ok(current));
        launcher.MapPut("/mode", (NodeMode mode) => { current = NodeMode.Validate(mode); return Results.Ok(current); });
        await launcher.StartAsync();
        var launcherUrl = launcher.Urls.First();

        await using (var managed = await StartHostAsync(launcherUrl))
        using (var client = new HttpClient { BaseAddress = new Uri(managed.Urls.First()) })
        {
            var status = await client.GetFromJsonAsync<NodeModeStatus>("/mode");
            Check.That(status == new NodeModeStatus(true, true, true), "Le nœud géré expose le mode du lanceur.");
            using var changed = await client.PutAsJsonAsync("/mode", new NodeMode(false, true));
            Check.That(changed.StatusCode == HttpStatusCode.OK && current == new NodeMode(false, true), "Le changement de mode est transmis au lanceur.");
            using var empty = await client.PutAsJsonAsync("/mode", new NodeMode(false, false));
            Check.That(empty.StatusCode == HttpStatusCode.BadRequest && current == new NodeMode(false, true), "Un mode sans fonction est refusé sans toucher au lanceur.");
        }

        await using (var unmanaged = await StartHostAsync(null))
        using (var client = new HttpClient { BaseAddress = new Uri(unmanaged.Urls.First()) })
        {
            var status = await client.GetFromJsonAsync<NodeModeStatus>("/mode");
            Check.That(status is { Managed: false }, "Hors image unique, le nœud se déclare non géré.");
            using var refused = await client.PutAsJsonAsync("/mode", NodeMode.Both);
            Check.That(refused.StatusCode == HttpStatusCode.Conflict, "Hors image unique, changer le mode est refusé clairement.");
        }

        await launcher.StopAsync();
        await using (var broken = await StartHostAsync(launcherUrl))
        using (var client = new HttpClient { BaseAddress = new Uri(broken.Urls.First()) })
        {
            var status = await client.GetFromJsonAsync<NodeModeStatus>("/mode");
            Check.That(status is { Managed: false }, "Un lanceur injoignable est lu comme non géré.");
            using var failed = await client.PutAsJsonAsync("/mode", NodeMode.Both);
            Check.That(failed.StatusCode == HttpStatusCode.BadGateway, "Un lanceur injoignable est signalé par un 502.");
        }
        await launcher.DisposeAsync();
    }

    private static async Task<WebApplication> StartHostAsync(string? launcherUrl)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddNodeModeControl(launcherUrl);
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapNodeMode("/mode");
        await app.StartAsync();
        return app;
    }
}
