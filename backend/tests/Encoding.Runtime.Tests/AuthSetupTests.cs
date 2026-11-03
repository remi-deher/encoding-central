using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Encoding.Api.Auth;
using Encoding.Contracts.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using ApiAssembly = Encoding.Api.Auth.AuthOptions;

namespace Encoding.Runtime.Tests;

/// <summary>Initialisation du premier administrateur : jeton d'initialisation.</summary>
public sealed class AuthSetupTests
{
    private const string Token = "jeton-d-initialisation-de-test";
    private const string Password = "correct horse battery staple";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    private static WebApplicationFactory<ApiAssembly> Factory(TestDatabase database, string directory, string environment, Dictionary<string, string> settings) =>
        new WebApplicationFactory<ApiAssembly>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting(WebHostDefaults.ContentRootKey, AppContext.BaseDirectory);
            foreach (var setting in database.Settings()) builder.UseSetting(setting.Key, setting.Value);
            builder.UseSetting("Logging:LogLevel:Default", "Error");
            builder.UseSetting("Worker:DataDirectory", directory);
            builder.UseSetting("PluginSettings:KeyDirectory", Path.Combine(directory, "keys"));
            builder.UseSetting("AiTools:CredentialsDirectory", Path.Combine(directory, "ai-tools"));
            builder.UseSetting("Auth:AnonymousRequestsPerMinute", "1000");
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
            builder.ConfigureServices(services =>
            {
                foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService)).ToArray()) services.Remove(descriptor);
            });
        });

    private static async Task WithFactoryAsync(string environment, Dictionary<string, string> settings, Func<WebApplicationFactory<ApiAssembly>, Task> body)
    {
        await using var database = await TestDatabase.CreateSqliteAsync("auth-setup");
        var directory = Path.Combine(Path.GetTempPath(), "auth-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var factory = Factory(database, directory, environment, settings);
            await body(factory);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static Task<HttpResponseMessage> PostSetup(HttpClient client, SetupRequest request) =>
        client.PostAsJsonAsync("/api/v1/auth/setup", request);

    [Fact(Timeout = 60000)]
    public Task SetupTokenIsRequiredWhenConfigured() => WithFactoryAsync("Development", new() { ["Auth:SetupToken"] = Token }, async factory =>
    {
        using var client = factory.CreateClient();
        var refused = new List<(HttpStatusCode Status, string Body)>();
        foreach (var attempt in new SetupRequest?[]
        {
            new("root", Password),
            new("root", Password, SetupToken: "mauvais-jeton"),
            new("root", Password, SetupToken: Token + "x"),
            new("root", "court", SetupToken: "mauvais-jeton"), // le jeton est examiné avant la validation : pas de détail sur les champs
        })
        {
            using var response = await PostSetup(client, attempt!);
            refused.Add((response.StatusCode, await response.Content.ReadAsStringAsync()));
        }
        Assert.All(refused, item => Assert.Equal(HttpStatusCode.Forbidden, item.Status));
        Assert.Single(refused.Select(item => item.Body).Distinct());
        Assert.DoesNotContain(Token, refused[0].Body);
        Assert.DoesNotContain("errors", refused[0].Body);

        var status = await client.GetFromJsonAsync<AuthStatusResponse>("/api/v1/auth/status", Json);
        Assert.True(status!.SetupRequired);

        using var accepted = await PostSetup(client, new SetupRequest("root", Password, SetupToken: Token));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.DoesNotContain(Token, await accepted.Content.ReadAsStringAsync());
    });

    [Fact(Timeout = 60000)]
    public Task SetupTokenFromAuthSetupTokenVariableIsEnforced() => WithFactoryAsync("Development", new() { ["AUTH_SETUP_TOKEN"] = Token }, async factory =>
    {
        using var client = factory.CreateClient();
        using var refused = await PostSetup(client, new SetupRequest("root", Password));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var accepted = await PostSetup(client, new SetupRequest("root", Password, SetupToken: Token));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    });

    [Fact(Timeout = 60000)]
    public Task SetupWithoutConfiguredTokenDoesNotRequireOne() => WithFactoryAsync("Development", new(), async factory =>
    {
        using var client = factory.CreateClient();
        using var accepted = await PostSetup(client, new SetupRequest("root", Password));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    });

    [Fact]
    public void BlankSetupTokenMeansNoToken()
    {
        Assert.Null(Options(new() { ["Auth:SetupToken"] = "   " }).SetupToken);
        Assert.Equal(Token, Options(new() { ["Auth:SetupToken"] = $" {Token} " }).SetupToken);
    }

    private static AuthOptions Options(Dictionary<string, string?> values) =>
        AuthOptions.From(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
}
