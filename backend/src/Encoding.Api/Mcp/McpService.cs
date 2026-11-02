using System.Security.Claims;
using System.Text.Json;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Mcp;

/// <summary>
/// Thin MCP façade: JSON-RPC list/validate/dispatch. Domain work lives in <see cref="IMcpDomainHandler"/> implementations.
/// </summary>
public sealed class McpService(McpToolRegistry registry, ILogger<McpService> logger)
{
    public async Task<McpJsonRpcResponse?> HandleMessageAsync(McpJsonRpcRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        if (request.JsonRpc != "2.0" || string.IsNullOrWhiteSpace(request.Method)
            || request.Id is { } suppliedId && suppliedId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null)
            || request.Params is { } suppliedParams && suppliedParams.ValueKind != JsonValueKind.Object)
            return McpJsonRpcResponse.Failure(null, -32600, "Requête JSON-RPC invalide.");
        if (request.Params is { } boundedParams && boundedParams.GetRawText().Length > 1_048_576)
            return McpJsonRpcResponse.Failure(request.Id, -32600, "Les paramètres JSON-RPC dépassent la taille maximale autorisée.");
        // JSON-RPC notifications never receive a response and must not trigger a tool mutation.
        if (request.Id is null)
            return null;
        try
        {
            return request.Method switch
            {
                "initialize" => HandleInitialize(request),
                "notifications/initialized" => null,
                "ping" => McpJsonRpcResponse.Success(request.Id, new { }),
                "tools/list" => McpJsonRpcResponse.Success(request.Id, new McpToolsListResult(
                    BusinessOperationCatalog.DescribeTools()
                        .Select(operation => new McpToolDefinition(operation.Name, operation.Description, operation.InputSchema)).ToArray())),
                "tools/call" => await HandleCallToolAsync(request, user, ct),
                _ => McpJsonRpcResponse.Failure(request.Id, -32601, $"Méthode inconnue : {request.Method}")
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erreur lors du traitement de la requête MCP {Method}", request.Method);
            return McpJsonRpcResponse.Failure(request.Id, -32603, $"Erreur interne du serveur MCP : {ex.Message}");
        }
    }

    /// <summary>Invokes the assistant's bounded read-only subset through the same handlers MCP exposes.</summary>
    public async Task<AiAssistantToolResult> CallAssistantReadToolAsync(AiAssistantToolCall call, CancellationToken ct)
    {
        var operation = BusinessOperationCatalog.FindTool(call.Name);
        if (operation is null || !operation.ReadOnly || !operation.AvailableToAssistant)
            throw new ArgumentException("Outil de lecture IA inconnu ou paramètres invalides.");
        var arguments = call.Arguments is { ValueKind: JsonValueKind.Object } supplied ? supplied : call.Name switch
        {
            "get_flow" when call.FlowId is { } flowId => JsonSerializer.SerializeToElement(new { id = flowId }, McpToolHelpers.JsonOptions),
            "list_executions" => JsonSerializer.SerializeToElement(new { state = call.State, limit = call.Limit ?? 20 }, McpToolHelpers.JsonOptions),
            _ when call.FlowId is null && call.State is null && call.Limit is null => JsonSerializer.SerializeToElement(new { }, McpToolHelpers.JsonOptions),
            _ => default
        };
        if (!BusinessOperationCatalog.ValidateArguments(call.Name, arguments))
            throw new ArgumentException("Les arguments ne correspondent pas au contrat de l’outil.");
        var result = await ExecuteOperationAsync(call.Name, arguments, isAdmin: false, ct);
        if (result.IsError) throw new InvalidOperationException("L’outil de lecture n’a pas pu obtenir son résultat.");
        var raw = result.Content.FirstOrDefault()?.Text ?? "null";
        using var document = JsonDocument.Parse(raw);
        if (raw.Length > 80_000) throw new ArgumentException("Le résultat de l’outil dépasse la limite de contexte.");
        var href = operation.Domain switch
        {
            "libraries" => "/libraries",
            "nodes-and-groups" => "/nodes",
            "executions-and-logs" => "/tasks",
            "scripts" => "/scripts",
            "encoding-profiles" => "/settings/encoding-profiles",
            "plugins-and-integrations" => "/plugins",
            "flow-templates" or "flows-and-components" => "/flows",
            _ => "/"
        };
        return new(call.Name, operation.Description, href, document.RootElement.Clone());
    }

    private static McpJsonRpcResponse HandleInitialize(McpJsonRpcRequest request)
    {
        var result = new McpInitializeResult(
            ProtocolVersion: "2024-11-05",
            Capabilities: new McpCapabilities(Tools: new McpToolsCapability(ListChanged: false)),
            ServerInfo: new McpServerInfo("encoding-mcp", "1.0.0")
        );
        return McpJsonRpcResponse.Success(request.Id, result);
    }

    private async Task<McpJsonRpcResponse> HandleCallToolAsync(McpJsonRpcRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        if (request.Params is not { } parameters || parameters.ValueKind != JsonValueKind.Object)
            return McpJsonRpcResponse.Failure(request.Id, -32602, "Paramètres invalides pour tools/call");

        if (!parameters.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String
            || nameProp.GetString() is not { Length: > 0 } toolName)
            return McpJsonRpcResponse.Failure(request.Id, -32602, "Le nom de l'outil est requis dans tools/call");

        if (parameters.TryGetProperty("arguments", out var argsProp) && argsProp.ValueKind != JsonValueKind.Object)
            return McpJsonRpcResponse.Failure(request.Id, -32602, "Les arguments de tools/call doivent être un objet.");
        var arguments = parameters.TryGetProperty("arguments", out argsProp)
            ? argsProp
            : JsonSerializer.SerializeToElement(new { }, McpToolHelpers.JsonOptions);

        var callResult = await ExecuteForPrincipalAsync(toolName, arguments, user, ct);

        return McpJsonRpcResponse.Success(request.Id, callResult);
    }

    /// <summary>
    /// Exécute une opération au nom d’un appelant : applique les portées d’un jeton API et journalise toute tentative d’écriture
    /// (outil, acteur, issue et noms des arguments, jamais leurs valeurs).
    /// </summary>
    public async Task<McpCallToolResult> ExecuteForPrincipalAsync(string toolName, JsonElement arguments, ClaimsPrincipal user, CancellationToken ct)
    {
        var operation = BusinessOperationCatalog.FindTool(toolName);
        if (operation is null || operation.ReadOnly)
            return await ExecuteOperationAsync(toolName, arguments, user.IsInRole("admin"), ct);

        var actor = $"{(McpAccess.IsApiToken(user) ? "jeton" : "session")} « {user.Identity?.Name ?? "inconnu"} »";
        var names = arguments.ValueKind == JsonValueKind.Object ? string.Join(',', arguments.EnumerateObject().Select(property => property.Name)) : "";
        if (McpAccess.MissingScope(user, operation) is { } missing)
        {
            logger.LogWarning("Audit MCP : écriture refusée pour {Actor}, outil {Tool}, portée manquante {Scope}.", actor, toolName, missing);
            return McpCallToolResult.Text(McpAccess.MissingScopeMessage(missing), isError: true);
        }

        var result = await ExecuteOperationAsync(toolName, arguments, user.IsInRole("admin"), ct);
        logger.LogWarning("Audit MCP : écriture {Outcome} par {Actor}, outil {Tool}, arguments [{Arguments}].",
            result.IsError ? "en échec ou refusée" : "réussie", actor, toolName, names);
        return result;
    }

    /// <summary>Execute a catalogued operation; all public surfaces share this dispatcher.</summary>
    public Task<McpCallToolResult> ExecuteOperationAsync(string toolName, JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        var operation = BusinessOperationCatalog.FindTool(toolName);
        if (operation is null)
            return Task.FromResult(McpCallToolResult.Text($"Outil introuvable : '{toolName}'", isError: true));
        if (arguments.ValueKind != JsonValueKind.Object)
            arguments = JsonSerializer.SerializeToElement(new { }, McpToolHelpers.JsonOptions);
        if (!BusinessOperationCatalog.ValidateArguments(toolName, arguments))
            return Task.FromResult(McpCallToolResult.Text("Les arguments ne correspondent pas au schéma de l’opération.", isError: true));

        return registry.ExecuteAsync(toolName, arguments, isAdmin, ct);
    }
}
