using Encoding.Contracts.Tools;
using Microsoft.Extensions.Logging;

namespace Encoding.Api.Tools;

public sealed class AiClientFactory(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    AiSettingsService? settingsService = null,
    AiCliRuntime? cliRuntime = null,
    IServiceProvider? services = null,
    ILoggerFactory? loggerFactory = null)
{
    private readonly AiSettingsService settingsService = settingsService ?? new AiSettingsService(configuration);

    public IAiTextClient CreateClient(string? preferredProvider = null, bool localOnly = false)
    {
        if (services is not null) return new ClusterClient(services.GetRequiredService<AiClusterService>(), preferredProvider, localOnly);
        return CreateLocalClient(preferredProvider);
    }

    public IAiTextClient CreateLocalClient(string? preferredProvider = null)
    {
        var settings = settingsService.GetSettings();
        var provider = preferredProvider
            ?? settings.DefaultProvider
            ?? "codex-cli";


        if (provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            var url = !string.IsNullOrWhiteSpace(settings.OllamaBaseUrl) ? settings.OllamaBaseUrl : "http://127.0.0.1:11434/v1/";
            if (!new Uri(url).AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.Ordinal)) url = url.TrimEnd('/') + "/v1/";
            var model = !string.IsNullOrWhiteSpace(settings.OllamaModel) ? settings.OllamaModel : "llama3.2";
            var client = httpClientFactory.CreateClient("AiClient");
            client.BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/");
            return new OpenAiCompatibleAiClient(client, model);
        }

        if (provider.Equals("openai", StringComparison.OrdinalIgnoreCase))
        {
            var url = !string.IsNullOrWhiteSpace(settings.OpenAiBaseUrl) ? settings.OpenAiBaseUrl : "https://api.openai.com/v1/";
            var model = !string.IsNullOrWhiteSpace(settings.OpenAiModel) ? settings.OpenAiModel : "gpt-4o-mini";
            var key = settings.OpenAiApiKey ?? configuration["AiTools:OpenAi:ApiKey"];
            var client = httpClientFactory.CreateClient("AiClient");
            client.BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/");
            return new OpenAiCompatibleAiClient(client, model, key, supportsReasoningEffort: true);
        }

        if (provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            var model = !string.IsNullOrWhiteSpace(settings.AnthropicModel) ? settings.AnthropicModel : "claude-sonnet-5-5";
            var key = settings.AnthropicApiKey ?? configuration["AiTools:Anthropic:ApiKey"];
            var client = httpClientFactory.CreateClient("AiClient");
            client.BaseAddress = new Uri("https://api.anthropic.com/v1/");
            return new AnthropicAiClient(client, model, key);
        }

        // Par défaut : CLI officielle (Codex ou Claude)
        var toolId = provider.Equals("claude-cli", StringComparison.OrdinalIgnoreCase) || provider.Equals("claude", StringComparison.OrdinalIgnoreCase)
            ? "claude-cli"
            : "codex-cli";

        if (provider is not ("codex-cli" or "claude-cli" or "claude")) throw new ArgumentException("Fournisseur IA inconnu.");
        return new CliAiTextClient(toolId, cliRuntime ?? new AiCliRuntime(configuration,
            new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()), settings.TimeoutSeconds,
            loggerFactory?.CreateLogger<CliAiTextClient>());
    }

    private sealed class ClusterClient(AiClusterService cluster, string? provider, bool localOnly) : IAiTextClient
    {
        public Task<AiCompletionResult> GenerateTextAsync(AiPrompt prompt, CancellationToken ct = default) => cluster.GenerateAsync(provider, prompt, ct, localOnly);
    }
}
