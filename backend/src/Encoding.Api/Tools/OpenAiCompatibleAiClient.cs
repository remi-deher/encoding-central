using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Tools;

public sealed class OpenAiCompatibleAiClient(HttpClient httpClient, string model = "gpt-4o-mini", string? apiKey = null,
    bool supportsReasoningEffort = false) : IAiTextClient
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;

    public async Task<AiCompletionResult> GenerateTextAsync(AiPrompt prompt, CancellationToken ct = default)
    {
        try
        {
            var requestBody = new Dictionary<string, object?>
            {
                ["model"] = prompt.Model ?? model,
                ["messages"] = new[]
                {
                    new { role = "system", content = prompt.SystemPrompt },
                    new { role = "user", content = prompt.UserPrompt }
                }
            };
            if (supportsReasoningEffort && prompt.ReasoningEffort is { Length: > 0 })
                requestBody["reasoning_effort"] = prompt.ReasoningEffort;
            else
                requestBody["temperature"] = prompt.Temperature;

            using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = JsonContent.Create(requestBody)
            };

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var content = await ReadBoundedResponseAsync(response.Content, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                return new AiCompletionResult(false, string.Empty, "Limite d'appels API atteinte (HTTP 429).", IsRateLimited: true);
            }

            if (HasStructuredRateLimit(content))
                return new AiCompletionResult(false, string.Empty, "Le fournisseur a signalé une limite de quota.", IsRateLimited: true);

            if (!response.IsSuccessStatusCode)
            {
                return new AiCompletionResult(false, string.Empty, $"Le fournisseur IA a refusé la requête (HTTP {(int)response.StatusCode}). Vérifiez la configuration et les quotas.");
            }

            using var doc = JsonDocument.Parse(content);
            var reply = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            var root = doc.RootElement;
            var observedModel = root.TryGetProperty("model", out var observed) && observed.ValueKind == JsonValueKind.String ? observed.GetString() : null;
            root.TryGetProperty("usage", out var usage);
            long? Tokens(string name) => usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty(name, out var field)
                && field.TryGetInt64(out var count) && count >= 0 ? count : null;
            return new AiCompletionResult(true, AnsiSanitizer.Sanitize(reply),
                Usage: observedModel is null && usage.ValueKind != JsonValueKind.Object ? null
                    : new AiUsage(Tokens("prompt_tokens"), Tokens("completion_tokens"), Model: observedModel));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new AiCompletionResult(false, string.Empty, "La requête a été annulée.");
        }
        catch (OperationCanceledException)
        {
            return new AiCompletionResult(false, string.Empty, "Délai d'attente dépassé.");
        }
        catch (InvalidDataException)
        {
            return new AiCompletionResult(false, string.Empty, "La réponse du fournisseur dépasse la taille maximale autorisée.");
        }
        catch (Exception ex)
        {
            return new AiCompletionResult(false, string.Empty, ex.Message);
        }
    }

    private static async Task<string> ReadBoundedResponseAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("Provider response exceeded the configured limit.");
        await using var input = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("Provider response exceeded the configured limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return System.Text.Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private static bool HasStructuredRateLimit(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
                return false;
            foreach (var field in new[] { "type", "code" })
            {
                if (!error.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String) continue;
                var marker = value.GetString();
                if (marker is null) continue;
                var normalized = marker.Replace('-', '_').ToLowerInvariant();
                if (normalized.Contains("rate_limit", StringComparison.Ordinal)
                    || normalized.Contains("quota_exceeded", StringComparison.Ordinal)
                    || normalized.Contains("insufficient_quota", StringComparison.Ordinal)
                    || normalized is "billing_hard_limit_reached" or "billing_not_active") return true;
            }
            if (error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number
                && code.TryGetInt32(out var statusCode) && statusCode == 429) return true;
        }
        catch (JsonException) { }
        return false;
    }
}
