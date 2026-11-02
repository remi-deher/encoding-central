using System.Net.Http.Json;
using System.Text.Json;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

/// <summary>Direct call to the Anthropic Messages API with an API key (no CLI, no subscription).</summary>
public sealed class AnthropicAiClient(HttpClient httpClient, string model, string? apiKey) : IAiTextClient
{
    public const string ApiVersion = "2023-06-01";
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private static readonly HashSet<string> Efforts = new(StringComparer.Ordinal) { "low", "medium", "high", "xhigh", "max" };

    public async Task<AiCompletionResult> GenerateTextAsync(AiPrompt prompt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return new(false, string.Empty, "Aucune clé API Anthropic n’est configurée.");
        try
        {
            // Sampling parameters (temperature, top_p, top_k) are rejected by current Claude models, so none is sent.
            var body = new Dictionary<string, object?>
            {
                ["model"] = prompt.Model ?? model,
                ["max_tokens"] = 16000,
                ["system"] = prompt.SystemPrompt,
                ["messages"] = new[] { new { role = "user", content = prompt.UserPrompt } },
            };
            if (prompt.ReasoningEffort is { } effort && Efforts.Contains(effort))
                body["output_config"] = new { effort };

            using var request = new HttpRequestMessage(HttpMethod.Post, "messages") { Content = JsonContent.Create(body) };
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", ApiVersion);

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var content = await ReadBoundedAsync(response.Content, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests || (int)response.StatusCode == 529)
                return new(false, string.Empty, "Limite d’appels API Anthropic atteinte ou service surchargé.", IsRateLimited: true);
            if (!response.IsSuccessStatusCode)
                return new(false, string.Empty, $"Anthropic a refusé la requête (HTTP {(int)response.StatusCode}). Vérifiez la clé, le modèle et le crédit du compte.");

            using var doc = JsonDocument.Parse(content);
            return Parse(doc.RootElement);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(false, string.Empty, "La requête a été annulée."); }
        catch (OperationCanceledException) { return new(false, string.Empty, "Délai d'attente dépassé."); }
        catch (InvalidDataException) { return new(false, string.Empty, "La réponse du fournisseur dépasse la taille maximale autorisée."); }
        catch (HttpRequestException) { return new(false, string.Empty, "API Anthropic injoignable."); }
        catch (JsonException) { return new(false, string.Empty, "Réponse Anthropic invalide."); }
    }

    public static AiCompletionResult Parse(JsonElement root)
    {
        if (root.TryGetProperty("stop_reason", out var stop) && stop.ValueKind == JsonValueKind.String && stop.GetString() == "refusal")
            return new(false, string.Empty, "Anthropic a refusé de répondre à cette demande.");
        var text = new System.Text.StringBuilder();
        if (root.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
            foreach (var block in blocks.EnumerateArray())
                if (block.TryGetProperty("type", out var type) && type.GetString() == "text"
                    && block.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String) text.Append(value.GetString());
        var observed = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        root.TryGetProperty("usage", out var usage);
        long? Tokens(string name) => usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty(name, out var f) && f.TryGetInt64(out var n) && n >= 0 ? n : null;
        // input_tokens excludes cached reads, which are billed and reported separately.
        var cached = Tokens("cache_read_input_tokens");
        var input = Tokens("input_tokens") is { } plain ? plain + (cached ?? 0) + (Tokens("cache_creation_input_tokens") ?? 0) : (long?)null;
        return new(true, AnsiSanitizer.Sanitize(text.ToString()),
            Usage: observed is null && usage.ValueKind != JsonValueKind.Object ? null : new AiUsage(input, Tokens("output_tokens"), cached, Model: observed));
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes) throw new InvalidDataException();
        await using var input = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > MaximumResponseBytes) throw new InvalidDataException();
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return System.Text.Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }
}
