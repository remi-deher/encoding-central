using System.Text.Json;
using Encoding.Api.Mcp;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class McpInitializeTests
{
    [Fact]
    public void InitializeResultOmitsUnsupportedCapabilities()
    {
        var result = new McpInitializeResult("2024-11-05",
            new McpCapabilities(Tools: new McpToolsCapability(ListChanged: false)),
            new McpServerInfo("encoding-mcp", "1.0.0"));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var capabilities = document.RootElement.GetProperty("capabilities");

        Assert.Equal(JsonValueKind.Object, capabilities.GetProperty("tools").ValueKind);
        Assert.False(capabilities.TryGetProperty("prompts", out _));
        Assert.False(capabilities.TryGetProperty("resources", out _));
    }
}
