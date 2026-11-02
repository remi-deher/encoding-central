using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

public sealed class AiSettingsService
{
    private readonly string settingsFilePath;
    private readonly object syncRoot = new();
    private AiSettingsDto cachedSettings;
    private readonly IDataProtector protector;

    public AiSettingsService(IConfiguration configuration, IDataProtectionProvider? protection = null)
    {
        var credDir = AiCliRuntime.ResolveRoot(configuration);

        protector = (protection ?? DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(credDir, ".keys"))))
            .CreateProtector("Encoding.Ai.ApiKey.v1");
        settingsFilePath = Path.Combine(credDir, "settings.json");
        cachedSettings = LoadSettings(configuration);
    }

    public AiSettingsDto GetSettings()
    {
        lock (syncRoot)
        {
            return cachedSettings;
        }
    }

    public void ValidateModelSelections(AiSettingsDto current, AiSettingsUpdateRequest update, AiClusterModelCatalog catalog)
    {
        var defaultProvider = update.DefaultProvider ?? current.DefaultProvider;
        var assistantProvider = update.AssistantProvider is null
            ? current.AssistantProvider
            : update.AssistantProvider == "default" ? null : update.AssistantProvider;
        var flowProvider = update.FlowProvider is null
            ? current.FlowProvider
            : update.FlowProvider == "default" ? null : update.FlowProvider;
        var assistantLocalOnly = update.AssistantLocalOnly ?? current.AssistantLocalOnly;
        var flowLocalOnly = update.FlowLocalOnly ?? current.FlowLocalOnly;

        ValidateSelection("moteur par défaut",
            (current.DefaultProvider, current.DefaultModel, current.DefaultReasoningEffort, false),
            (defaultProvider, UpdateOptional(update.DefaultModel, current.DefaultModel), UpdateOptional(update.DefaultReasoningEffort, current.DefaultReasoningEffort), false), catalog);
        ValidateSelection("assistant",
            (current.AssistantProvider ?? current.DefaultProvider, current.AssistantModel, current.AssistantReasoningEffort, current.AssistantLocalOnly),
            (assistantProvider ?? defaultProvider, UpdateOptional(update.AssistantModel, current.AssistantModel),
                UpdateOptional(update.AssistantReasoningEffort, current.AssistantReasoningEffort), assistantLocalOnly), catalog);
        ValidateSelection("flows",
            (current.FlowProvider ?? current.DefaultProvider, current.FlowModel, current.FlowReasoningEffort, current.FlowLocalOnly),
            (flowProvider ?? defaultProvider, UpdateOptional(update.FlowModel, current.FlowModel), UpdateOptional(update.FlowReasoningEffort, current.FlowReasoningEffort), flowLocalOnly), catalog);
    }

    private static void ValidateSelection(
        string usage,
        (string Provider, string? Model, string? Effort, bool LocalOnly) current,
        (string Provider, string? Model, string? Effort, bool LocalOnly) proposed,
        AiClusterModelCatalog catalog)
    {
        if (current == proposed || proposed.Model is null && proposed.Effort is null && proposed.Provider == current.Provider)
            return;

        var nodes = proposed.LocalOnly ? catalog.Nodes.Take(1) : catalog.Nodes;
        var discovered = nodes.SelectMany(node => node.Providers)
            .Where(provider => provider.Provider == proposed.Provider && provider.State == "available")
            .SelectMany(provider => provider.Models)
            .Where(model => !catalog.Policy.DisabledModels.Any(disabled => disabled.Provider == proposed.Provider && disabled.Model == model.Id))
            .ToArray();
        AiDiscoveredModel[] matching;
        if (proposed.Model is null)
        {
            var defaultModel = discovered.FirstOrDefault(model => model.IsDefault) ?? discovered.FirstOrDefault();
            matching = defaultModel is null ? [] : [defaultModel];
        }
        else
        {
            matching = discovered.Where(model => model.Id == proposed.Model).ToArray();
        }
        if (matching.Length == 0)
            throw new ArgumentException($"Le fournisseur ou le modèle sélectionné pour {usage} n’est pas annoncé comme disponible par le backend.");
        if (proposed.Effort is not null && !matching.Any(model => model.ReasoningKnown && model.ReasoningEfforts.Contains(proposed.Effort, StringComparer.Ordinal)))
            throw new ArgumentException($"L’effort de raisonnement sélectionné pour {usage} n’est pas annoncé comme disponible pour ce modèle.");
    }

    private static string? UpdateOptional(string? value, string? current) =>
        value is null ? current : string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public AiSettingsDto UpdateSettings(AiSettingsUpdateRequest update)
    {
        if (update.DefaultProvider is not null && update.DefaultProvider is not ("ollama" or "openai" or "anthropic" or "codex-cli" or "claude-cli"))
            throw new ArgumentException("Fournisseur IA inconnu.");
        foreach (var provider in new[] { update.AssistantProvider, update.FlowProvider }.OfType<string>())
            if (provider is not ("default" or "ollama" or "openai" or "anthropic" or "codex-cli" or "claude-cli"))
                throw new ArgumentException("Fournisseur IA inconnu.");
        var effectiveAssistantProvider = update.AssistantProvider == "default" ? update.DefaultProvider ?? cachedSettings.DefaultProvider
            : update.AssistantProvider ?? cachedSettings.AssistantProvider ?? update.DefaultProvider ?? cachedSettings.DefaultProvider;
        var effectiveFlowProvider = update.FlowProvider == "default" ? update.DefaultProvider ?? cachedSettings.DefaultProvider
            : update.FlowProvider ?? cachedSettings.FlowProvider ?? update.DefaultProvider ?? cachedSettings.DefaultProvider;
        var assistantLocalOnly = update.AssistantLocalOnly ?? cachedSettings.AssistantLocalOnly;
        var flowLocalOnly = update.FlowLocalOnly ?? cachedSettings.FlowLocalOnly;
        if (assistantLocalOnly && effectiveAssistantProvider != "ollama"
            || flowLocalOnly && effectiveFlowProvider != "ollama")
            throw new ArgumentException("Le mode local uniquement exige Ollama comme fournisseur effectif.");
        if (update.TimeoutSeconds is < 10 or > 600 || update.SubtitleBatchSize is < 5 or > 200)
            throw new ArgumentException("Les limites IA sont invalides.");
        if (update.AssistantConversationRetentionDays is < 1 or > 3650 || update.AssistantActivityRetentionDays is < 1 or > 3650)
            throw new ArgumentException("Les délais de rétention IA doivent être compris entre 1 et 3 650 jours.");
        foreach (var url in new[] { update.OllamaBaseUrl, update.OpenAiBaseUrl }
                     .OfType<string>()
                     .Where(url => !string.IsNullOrWhiteSpace(url)))
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("L’adresse du fournisseur doit être une URL HTTP(S) sans identifiants.");
        lock (syncRoot)
        {
            var ollamaEnabled = update.OllamaEnabled ?? cachedSettings.OllamaEnabled;
            var openAiEnabled = update.OpenAiEnabled ?? cachedSettings.OpenAiEnabled;
            var anthropicEnabled = update.AnthropicEnabled ?? cachedSettings.AnthropicEnabled;
            var nextDefault = !string.IsNullOrWhiteSpace(update.DefaultProvider) ? update.DefaultProvider : cachedSettings.DefaultProvider;
            if (nextDefault == "ollama" && !ollamaEnabled || nextDefault == "openai" && !openAiEnabled || nextDefault == "anthropic" && !anthropicEnabled)
                throw new ArgumentException("Le moteur par défaut doit être activé et configuré.");
            var next = new AiSettingsDto(
                DefaultProvider: nextDefault,
                OllamaBaseUrl: update.OllamaEnabled == false ? string.Empty : !string.IsNullOrWhiteSpace(update.OllamaBaseUrl) ? update.OllamaBaseUrl : cachedSettings.OllamaBaseUrl,
                OllamaModel: update.OllamaEnabled == false ? string.Empty : !string.IsNullOrWhiteSpace(update.OllamaModel) ? update.OllamaModel : cachedSettings.OllamaModel,
                OpenAiBaseUrl: update.OpenAiEnabled == false ? string.Empty : !string.IsNullOrWhiteSpace(update.OpenAiBaseUrl) ? update.OpenAiBaseUrl : cachedSettings.OpenAiBaseUrl,
                OpenAiModel: update.OpenAiEnabled == false ? string.Empty : !string.IsNullOrWhiteSpace(update.OpenAiModel) ? update.OpenAiModel : cachedSettings.OpenAiModel,
                OpenAiApiKey: update.OpenAiEnabled == false ? null : update.OpenAiApiKey ?? cachedSettings.OpenAiApiKey,
                TimeoutSeconds: update.TimeoutSeconds is > 0 ? update.TimeoutSeconds.Value : cachedSettings.TimeoutSeconds,
                SubtitleBatchSize: update.SubtitleBatchSize is > 0 ? update.SubtitleBatchSize.Value : cachedSettings.SubtitleBatchSize,
                AssistantProvider: update.AssistantProvider is null ? cachedSettings.AssistantProvider : update.AssistantProvider == "default" ? null : update.AssistantProvider,
                FlowProvider: update.FlowProvider is null ? cachedSettings.FlowProvider : update.FlowProvider == "default" ? null : update.FlowProvider,
                AssistantLocalOnly: update.AssistantLocalOnly ?? cachedSettings.AssistantLocalOnly,
                FlowLocalOnly: update.FlowLocalOnly ?? cachedSettings.FlowLocalOnly,
                AssistantConversationRetentionDays: update.AssistantConversationRetentionDays ?? cachedSettings.AssistantConversationRetentionDays,
                AssistantActivityRetentionDays: update.AssistantActivityRetentionDays ?? cachedSettings.AssistantActivityRetentionDays,
                OllamaEnabled: ollamaEnabled,
                OpenAiEnabled: openAiEnabled,
                CodexEnabled: update.CodexEnabled ?? cachedSettings.CodexEnabled,
                ClaudeEnabled: update.ClaudeEnabled ?? cachedSettings.ClaudeEnabled,
                DefaultModel: UpdateOptional(update.DefaultModel, cachedSettings.DefaultModel),
                DefaultReasoningEffort: UpdateOptional(update.DefaultReasoningEffort, cachedSettings.DefaultReasoningEffort),
                AssistantModel: UpdateOptional(update.AssistantModel, cachedSettings.AssistantModel),
                AssistantReasoningEffort: UpdateOptional(update.AssistantReasoningEffort, cachedSettings.AssistantReasoningEffort),
                FlowModel: UpdateOptional(update.FlowModel, cachedSettings.FlowModel),
                FlowReasoningEffort: UpdateOptional(update.FlowReasoningEffort, cachedSettings.FlowReasoningEffort),
                AnthropicApiKey: update.AnthropicEnabled == false ? null : update.AnthropicApiKey ?? cachedSettings.AnthropicApiKey,
                AnthropicModel: update.AnthropicEnabled == false ? string.Empty : !string.IsNullOrWhiteSpace(update.AnthropicModel) ? update.AnthropicModel.Trim() : cachedSettings.AnthropicModel,
                AnthropicEnabled: anthropicEnabled
            );

            SaveSettings(next);
            cachedSettings = next;
            return cachedSettings;
        }
    }

    /// <summary>Remplace uniquement la clé OpenAI (réplication entre nœuds) ; les autres réglages restent propres au nœud.</summary>
    public void SetOpenAiApiKey(string? key)
    {
        lock (syncRoot)
        {
            var next = cachedSettings with { OpenAiApiKey = string.IsNullOrWhiteSpace(key) ? null : key };
            SaveSettings(next);
            cachedSettings = next;
        }
    }

    /// <summary>Remplace uniquement la clé Anthropic (réplication entre nœuds).</summary>
    public void SetAnthropicApiKey(string? key)
    {
        lock (syncRoot)
        {
            var next = cachedSettings with { AnthropicApiKey = string.IsNullOrWhiteSpace(key) ? null : key };
            SaveSettings(next);
            cachedSettings = next;
        }
    }

    private static string? Unprotect(IDataProtector protector, string? value) =>
        value is not null && value.StartsWith("protected:", StringComparison.Ordinal) ? protector.Unprotect(value[10..]) : value;

    private AiSettingsDto LoadSettings(IConfiguration configuration)
    {
        try
        {
            if (File.Exists(settingsFilePath))
            {
                var json = File.ReadAllText(settingsFilePath);
                var loaded = JsonSerializer.Deserialize<AiSettingsDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (loaded is not null) return loaded with { OpenAiApiKey = Unprotect(protector, loaded.OpenAiApiKey), AnthropicApiKey = Unprotect(protector, loaded.AnthropicApiKey) };
            }
        }
        catch
        {
            // Repli sur la configuration par défaut
        }

        return new AiSettingsDto(
            DefaultProvider: configuration["AiTools:DefaultProvider"] ?? "codex-cli",
            OllamaBaseUrl: configuration["AiTools:Ollama:BaseUrl"] ?? "",
            OllamaModel: configuration["AiTools:Ollama:Model"] ?? "",
            OpenAiBaseUrl: configuration["AiTools:OpenAi:BaseUrl"] ?? "https://api.openai.com/v1/",
            OpenAiModel: configuration["AiTools:OpenAi:Model"] ?? "gpt-4o-mini",
            OpenAiApiKey: configuration["AiTools:OpenAi:ApiKey"],
            TimeoutSeconds: int.TryParse(configuration["AiTools:TimeoutSeconds"], out var t) && t > 0 ? t : 120,
            SubtitleBatchSize: int.TryParse(configuration["AiTools:SubtitleBatchSize"], out var b) && b > 0 ? b : 80,
            AssistantConversationRetentionDays: int.TryParse(configuration["AiTools:AssistantConversationRetentionDays"], out var c) && c > 0 ? c : 90,
            AssistantActivityRetentionDays: int.TryParse(configuration["AiTools:AssistantActivityRetentionDays"], out var a) && a > 0 ? a : 30,
            AnthropicApiKey: configuration["AiTools:Anthropic:ApiKey"]
        );
    }

    private string? Protect(string? secret) => secret is { Length: > 0 } ? "protected:" + protector.Protect(secret) : null;

    private void SaveSettings(AiSettingsDto settings)
    {
        var dir = Path.GetDirectoryName(settingsFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var json = JsonSerializer.Serialize(settings with { OpenAiApiKey = Protect(settings.OpenAiApiKey),
            AnthropicApiKey = Protect(settings.AnthropicApiKey) }, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        });

        var tmp = settingsFilePath + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(tmp, options))
        using (var writer = new StreamWriter(stream)) writer.Write(json);
        File.Move(tmp, settingsFilePath, overwrite: true);
    }
}
