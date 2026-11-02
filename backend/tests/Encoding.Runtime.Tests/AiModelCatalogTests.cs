using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Encoding.Api.Tools;
using Encoding.Contracts.Tools;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class AiModelCatalogTests
{
    [Fact]
    public void CodexCapabilitiesArePerModelAndHiddenModelsAreExcluded()
    {
        using var doc = JsonDocument.Parse("""
            {"data":[
              {"id":"display-id","model":"actual-id","displayName":"Example","supportedReasoningEfforts":[{"reasoningEffort":"low"},{"reasoningEffort":"ultra"}],"defaultReasoningEffort":"ultra","isDefault":true},
              {"model":"hidden","hidden":true}, {"id":"other","supportedReasoningEfforts":[]}
            ]}
            """);
        var models = AiModelProtocol.ParseCodex(doc.RootElement);
        Assert.Equal(2, models.Count);
        Assert.Equal("actual-id", models[0].Id);
        Assert.Equal(new[] { "low", "ultra" }, models[0].ReasoningEfforts);
        Assert.Equal("ultra", models[0].DefaultReasoningEffort);
        Assert.True(models[0].IsDefault);
        Assert.True(models[0].ReasoningKnown);
        Assert.Empty(models[1].ReasoningEfforts);
    }

    [Fact]
    public void ClaudeAndHttpDoNotInventReasoningCapabilities()
    {
        using var doc = JsonDocument.Parse("""
            {"models":[{"value":"opus","displayName":"Opus","supportedEffortLevels":["high","max"]},
              {"value":"sonnet","displayName":"Sonnet"},{"value":"haiku","supportsEffort":false}]}
            """);
        var models = AiModelProtocol.ParseClaude(doc.RootElement);
        Assert.Equal(new[] { "high", "max" }, models[0].ReasoningEfforts);
        Assert.False(models[1].ReasoningKnown);
        Assert.Empty(models[1].ReasoningEfforts);
        Assert.True(models[2].ReasoningKnown);
        using var http = JsonDocument.Parse("""{"data":[{"id":"a"},{"id":"b"},{"id":"a"}]}""");
        Assert.Equal(2, AiModelProtocol.ParseHttp(http.RootElement).Count);
        Assert.All(AiModelProtocol.ParseHttp(http.RootElement), m => Assert.False(m.ReasoningKnown));
    }

    [Fact]
    public void ClaudeAliasDefaultIsDroppedAndResolvedModelIsShown()
    {
        using var doc = JsonDocument.Parse("""
            {"models":[{"value":"default","resolvedModel":"claude-sonnet-5-5","displayName":"Default (recommended)"},
              {"value":"opus","resolvedModel":"claude-opus-5-5","displayName":"Opus"}]}
            """);
        var models = AiModelProtocol.ParseClaude(doc.RootElement);
        var single = Assert.Single(models);
        Assert.Equal("opus", single.Id);
        Assert.Equal("Opus · claude-opus-5-5", single.Label);
    }

    [Fact]
    public void AnthropicModelsAndMessagesResponsesAreParsed()
    {
        using var list = JsonDocument.Parse("""{"data":[{"id":"claude-opus-5-5","display_name":"Claude Opus 5.5"},{"id":"claude-opus-5-5"},{"display_name":"x"}]}""");
        var models = AiModelProtocol.ParseAnthropic(list.RootElement);
        Assert.Equal("claude-opus-5-5", Assert.Single(models).Id);
        using var reply = JsonDocument.Parse("""
            {"model":"claude-opus-5-5","stop_reason":"end_turn","content":[{"type":"thinking","thinking":""},{"type":"text","text":"Bonjour"}],
             "usage":{"input_tokens":10,"cache_read_input_tokens":5,"output_tokens":3}}
            """);
        var result = AnthropicAiClient.Parse(reply.RootElement);
        Assert.True(result.Success);
        Assert.Equal("Bonjour", result.Content);
        Assert.Equal(15, result.Usage!.InputTokens);
        Assert.Equal(3, result.Usage.OutputTokens);
        using var refusal = JsonDocument.Parse("""{"stop_reason":"refusal","content":[]}""");
        Assert.False(AnthropicAiClient.Parse(refusal.RootElement).Success);
    }

    [Fact]
    public async Task CodexDiscoveryPaginatesWithoutGeneratingAndConcurrentReadsAreCached()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new AiCliTestProfile("""
            [ "$1" = "app-server" ] || exit 1
            read -r init
            printf '%s\n' '{"id":1,"result":{}}'
            read -r initialized
            read -r page
            case "$page" in *'"method":"model/list"'*) ;; *) exit 2 ;; esac
            printf 'probe\n' >> "$HOME/probes"
            cat "$HOME/reply"
            read -r page
            case "$page" in *'"cursor":"next"'*) ;; *) exit 3 ;; esac
            printf '%s\n' '{"id":3,"result":{"data":[{"model":"second"}],"nextCursor":null}}'
            sleep 30
            """);
        var profile = fixture.Runtime.Profile("codex-cli");
        await File.WriteAllTextAsync(Path.Combine(profile, "reply"), "{\"id\":2,\"result\":{\"data\":[{\"model\":\"first\"}],\"nextCursor\":\"next\"}}\n");
        var clock = new Clock();
        var service = new AiModelCatalogService(fixture.Runtime, new(fixture.Configuration), new Factory(), clock);
        var reads = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => service.ReadAsync("codex-cli", "account1", false, CancellationToken.None)));
        Assert.All(reads, snapshot => Assert.Equal("available", snapshot.State));
        Assert.Equal(2, reads[0].Models.Count);
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(profile, "probes")));
        clock.Now = clock.Now.AddMinutes(2);
        await File.WriteAllTextAsync(Path.Combine(profile, "reply"), "{\"id\":2,\"error\":{\"message\":\"private-token\"}}\n");
        var stale = await service.ReadAsync("codex-cli", "account1", false, CancellationToken.None);
        Assert.Equal("stale", stale.State);
        Assert.Equal(reads[0].ObservedAt, stale.ObservedAt);
        Assert.DoesNotContain("private-token", JsonSerializer.Serialize(stale));
        Assert.Equal("unavailable", (await service.ReadAsync("codex-cli", "account2", false, CancellationToken.None)).State);
    }

    [Fact]
    public async Task ClaudeDiscoveryUsesOnlyInitializeAndReturnsProviderEfforts()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new AiCliTestProfile("""
            read -r init
            case "$init" in *'"subtype":"initialize"'*) ;; *) exit 2 ;; esac
            printf '%s\n' '{"type":"control_response","response":{"subtype":"success","request_id":"models-init","response":{"models":[{"value":"opus","displayName":"Opus","supportedEffortLevels":["high","max"]}]}}}'
            sleep 30
            """);
        var service = new AiModelCatalogService(fixture.Runtime, new(fixture.Configuration), new Factory());
        var result = await service.ReadAsync("claude-cli", "account", false, CancellationToken.None);
        Assert.Equal("available", result.State);
        Assert.Contains("max", Assert.Single(result.Models).ReasoningEfforts);
    }

    [Fact]
    public async Task HttpDiscoveryUsesModelsEndpointAndOllamaIsNotConfiguredByDefault()
    {
        using var fixture = new AiCliTestProfile();
        var settings = new AiSettingsService(fixture.Configuration);
        Assert.Empty(settings.GetSettings().OllamaBaseUrl);
        Assert.Empty(settings.GetSettings().OllamaModel);
        settings.UpdateSettings(new(OpenAiApiKey: "private-key"));
        var factory = new Factory();
        var service = new AiModelCatalogService(fixture.Runtime, settings, factory);
        var result = await service.ReadAsync("openai", "configured", false, CancellationToken.None);
        Assert.Equal("available", result.State);
        Assert.Equal("/v1/models", factory.LastPath);
        Assert.Equal("private-key", factory.LastKey);
        Assert.DoesNotContain("private-key", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void RoutingRequiresTheSelectedConnectionModelAndEffortAndRejectsStaleCatalogs()
    {
        var id = Guid.NewGuid();
        var catalog = new AiModelCatalog("codex-cli", "available", [new("a", "A", ["high"])], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var node = new AiNodeAvailability(id, "node", ["codex-cli"], Catalogs: [catalog]);
        Assert.True(AiClusterService.Supports(node, "codex-cli", new("s", "u", Model: "a", ReasoningEffort: "high", NodeId: id)));
        Assert.False(AiClusterService.Supports(node, "codex-cli", new("s", "u", Model: "a", ReasoningEffort: "max")));
        Assert.False(AiClusterService.Supports(node, "codex-cli", new("s", "u", Model: "a", NodeId: Guid.NewGuid())));
        Assert.False(AiClusterService.Supports(node with { Catalogs = [catalog with { State = "stale" }] }, "codex-cli", new("s", "u", Model: "a")));
    }

    [Fact]
    public async Task InvalidatedInflightCatalogCannotPublishAnotherAccountsModelsAndCancellationPropagates()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new AiCliTestProfile("""
            read -r init
            printf '%s\n' '{"id":1,"result":{}}'
            read -r initialized
            read -r list
            touch "$HOME/started"
            while [ ! -f "$HOME/release" ]; do sleep 0.02; done
            printf '%s\n' '{"id":2,"result":{"data":[{"model":"old-account-model"}],"nextCursor":null}}'
            sleep 30
            """);
        var service = new AiModelCatalogService(fixture.Runtime, new(fixture.Configuration), new Factory());
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reading = service.ReadAsync("codex-cli", "account", false, watchdog.Token);
        var profile = fixture.Runtime.Profile("codex-cli");
        while (!File.Exists(Path.Combine(profile, "started"))) await Task.Delay(10, watchdog.Token);
        service.Invalidate("codex-cli");
        await File.WriteAllTextAsync(Path.Combine(profile, "release"), "go", watchdog.Token);
        var discarded = await reading;
        Assert.Equal("unavailable", discarded.State);
        Assert.Empty(discarded.Models);
        using var silent = new AiCliTestProfile("sleep 30");
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AiModelCatalogService(silent.Runtime, new(silent.Configuration), new Factory())
            .ReadAsync("codex-cli", "account", false, cancelled.Token));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Factory : IHttpClientFactory
    {
        public string? LastPath;
        public string? LastKey;
        public HttpClient CreateClient(string name) => new(new Handler(this));
        private sealed class Handler(Factory factory) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                factory.LastPath = request.RequestUri!.AbsolutePath;
                factory.LastKey = request.Headers.Authorization?.Parameter;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { data = new[] { new { id = "gpt-4o-mini" } } }) });
            }
        }
    }
}
