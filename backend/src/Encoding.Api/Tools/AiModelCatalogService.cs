using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

/// <summary>Bounded, connection-local catalogue. Credentials and raw provider errors never leave the node.</summary>
public sealed class AiModelCatalogService(AiCliRuntime runtime, AiSettingsService settings, IHttpClientFactory clients,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1);
        public readonly object Sync = new();
        public AiModelCatalog? Snapshot;
        public string? Fingerprint;
        public bool RefreshQueued;
    }
    private readonly ConcurrentDictionary<string, Entry> entries = new();
    public void Invalidate(string provider) => entries.TryRemove(provider, out _);

    /// <summary>Returns the last observation immediately and schedules a bounded refresh when it is missing or old.</summary>
    public AiModelCatalog ReadCachedOrRefresh(string provider, string connectionRevision)
    {
        var entry = entries.GetOrAdd(provider, _ => new());
        var fingerprint = GetFingerprint(provider, connectionRevision);
        AiModelCatalog? snapshot;
        var scheduleRefresh = false;
        lock (entry.Sync)
        {
            if (entry.Fingerprint != fingerprint)
            {
                entry.Snapshot = null;
                entry.Fingerprint = fingerprint;
                entry.RefreshQueued = false;
            }
            snapshot = entry.Snapshot;
            if (!entry.RefreshQueued && (snapshot is null || snapshot.CheckedAt.AddMinutes(5) <= clock.GetUtcNow()))
            {
                entry.RefreshQueued = true;
                scheduleRefresh = true;
            }
        }
        if (scheduleRefresh) _ = RefreshInBackground(provider, connectionRevision, entry);
        return snapshot ?? new(provider, "loading", [], null, clock.GetUtcNow(), "Chargement du catalogue observé par le fournisseur.");
    }

    public async Task<AiModelCatalog> ReadAsync(string provider, string connectionRevision, bool refresh, CancellationToken ct)
    {
        var entry = entries.GetOrAdd(provider, _ => new());
        var fingerprint = GetFingerprint(provider, connectionRevision);
        lock (entry.Sync)
        {
            if (entry.Fingerprint != fingerprint)
            {
                entry.Snapshot = null;
                entry.Fingerprint = fingerprint;
                entry.RefreshQueued = false;
            }
        }
        await entry.Gate.WaitAsync(ct);
        try
        {
            var local = settings.GetSettings();
            AiModelCatalog? previous;
            lock (entry.Sync)
            {
                if (entry.Fingerprint != fingerprint)
                    return new(provider, "unavailable", [], null, clock.GetUtcNow(), "Connexion modifiée pendant l’observation du catalogue.");
                previous = entry.Snapshot;
            }
            if (!refresh && previous is not null && previous.CheckedAt.AddMinutes(1) > clock.GetUtcNow()) return previous;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                IReadOnlyList<AiDiscoveredModel> models;
                if (provider is "codex-cli" or "claude-cli")
                {
                    string[] args = provider == "codex-cli" ? ["app-server"]
                        : ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--no-session-persistence",
                            "--tools", "", "--strict-mcp-config", "--setting-sources", "", "--disable-slash-commands"];
                    models = await runtime.RunProtocolAsync<IReadOnlyList<AiDiscoveredModel>>(provider, args,
                        provider == "codex-cli" ? AiModelProtocol.ReadCodexAsync : AiModelProtocol.ReadClaudeAsync, timeout.Token);
                }
                else
                {
                    if (provider == "anthropic")
                    {
                        if (string.IsNullOrWhiteSpace(local.AnthropicApiKey)) throw new InvalidOperationException();
                        using var anthropicRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models?limit=100");
                        anthropicRequest.Headers.Add("x-api-key", local.AnthropicApiKey);
                        anthropicRequest.Headers.Add("anthropic-version", AnthropicAiClient.ApiVersion);
                        using var anthropicResponse = await clients.CreateClient("AiClient").SendAsync(anthropicRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                        anthropicResponse.EnsureSuccessStatusCode();
                        await using var anthropicStream = await anthropicResponse.Content.ReadAsStreamAsync(timeout.Token);
                        using var anthropicDoc = JsonDocument.Parse(await AiClusterTransport.ReadAsync(anthropicStream, timeout.Token));
                        models = AiModelProtocol.ParseAnthropic(anthropicDoc.RootElement).Select(m => m with { IsDefault = m.Id == local.AnthropicModel }).ToArray();
                        var checkedAt = clock.GetUtcNow();
                        return Publish(provider, entry, fingerprint, new(provider, models.Count > 0 ? "available" : "unavailable", models, checkedAt, checkedAt,
                            models.Count == 0 ? "Le fournisseur ne publie aucun modèle utilisable." : null));
                    }
                    var url = provider == "ollama" ? local.OllamaBaseUrl : local.OpenAiBaseUrl;
                    if (provider is not ("ollama" or "openai" or "anthropic") || string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException();
                    if (provider == "ollama" && !new Uri(url).AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.Ordinal)) url = url.TrimEnd('/') + "/v1/";
                    using var request = new HttpRequestMessage(HttpMethod.Get, url.TrimEnd('/') + "/models");
                    if (provider == "openai" && !string.IsNullOrWhiteSpace(local.OpenAiApiKey))
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", local.OpenAiApiKey);
                    using var response = await clients.CreateClient("AiClient").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                    // Reuse the bounded peer reader so a provider cannot allocate an unbounded response.
                    using var doc = JsonDocument.Parse(await AiClusterTransport.ReadAsync(stream, timeout.Token));
                    var defaultModel = provider == "ollama" ? local.OllamaModel : local.OpenAiModel;
                    models = AiModelProtocol.ParseHttp(doc.RootElement).Select(m => m with { IsDefault = m.Id == defaultModel }).ToArray();
                }
                var now = clock.GetUtcNow();
                return Publish(provider, entry, fingerprint, new(provider, models.Count > 0 ? "available" : "unavailable", models, now, now,
                    models.Count == 0 ? "Le fournisseur ne publie aucun modèle utilisable." : null));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or JsonException or IOException
                or ArgumentException or HttpRequestException or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException)
            {
                var now = clock.GetUtcNow();
                return Publish(provider, entry, fingerprint, previous is { Models.Count: > 0 }
                    ? previous with { State = "stale", CheckedAt = now, Message = "Actualisation impossible. Ce catalogue ancien ne permet pas de nouvelle sélection." }
                    : new(provider, "unavailable", [], null, now, "Catalogue indisponible. Vérifiez la connexion et la compatibilité du fournisseur."));
            }
        }
        finally { entry.Gate.Release(); }
    }
    private AiModelCatalog Publish(string provider, Entry entry, string fingerprint, AiModelCatalog snapshot)
    {
        lock (entry.Sync)
        {
            if (!entries.TryGetValue(provider, out var current) || !ReferenceEquals(current, entry)
                || entry.Fingerprint != fingerprint)
                return new(provider, "unavailable", [], null, clock.GetUtcNow(), "Connexion modifiée. Actualisez le catalogue.");
            entry.Snapshot = snapshot;
            return snapshot;
        }
    }

    private string GetFingerprint(string provider, string connectionRevision)
    {
        var local = settings.GetSettings();
        // Local comparison only; the key is never returned or logged.
        return connectionRevision + (provider == "openai" ? local.OpenAiBaseUrl + local.OpenAiApiKey + local.OpenAiModel
            : provider == "anthropic" ? local.AnthropicApiKey + local.AnthropicModel
            : provider == "ollama" ? local.OllamaBaseUrl + local.OllamaModel : runtime.Find(provider));
    }

    private async Task RefreshInBackground(string provider, string connectionRevision, Entry entry)
    {
        try { await ReadAsync(provider, connectionRevision, true, CancellationToken.None); }
        catch (Exception) { }
        finally { lock (entry.Sync) entry.RefreshQueued = false; }
    }

}
