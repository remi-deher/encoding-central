using System.Text.Json.Nodes;

namespace Encoding.Api.Mcp;

public static partial class BusinessOperationCatalog
{
    private static readonly HashSet<string> Mutations = new(StringComparer.Ordinal)
    {
        "create_flow", "clone_flow", "delete_flow", "save_script", "publish_script",
        "save_encoding_profile", "publish_encoding_profile", "create_template_from_flow",
        "create_component_pack_from_flow", "save_plugin_configuration", "save_flow_plugin_configuration", "configure_library_node_access",
        "save_library_processing", "set_library_activation", "assign_library_flow", "request_library_discovery",
        "create_library", "update_library", "delete_library", "save_node_group", "delete_node_group",
        "save_node_identity", "set_node_draining", "save_node_processing", "cancel_execution", "retry_execution", "resume_execution",
        "delete_script", "create_resource", "update_resource", "add_resource_version", "delete_resource"
    };

    /// <summary>
    /// Assistant kebab-case special actions and their MCP snake_case tool when one exists.
    /// <c>null</c> means the assistant action has no identical MCP tool (draft publish / enqueue files).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string?> AssistantToMcpToolNames =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["publish-flow"] = null,
            ["delete-flow"] = "delete_flow",
            ["start-library"] = null,
            ["cancel-execution"] = "cancel_execution",
            ["assign-library-flow"] = "assign_library_flow",
            ["create-flow-template"] = "create_template_from_flow",
        };

    private static readonly HashSet<string> AssistantActions = new(AssistantToMcpToolNames.Keys, StringComparer.Ordinal);

    public static IReadOnlyList<BusinessOperationDescriptor> Describe() =>
        DescribeTools().Concat(AssistantActions.Order(StringComparer.Ordinal).Select(ActionDescriptor)).ToArray();

    /// <summary>Converts an MCP snake_case tool name to the assistant kebab-case form.</summary>
    public static string ToAssistantName(string mcpSnakeName) =>
        mcpSnakeName.Replace('_', '-');

    /// <summary>Converts an assistant kebab-case name to MCP snake_case (mechanical; use <see cref="TryResolveMcpToolName"/> for specials).</summary>
    public static string ToMcpName(string assistantKebabName) =>
        assistantKebabName.Replace('-', '_');

    /// <summary>
    /// Resolves an assistant or MCP name to a catalog MCP tool name when one exists.
    /// Special assistant actions without an MCP twin return false.
    /// </summary>
    public static bool TryResolveMcpToolName(string name, out string mcpName)
    {
        if (FindTool(name) is not null)
        {
            mcpName = name;
            return true;
        }

        if (AssistantToMcpToolNames.TryGetValue(name, out var mapped) && mapped is not null)
        {
            mcpName = mapped;
            return true;
        }

        var mechanical = ToMcpName(name);
        if (!string.Equals(mechanical, name, StringComparison.Ordinal) && FindTool(mechanical) is not null)
        {
            mcpName = mechanical;
            return true;
        }

        mcpName = "";
        return false;
    }

    public static IReadOnlyList<BusinessOperationDescriptor> DescribeTools() =>
        ToolDefinitions.Select(tool =>
        {
            var mutation = Mutations.Contains(tool.Name);
            return new BusinessOperationDescriptor(
                tool.Name,
                DomainFor(tool.Name),
                tool.Description,
                !mutation,
                mutation,
                !mutation,
                CompleteInputSchema(tool.InputSchema),
                RequiresConfirmation: mutation);
        }).ToArray();

    public static BusinessOperationDescriptor? FindTool(string name) =>
        DescribeTools().FirstOrDefault(operation => operation.Name == name);

    public static bool ValidateArguments(string name, System.Text.Json.JsonElement arguments)
    {
        var operation = FindTool(name);
        if (operation is null || arguments.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
        using var schema = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(operation.InputSchema));
        return MatchesSchema(arguments, schema.RootElement, rejectUnknownProperties: true);
    }

    private static JsonNode CompleteInputSchema(object inputSchema)
    {
        var node = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(inputSchema))!.AsObject();
        CompleteSchemaNode(node, isRoot: true);
        return node;
    }

    private static void CompleteSchemaNode(JsonObject schema, bool isRoot = false)
    {
        var type = schema["type"] is JsonValue typeValue ? typeValue.GetValue<string>() : null;
        if (type == "array")
        {
            schema["maxItems"] ??= 1000;
            schema["items"] ??= new JsonObject();
        }
        if (type == "string") schema["maxLength"] ??= 65536;
        if (type == "object") schema["maxProperties"] ??= 256;
        if (type == "object" && (isRoot || schema["properties"] is JsonObject) && schema["additionalProperties"] is null)
            schema["additionalProperties"] = false;

        if (schema["properties"] is JsonObject properties)
            foreach (var (name, node) in properties)
            {
                if (node is not JsonObject property) continue;
                if (name is "libraryId" or "flowId" or "nodeId" or "executionId" or "sourceFlowId" or "transferSourceNodeId"
                    or "profileId" or "packId" or "templateId" or "resourceId" || isRoot && name == "id")
                    property["format"] ??= "uuid";
                if (name == "nodeIds" && property["items"] is JsonObject nodeIdsItem)
                    nodeIdsItem["format"] ??= "uuid";
                CompleteSchemaNode(property);
            }
        if (schema["items"] is JsonObject items)
            CompleteSchemaNode(items);
        if (schema["additionalProperties"] is JsonObject additionalProperties)
            CompleteSchemaNode(additionalProperties);
    }

    private static bool MatchesSchema(System.Text.Json.JsonElement value, System.Text.Json.JsonElement schema, bool rejectUnknownProperties = false)
    {
        if (schema.TryGetProperty("type", out var type))
        {
            var matches = type.ValueKind == System.Text.Json.JsonValueKind.Array
                ? type.EnumerateArray().Any(item => MatchesType(value, item.GetString()))
                : MatchesType(value, type.GetString());
            if (!matches) return false;
        }

        if (schema.TryGetProperty("enum", out var allowed)
            && !allowed.EnumerateArray().Any(item => JsonValuesEqual(value, item))) return false;

        if (value.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (schema.TryGetProperty("minLength", out var minLength) && text.Length < minLength.GetInt32()) return false;
            if (schema.TryGetProperty("maxLength", out var maxLength) && text.Length > maxLength.GetInt32()) return false;
            if (schema.TryGetProperty("format", out var format) && format.GetString() == "uuid"
                && (!Guid.TryParse(text, out var id) || id == Guid.Empty)) return false;
            if (schema.TryGetProperty("pattern", out var pattern)
                && !System.Text.RegularExpressions.Regex.IsMatch(text, pattern.GetString()!,
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return false;
            if (schema.TryGetProperty("minLength", out _) && string.IsNullOrWhiteSpace(text)) return false;
        }

        if (value.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            if (schema.TryGetProperty("type", out var numericType) &&
                (numericType.ValueKind == System.Text.Json.JsonValueKind.String && numericType.GetString() == "integer"
                 || numericType.ValueKind == System.Text.Json.JsonValueKind.Array && numericType.EnumerateArray().Any(item => item.GetString() == "integer"))
                && !value.TryGetInt64(out _)) return false;
            if (!value.TryGetDecimal(out var numericValue)) return false;
            if (schema.TryGetProperty("minimum", out var minimum) && numericValue < minimum.GetDecimal()) return false;
            if (schema.TryGetProperty("maximum", out var maximum) && numericValue > maximum.GetDecimal()) return false;
        }

        if (value.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var items = value.EnumerateArray().ToArray();
            if (schema.TryGetProperty("minItems", out var minItems) && items.Length < minItems.GetInt32()) return false;
            if (schema.TryGetProperty("maxItems", out var maxItems) && items.Length > maxItems.GetInt32()) return false;
            if (schema.TryGetProperty("items", out var itemSchema)
                && items.Any(item => !MatchesSchema(item, itemSchema))) return false;
        }

        if (value.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var supplied = value.EnumerateObject().ToArray();
            if (supplied.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != supplied.Length) return false;
            if (schema.TryGetProperty("maxProperties", out var maxProperties) && supplied.Length > maxProperties.GetInt32()) return false;
            if (schema.TryGetProperty("required", out var required)
                && required.EnumerateArray().Any(item => !value.TryGetProperty(item.GetString()!, out _))) return false;
            if (schema.TryGetProperty("properties", out var properties))
            {
                foreach (var property in supplied)
                {
                    if (properties.TryGetProperty(property.Name, out var definition))
                    {
                        if (!MatchesSchema(property.Value, definition)) return false;
                    }
                    else if (schema.TryGetProperty("additionalProperties", out var additional))
                    {
                        if (additional.ValueKind == System.Text.Json.JsonValueKind.False
                            || additional.ValueKind == System.Text.Json.JsonValueKind.Object && !MatchesSchema(property.Value, additional)) return false;
                    }
                    else if (rejectUnknownProperties || properties.EnumerateObject().Any()) return false;
                }
            }
            else if (schema.TryGetProperty("additionalProperties", out var additionalProperties))
            {
                if (additionalProperties.ValueKind == System.Text.Json.JsonValueKind.False && supplied.Length > 0) return false;
                if (additionalProperties.ValueKind == System.Text.Json.JsonValueKind.Object
                    && supplied.Any(property => !MatchesSchema(property.Value, additionalProperties))) return false;
            }
        }
        return true;
    }

    private static bool MatchesType(System.Text.Json.JsonElement value, string? type) => type switch
    {
        "null" => value.ValueKind == System.Text.Json.JsonValueKind.Null,
        "string" => value.ValueKind == System.Text.Json.JsonValueKind.String,
        "integer" => value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == System.Text.Json.JsonValueKind.Number,
        "boolean" => value.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False,
        "object" => value.ValueKind == System.Text.Json.JsonValueKind.Object,
        "array" => value.ValueKind == System.Text.Json.JsonValueKind.Array,
        _ => false
    };

    private static bool JsonValuesEqual(System.Text.Json.JsonElement left, System.Text.Json.JsonElement right) =>
        left.ValueKind == right.ValueKind && left.ToString() == right.ToString();

    public static bool IsAssistantRead(string name) => FindTool(name) is { ReadOnly: true, AvailableToAssistant: true };

    public static bool IsAssistantAction(string name) => AssistantActions.Contains(name)
        || FindTool(name) is { RequiresAdministrator: true };

    private static BusinessOperationDescriptor ActionDescriptor(string name)
    {
        object schema = name switch
        {
            "assign-library-flow" => new { type = "object", properties = new { resourceId = new { type = "string" }, expectedVersion = new { type = "integer" },
                flowId = new { type = "string" }, flowVersion = new { type = "integer" } }, required = new[] { "resourceId", "expectedVersion", "flowId",
                "flowVersion" } },
            "create-flow-template" => new { type = "object", properties = new { resourceId = new { type = "string" },
                expectedVersion = new { type = "integer" }, templateName = new { type = "string" } }, required = new[] { "resourceId", "expectedVersion",
                "templateName" } },
            _ => new { type = "object", properties = new { resourceId = new { type = "string" }, expectedVersion = new { type = "integer" } }, required = new[] { "resourceId", "expectedVersion" } }
        };
        return new(name, name switch
        {
            "create-flow-template" => "flow-templates",
            "assign-library-flow" or "start-library" => "libraries",
            "cancel-execution" => "executions-and-logs",
            _ => "flows-and-components"
        }, name switch
        {
            "create-flow-template" => "Crée un modèle réutilisable depuis une version publiée d’un flow, après confirmation.",
            "assign-library-flow" => "Affecte une version publiée d’un flow à une bibliothèque, après confirmation.",
            "start-library" => "Met en file les fichiers détectés d’une bibliothèque, après confirmation.",
            "cancel-execution" => "Demande l’annulation d’une exécution, après confirmation.",
            "publish-flow" => "Publie le brouillon d’un flow, après confirmation.",
            _ => "Supprime un flow, après confirmation."
        }, false, true, true, CompleteInputSchema(schema), true);
    }

    private static string DomainFor(string name) => name switch
    {
        "get_version" or "get_updates" => "updates",
        "get_libraries_overview" or "list_libraries" or "create_library" or "update_library" or "delete_library" or "get_library_node_access" or "get_library_workflow" or "get_library_processing" or "configure_library_node_access" or "save_library_processing" or "set_library_activation" or "assign_library_flow" or "request_library_discovery" => "libraries",
        "list_nodes" or "list_node_groups" or "get_node_group" or "save_node_group" or "delete_node_group" or "get_node_activity" or "get_node_identity" or "save_node_identity" or "set_node_draining" or "get_node_processing" or "save_node_processing" => "nodes-and-groups",
        "list_executions" or "get_execution" or "get_execution_events" or "get_execution_pipeline" or "cancel_execution" or "retry_execution" or "resume_execution" or "get_system_logs" => "executions-and-logs",
        "get_flow_schema" or "list_flows" or "get_flow" or "create_flow" or "clone_flow" or "delete_flow" => "flows-and-components",
        "list_scripts" or "get_script" or "save_script" or "publish_script" or "delete_script" or "validate_script_draft" => "scripts",
        "list_resources" or "get_resource" or "create_resource" or "update_resource" or "add_resource_version" or "delete_resource" => "scripts",
        "list_encoding_profiles" or "get_encoding_profile" or "save_encoding_profile" or "publish_encoding_profile" => "encoding-profiles",
        "list_templates" or "get_template" or "create_template_from_flow" => "flow-templates",
        "list_component_packs" or "get_component_pack" or "create_component_pack_from_flow" => "component-packs",
        "list_plugins" or "get_plugin_configuration" or "save_plugin_configuration" or "test_plugin_connection" or "get_flow_plugin_configuration" or "save_flow_plugin_configuration" => "plugins-and-integrations",
        "list_media_tools" or "get_media_tool_info" or "validate_tool_definition" or "validate_plugin_manifest" => "tools-and-validation",
        _ => "other"
    };
}
