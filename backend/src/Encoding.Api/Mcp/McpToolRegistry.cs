using System.Text.Json;

namespace Encoding.Api.Mcp;

/// <summary>Routes tools/call to the domain handler that owns the operation.</summary>
public sealed class McpToolRegistry(IEnumerable<IMcpDomainHandler> handlers)
{
    private readonly Dictionary<string, IMcpDomainHandler> byTool = handlers
        .SelectMany(handler => handler.ToolNames.Select(name => (name, handler)))
        .ToDictionary(pair => pair.name, pair => pair.handler, StringComparer.Ordinal);

    public Task<McpCallToolResult> ExecuteAsync(string toolName, JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        if (!byTool.TryGetValue(toolName, out var handler))
            return Task.FromResult(McpCallToolResult.Text($"Outil introuvable : '{toolName}'", isError: true));
        return handler.HandleAsync(toolName, arguments, isAdmin, ct);
    }
}
