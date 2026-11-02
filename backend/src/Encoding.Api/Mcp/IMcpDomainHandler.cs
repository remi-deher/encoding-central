using System.Text.Json;

namespace Encoding.Api.Mcp;

/// <summary>Domain handler for a subset of MCP business-operation tools.</summary>
public interface IMcpDomainHandler
{
    IReadOnlyCollection<string> ToolNames { get; }
    Task<McpCallToolResult> HandleAsync(string toolName, JsonElement arguments, bool isAdmin, CancellationToken ct);
}
