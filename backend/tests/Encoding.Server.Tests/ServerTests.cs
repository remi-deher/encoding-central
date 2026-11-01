using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Encoding.Server.Database;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Routing;

namespace Encoding.Server.Tests;

public sealed class ServerTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Health_is_anonymous_and_ready()
    {
        await using var factory = new ServerFactory();
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Management_routes_are_closed()
    {
        await using var factory = new ServerFactory();
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/server")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/workers")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/inconnu")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_routes_are_exactly_the_declared_set()
    {
        await using var factory = new ServerFactory();
        _ = factory.CreateClient();
        var anonymous = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var raw = endpoint.RoutePattern.RawText ?? "";
            if (raw.Contains('{', StringComparison.Ordinal))
                continue;
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            var method = methods is { Count: 1 } ? methods[0] : "?";
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
                anonymous.Add($"{method} {raw}");
        }

        Assert.Equal(
            [
                "DELETE /api/v1/session",
                "GET /api/v1/setup",
                "GET /health/live",
                "GET /health/ready",
                "POST /api/v1/session",
                "POST /api/v1/setup",
            ],
            anonymous);
    }

    [Fact]
    public async Task Setup_creates_the_only_server_and_a_session()
    {
        await using var factory = new ServerFactory();
        var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/v1/setup", new
        {
            username = "remi",
            password = "phrase-de-passe-12",
            displayName = "Maison",
            publicBaseUrl = "http://192.168.1.51:8080/",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("Maison", body.GetProperty("server").GetProperty("displayName").GetString());
        Assert.Equal("http://192.168.1.51:8080", body.GetProperty("server").GetProperty("publicBaseUrl").GetString());
        Assert.Equal("remi", body.GetProperty("session").GetProperty("username").GetString());

        var server = await client.GetFromJsonAsync<JsonElement>("/api/v1/server", Json);
        Assert.Equal("http://192.168.1.51:8080", server.GetProperty("publicBaseUrl").GetString());
        var workers = await client.GetFromJsonAsync<JsonElement>("/api/v1/workers", Json);
        Assert.Equal(JsonValueKind.Array, workers.ValueKind);
        Assert.Equal(0, workers.GetArrayLength());

        var again = await client.PostAsJsonAsync("/api/v1/setup", new
        {
            username = "autre",
            password = "phrase-de-passe-12",
            displayName = "Autre",
            publicBaseUrl = "http://192.168.1.52:8080",
        });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var tables = factory.Services.GetRequiredService<ServerDatabase>().TableNames();
        Assert.Equal(["schema_migrations", "server", "sessions", "users", "workers"], tables);
        Assert.DoesNotContain(tables, name => name.Contains("cluster", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Login_rejects_a_bad_password_without_echoing_it()
    {
        await using var factory = new ServerFactory();
        var client = factory.CreateClient();
        await Setup(client);
        var password = "secret-value-should-not-leak";
        var refused = await client.PostAsJsonAsync("/api/v1/session", new { username = "remi", password });
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        var body = await refused.Content.ReadAsStringAsync();
        Assert.DoesNotContain(password, body, StringComparison.Ordinal);

        var accepted = await client.PostAsJsonAsync("/api/v1/session", new { username = "remi", password = "phrase-de-passe-12" });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var logout = await client.DeleteAsync("/api/v1/session");
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/server")).StatusCode);
    }

    [Fact]
    public async Task Public_url_rejects_a_path_and_accepts_an_update()
    {
        await using var factory = new ServerFactory();
        var client = factory.CreateClient();
        await Setup(client);
        var rejected = await client.PutAsJsonAsync("/api/v1/server", new
        {
            displayName = "Maison",
            publicBaseUrl = "http://192.168.1.51:8080/encoding",
        });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        var updated = await client.PutAsJsonAsync("/api/v1/server", new
        {
            displayName = "Salon",
            publicBaseUrl = "https://encoding.example",
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var body = await updated.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("Salon", body.GetProperty("displayName").GetString());
        Assert.Equal("https://encoding.example", body.GetProperty("publicBaseUrl").GetString());
    }

    [Fact]
    public void Opening_the_database_twice_keeps_the_migration()
    {
        using var factory = new ServerFactory();
        _ = factory.CreateClient();
        using var second = new ServerDatabase(factory.DatabasePath);
        Assert.Equal(["schema_migrations", "server", "sessions", "users", "workers"], second.TableNames());
    }

    private static async Task Setup(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/setup", new
        {
            username = "remi",
            password = "phrase-de-passe-12",
            displayName = "Maison",
            publicBaseUrl = "http://127.0.0.1:8080",
        });
        response.EnsureSuccessStatusCode();
    }
}
