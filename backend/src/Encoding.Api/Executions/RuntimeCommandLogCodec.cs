using System.Text.Json;
using Encoding.Contracts.Executions;

namespace Encoding.Api.Executions;

/// <summary>Reads command audits written both as one array and as adjacent legacy JSON arrays.</summary>
internal static class RuntimeCommandLogCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaximumLegacyPayloadBytes = 4 * 1024 * 1024;

    internal static bool TryDeserialize(string? value, out RuntimeCommandLog[] logs)
    {
        logs = [];
        if (string.IsNullOrWhiteSpace(value)) return true;

        try
        {
            logs = JsonSerializer.Deserialize<RuntimeCommandLog[]>(value, Json) ?? [];
            return true;
        }
        catch (JsonException)
        {
            return TryDeserializeAdjacentArrays(value, out logs);
        }
    }

    private static bool TryDeserializeAdjacentArrays(string value, out RuntimeCommandLog[] logs)
    {
        logs = [];
        if (System.Text.Encoding.UTF8.GetByteCount(value) > MaximumLegacyPayloadBytes) return false;

        try
        {
            var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(value), new JsonReaderOptions { AllowMultipleValues = true });
            var batches = new List<RuntimeCommandLog[]>();
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.StartArray) return false;
                using var document = JsonDocument.ParseValue(ref reader);
                var batch = document.RootElement.Deserialize<RuntimeCommandLog[]>(Json);
                if (batch is null) return false;
                batches.Add(batch);
                if (batches.Sum(items => items.Length) > 4096) return false;
            }

            if (batches.Count < 2) return false;
            logs = batches.SelectMany(batch => batch)
                .GroupBy(log => log.Id)
                .Select(group => group.Last())
                .OrderBy(log => log.StartedAt)
                .TakeLast(64)
                .ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
