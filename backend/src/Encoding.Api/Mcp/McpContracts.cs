using System.Text.Json;
using System.Text.Json.Serialization;

namespace Encoding.Api.Mcp;

/// <summary>
/// Représente une requête JSON-RPC 2.0 reçue selon la spécification Model Context Protocol (MCP).
/// </summary>
public sealed record McpJsonRpcRequest(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("id")] JsonElement? Id = null,
    [property: JsonPropertyName("params")] JsonElement? Params = null);

/// <summary>
/// Représente une réponse JSON-RPC 2.0 renvoyée selon la spécification Model Context Protocol (MCP).
/// </summary>
public sealed record McpJsonRpcResponse(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("result"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Result = null,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] McpJsonRpcError? Error = null)
{
    public static McpJsonRpcResponse Success(JsonElement? id, object result) =>
        new("2.0", id, Result: result);

    public static McpJsonRpcResponse Failure(JsonElement? id, int code, string message, object? data = null) =>
        new("2.0", id, Error: new McpJsonRpcError(code, message, data));
}

/// <summary>
/// Erreur JSON-RPC standard.
/// </summary>
public sealed record McpJsonRpcError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data")] object? Data = null);

/// <summary>
/// Déclaration d'un outil MCP avec sa description et son schéma JSON de paramètres.
/// </summary>
public sealed record McpToolDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("inputSchema")] object InputSchema);

/// <summary>
/// Résultat de la requête standard tools/list.
/// </summary>
public sealed record McpToolsListResult(
    [property: JsonPropertyName("tools")] IReadOnlyList<McpToolDefinition> Tools);

/// <summary>
/// Résultat de l'invocation d'un outil MCP via tools/call.
/// </summary>
public sealed record McpCallToolResult(
    [property: JsonPropertyName("content")] IReadOnlyList<McpContentItem> Content,
    [property: JsonPropertyName("isError")] bool IsError = false)
{
    public static McpCallToolResult Text(string text, bool isError = false) =>
        new([new McpContentItem("text", text)], isError);
}

public sealed record McpContentItem(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);

/// <summary>
/// Résultat de la négociation initiale de protocole (initialize).
/// </summary>
public sealed record McpInitializeResult(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("capabilities")] McpCapabilities Capabilities,
    [property: JsonPropertyName("serverInfo")] McpServerInfo ServerInfo);

public sealed record McpCapabilities(
    [property: JsonPropertyName("tools"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] McpToolsCapability? Tools = null,
    [property: JsonPropertyName("resources"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Resources = null,
    [property: JsonPropertyName("prompts"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Prompts = null);

public sealed record McpToolsCapability(
    [property: JsonPropertyName("listChanged")] bool ListChanged = false);

public sealed record McpServerInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version);
