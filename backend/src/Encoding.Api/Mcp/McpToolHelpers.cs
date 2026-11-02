using System.Text.Json;

namespace Encoding.Api.Mcp;

internal static class McpToolHelpers
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static McpCallToolResult Invalid(string message) => McpCallToolResult.Text(message, isError: true);
    internal static McpCallToolResult Denied(string message) => McpCallToolResult.Text("Permission refusée : " + message, isError: true);

    internal static bool TryGuid(JsonElement args, string name, out Guid value)
    {
        value = Guid.Empty;
        return args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var item)
            && item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out value) && value != Guid.Empty;
    }

    internal static bool TryInt(JsonElement args, string name, out int value)
    {
        value = 0;
        return args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var item)
            && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out value);
    }

    internal static bool TryDeserialize<T>(JsonElement args, out T value) where T : class
    {
        try
        {
            value = args.Deserialize<T>(JsonOptions)!;
            return value is not null;
        }
        catch (JsonException)
        {
            value = null!;
            return false;
        }
    }
}
