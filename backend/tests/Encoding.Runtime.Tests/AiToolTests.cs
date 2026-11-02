using Encoding.Api.Flows;
using Encoding.Api.Tools;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Tools;
using Encoding.NodeAgent.Runtime;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class AiToolTests
{
    [Fact]
    public void HardwareCapabilities_Protects_Accelerators()
    {
        Assert.True(HardwareCapabilities.RequiresHardwareAcceleration(["nvenc"]));
        Assert.True(HardwareCapabilities.RequiresHardwareAcceleration(["vaapi"]));
        Assert.True(HardwareCapabilities.RequiresHardwareAcceleration(["qsv"]));
        Assert.True(HardwareCapabilities.RequiresHardwareAcceleration(["cuda"]));
        Assert.False(HardwareCapabilities.RequiresHardwareAcceleration(["ai-text-generation"]));
        Assert.False(HardwareCapabilities.RequiresHardwareAcceleration(["subtitles-translation"]));
        Assert.False(HardwareCapabilities.RequiresHardwareAcceleration(null));
    }

    [Fact]
    public void ScriptTools_Includes_AiCliTools()
    {
        Assert.Contains("codex", ScriptTools.AllowedTools);
        Assert.Contains("claude", ScriptTools.AllowedTools);
    }

    [Fact]
    public async Task MissingCliNeverCreatesAnAuthorizationOrFakeCode()
    {
        using var fixture = new AiCliTestProfile();
        var service = new DeviceCodeAuthService(fixture.Runtime);
        Assert.Equal("unconfigured", (await service.GetStatusAsync("codex-cli")).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartDeviceAuthAsync("codex-cli", CancellationToken.None));
        Directory.CreateDirectory(fixture.Runtime.Profile("codex-cli"));
        File.WriteAllText(Path.Combine(fixture.Runtime.Profile("codex-cli"), "credentials.json"), "{}");
        Assert.Equal("unconfigured", (await service.GetStatusAsync("codex-cli")).State);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetStatusAsync("../other"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartDeviceAuthAsync("claude-cli", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.StartDeviceAuthAsync("unknown", CancellationToken.None));
    }

    [Fact]
    public void AnsiSanitizer_StripsCodes_And_DetectsRateLimits()
    {
        var rawWithAnsi = "\x1B[32mSuccès!\x1B[0m\r\nTexte propre.";
        var sanitized = AnsiSanitizer.Sanitize(rawWithAnsi);
        Assert.Equal("Succès!\nTexte propre.", sanitized);

        Assert.True(AnsiSanitizer.DetectRateLimit("Error: rate limit reached, please try again in 30s."));
        Assert.True(AnsiSanitizer.DetectRateLimit("HTTP 429 Too Many Requests"));
        Assert.False(AnsiSanitizer.DetectRateLimit("Traduction effectuée avec succès."));
    }

    [Fact]
    public async Task CliAiTextClientFailsWhenExecutableMissing()
    {
        using var fixture = new AiCliTestProfile();
        var result = await new CliAiTextClient("codex-cli", fixture.Runtime).GenerateTextAsync(new AiPrompt("System", "Hello"));
        Assert.False(result.Success);
        Assert.Empty(result.Content);
    }

    [Fact]
    public async Task FlowGenerationDoesNotReportSyntheticSuccessWhenCliIsMissing()
    {
        using var fixture = new AiCliTestProfile();
        var registry = StoreCatalogFixture.Create(fixture.Configuration);
        var validator = new FlowDefinitionValidator(registry);
        var factory = new AiClientFactory(fixture.Configuration, new MockHttpClientFactory(), cliRuntime: fixture.Runtime);
        var service = new FlowAiGeneratorService(factory, new FlowCatalogPromptBuilder(registry), validator);
        var response = await service.GenerateFlowAsync(new FlowAiGenerateRequest("video", "Encoder en HEVC"));
        Assert.False(response.Success);
        Assert.Null(response.Flow);
    }

    [Fact]
    public void SubtitleChunker_Parses_Chunks_And_Reassembles()
    {
        var srt = """
            1
            00:00:01,000 --> 00:00:03,500
            Hello world!

            2
            00:00:04,000 --> 00:00:06,000
            How are you?

            3
            00:00:07,000 --> 00:00:09,000
            I am fine, thank you.
            """;

        var entries = Encoding.Api.Flows.Subtitles.SubtitleChunker.ParseSrt(srt);
        Assert.Equal(3, entries.Count);
        Assert.Equal("Hello world!", entries[0].Text);
        Assert.Equal("00:00:01,000 --> 00:00:03,500", entries[0].Timecode);

        var chunks = Encoding.Api.Flows.Subtitles.SubtitleChunker.ChunkEntries(entries, 2);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(2, chunks[0].Count);
        Assert.Single(chunks[1]);

        var promptChunk = Encoding.Api.Flows.Subtitles.SubtitleChunker.FormatChunkForPrompt(chunks[0]);
        Assert.Contains("Hello world!", promptChunk);
        Assert.Contains("00:00:01,000 --> 00:00:03,500", promptChunk);

        var translated = Encoding.Api.Flows.Subtitles.SubtitleChunker.ReassembleSrt([
            "1\n00:00:01,000 --> 00:00:03,500\nBonjour le monde !\n\n2\n00:00:04,000 --> 00:00:06,000\nComment allez-vous ?",
            "3\n00:00:07,000 --> 00:00:09,000\nJe vais bien, merci."
        ]);

        Assert.Contains("Bonjour le monde !", translated);
        Assert.Contains("Comment allez-vous ?", translated);
        Assert.Contains("Je vais bien, merci.", translated);
    }

    [Fact]
    public async Task RuntimeSubtitleTranslation_UsesBoundedBatches_AndKeepsCueTiming()
    {
        var directory = Path.Combine(Path.GetTempPath(), "subtitle-translation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sourcePath = Path.Combine(directory, "source.srt");
            var source = string.Join("\n\n", Enumerable.Range(1, 21).Select(index =>
                $"{index}\n00:00:{index:00},000 --> 00:00:{index:00},900\nCue {index}"));
            await File.WriteAllTextAsync(sourcePath, source);
            var configuration = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["batchSize"] = System.Text.Json.JsonSerializer.SerializeToElement(20),
            };
            var batchIndexes = new List<int>();
            var outcome = await Encoding.NodeAgent.Runtime.Media.SubtitleTranslation.TranslateAsync(
                configuration, sourcePath, directory, "translate-node",
                (batchIndex, items) =>
                {
                    batchIndexes.Add(batchIndex);
                    return Task.FromResult(new RuntimeAiSubtitleTranslationResponse(true,
                        items.Select(item => item with { Text = "FR " + item.Text }).ToArray()));
                }, CancellationToken.None);

            Assert.Equal(new[] { 0, 1 }, batchIndexes);
            Assert.NotNull(outcome.File);
            var translated = (await File.ReadAllTextAsync(outcome.File!)).Replace("\r\n", "\n");
            Assert.Contains("1\n00:00:01,000 --> 00:00:01,900\nFR Cue 1", translated);
            Assert.Contains("21\n00:00:21,000 --> 00:00:21,900\nFR Cue 21", translated);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void FlowComponentRegistry_AdvertisesOnlyAiComponentsWithRuntimeHandlers()
    {
        var registry = StoreCatalogFixture.Create();
        Assert.NotNull(registry.FindReference("ai-translate-subtitles"));
        Assert.NotNull(registry.FindReference("ai-metadata-enricher"));
        Assert.Null(registry.FindReference("base-ai-translate-subtitles"));
        Assert.Null(registry.FindReference("base-ai-metadata-enricher"));
        var components = registry.Plugins.Single(plugin => plugin.Id == "ai").Components;
        var translation = Assert.Single(components, component => component.Id == "ai-translate-subtitles");
        Assert.Equal("video", translation.MediaType);
        Assert.Contains("video", translation.SupportedFlowMediaTypes);
        Assert.Contains("subtitles", translation.SupportedFlowMediaTypes);
        var metadata = Assert.Single(components, component => component.Id == "ai-metadata-enricher");
        Assert.Contains("video", metadata.SupportedFlowMediaTypes);
        Assert.Contains("music", metadata.SupportedFlowMediaTypes);
    }

    [Fact]
    public void FlowComponentRegistry_Loads_Modular_Plugins()
    {
        var registry = StoreCatalogFixture.Create();

        // Plugins exist
        var pluginIds = registry.Plugins.Select(p => p.Id).ToHashSet();
        Assert.Contains("base", pluginIds);
        Assert.Contains("ai", pluginIds);
        Assert.Contains("mkv", pluginIds);
        Assert.Contains("images", pluginIds);
        Assert.Contains("probe", pluginIds);

        // MKV
        var remux = registry.FindReference("mkv-remux-matroska");
        Assert.NotNull(remux);
        Assert.Equal("mkv", remux.PluginId);
        var legacyRemux = registry.FindReference("base-remux-matroska");
        Assert.NotNull(legacyRemux);
        Assert.Equal("mkv", legacyRemux.PluginId);

        // Images
        var convert = registry.FindReference("image-convert");
        Assert.NotNull(convert);
        Assert.Equal("images", convert.PluginId);
        var legacyConvert = registry.FindReference("base-convert-image");
        Assert.NotNull(legacyConvert);
        Assert.Equal("images", legacyConvert.PluginId);

        // Probe
        var probe = registry.FindReference("probe-media");
        Assert.NotNull(probe);
        Assert.Equal("probe", probe.PluginId);
        var legacyProbe = registry.FindReference("base-probe-media");
        Assert.NotNull(legacyProbe);
        Assert.Equal("probe", legacyProbe.PluginId);
    }

    [Fact]
    public void AiSettingsService_Lifecycle()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "enc_ai_settings_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiTools:CredentialsDirectory"] = tempDir,
                    ["AiTools:DefaultProvider"] = "codex-cli"
                })
                .Build();

            var service = new AiSettingsService(config);
            var initial = service.GetSettings();
            Assert.Equal("codex-cli", initial.DefaultProvider);

            var updated = service.UpdateSettings(new AiSettingsUpdateRequest(
                DefaultProvider: "ollama",
                OllamaBaseUrl: "http://192.168.1.100:11434/v1/",
                OllamaModel: "qwen2.5-coder:7b",
                TimeoutSeconds: 60,
                AssistantProvider: "ollama",
                FlowProvider: "openai",
                AssistantLocalOnly: true
            ));

            Assert.Equal("ollama", updated.DefaultProvider);
            Assert.Equal("http://192.168.1.100:11434/v1/", updated.OllamaBaseUrl);
            Assert.Equal("qwen2.5-coder:7b", updated.OllamaModel);
            Assert.Equal(60, updated.TimeoutSeconds);
            Assert.Equal("ollama", updated.AssistantProvider);
            Assert.Equal("openai", updated.FlowProvider);
            Assert.True(updated.AssistantLocalOnly);

            // Recharger une nouvelle instance depuis le même dossier pour vérifier la persistance
            var reloadedService = new AiSettingsService(config);
            var reloaded = reloadedService.GetSettings();
            Assert.Equal("ollama", reloaded.DefaultProvider);
            Assert.Equal("http://192.168.1.100:11434/v1/", reloaded.OllamaBaseUrl);
            Assert.Equal("qwen2.5-coder:7b", reloaded.OllamaModel);
            Assert.Equal(60, reloaded.TimeoutSeconds);
            Assert.Equal("ollama", reloaded.AssistantProvider);
            Assert.Equal("openai", reloaded.FlowProvider);
            Assert.True(reloaded.AssistantLocalOnly);

            var inherited = reloadedService.UpdateSettings(new(AssistantProvider: "default", AssistantLocalOnly: false));
            Assert.Null(inherited.AssistantProvider);
            Assert.False(inherited.AssistantLocalOnly);

            var strictPolicy = reloadedService.UpdateSettings(new(AssistantLocalOnly: true));
            Assert.Throws<ArgumentException>(() => reloadedService.UpdateSettings(new(DefaultProvider: "openai")));
            Assert.True(strictPolicy.AssistantLocalOnly);

            // Créer un client depuis la factory
            var factory = new AiClientFactory(config, new MockHttpClientFactory(), reloadedService);
            var client = factory.CreateClient();
            Assert.NotNull(client);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ApiKeyPersistsProtectedAndInvalidSettingsDoNotOverwriteSavedState()
    {
        using var profile = new AiCliTestProfile();
        var service = new AiSettingsService(profile.Configuration);
        service.UpdateSettings(new(OpenAiApiKey: "test-private-key"));
        var saved = File.ReadAllText(Path.Combine(AiCliRuntime.ResolveRoot(profile.Configuration), "settings.json"));
        Assert.DoesNotContain("test-private-key", saved);
        Assert.Equal("test-private-key", new AiSettingsService(profile.Configuration).GetSettings().OpenAiApiKey);
        Assert.Throws<ArgumentException>(() => service.UpdateSettings(new(TimeoutSeconds: 0)));
        Assert.Equal(120, service.GetSettings().TimeoutSeconds);
        Assert.Throws<ArgumentException>(() => service.UpdateSettings(new(DefaultProvider: "unknown")));
            Assert.Throws<ArgumentException>(() => service.UpdateSettings(new(AssistantProvider: "openai", AssistantLocalOnly: true)));
    }

    private sealed class MockHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
