using System.Text.Json;
using Encoding.Contracts.Tools;
using Encoding.Api.Mcp;

namespace Encoding.Api.Tools;

public sealed record AiAssistantToolPlan(string Kind, string? Content = null, string? Name = null, string? Arguments = null);
public sealed record AiAssistantActionCall(string Name, Guid ResourceId, int ExpectedVersion,
    Guid? SecondaryResourceId = null, int? SecondaryVersion = null, string? Text = null,
    JsonElement? Arguments = null);

/// <summary>Parses bounded read tools and explicitly enumerated action proposals. Unknown calls are rejected.</summary>
public static class AiAssistantToolPlanParser
{
    public static AiAssistantToolPlan Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 12_000)
            throw new ArgumentException("La décision d’outil du modèle est vide ou dépasse la limite.");
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException();
            var properties = root.EnumerateObject().ToArray();
            if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                throw new ArgumentException();
            var kind = root.GetProperty("kind").GetString();
            if (kind == "answer" && properties.Length == 2)
            {
                var answer = root.GetProperty("content").GetString();
                if (!string.IsNullOrWhiteSpace(answer) && answer.Length <= 8_000)
                    return new("answer", Content: answer);
            }
            if (kind == "tool_call" && properties.Length == 3
                && root.GetProperty("name").ValueKind == JsonValueKind.String
                && root.GetProperty("arguments").ValueKind == JsonValueKind.Object)
            {
                var name = root.GetProperty("name").GetString();
                if (name is not null && BusinessOperationCatalog.IsAssistantRead(name))
                    return new("tool_call", Name: name, Arguments: root.GetProperty("arguments").GetRawText());
            }
            if (kind == "action_call" && properties.Length == 3
                && root.GetProperty("name").ValueKind == JsonValueKind.String
                && root.GetProperty("arguments").ValueKind == JsonValueKind.Object)
            {
                var name = root.GetProperty("name").GetString();
                if (name is not null && BusinessOperationCatalog.IsAssistantAction(name))
                    return new("action_call", Name: name, Arguments: root.GetProperty("arguments").GetRawText());
            }
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        catch (KeyNotFoundException) { }
        throw new ArgumentException("Le modèle a retourné une décision d’outil invalide ; aucun outil n’a été exécuté.");
    }

    public static AiAssistantToolCall ParseToolCall(AiAssistantToolPlan plan)
    {
        if (plan.Kind != "tool_call" || plan.Name is null || plan.Arguments is null || !BusinessOperationCatalog.IsAssistantRead(plan.Name))
            throw new ArgumentException("Outil de lecture inconnu ou décision invalide.");
        using var document = JsonDocument.Parse(plan.Arguments);
        var arguments = document.RootElement;
        if (!BusinessOperationCatalog.ValidateArguments(plan.Name, arguments))
            throw new ArgumentException("Les arguments ne correspondent pas au contrat de l’outil.");
        Guid? flowId = null;
        if (plan.Name == "get_flow" && arguments.TryGetProperty("id", out var id)
            && Guid.TryParse(id.GetString(), out var parsed) && parsed != Guid.Empty) flowId = parsed;
        var state = arguments.TryGetProperty("state", out var stateValue) ? stateValue.GetString() : null;
        int? limit = arguments.TryGetProperty("limit", out var limitValue) && limitValue.TryGetInt32(out var parsedLimit) ? parsedLimit : null;
        return new(plan.Name, flowId, state, limit, arguments.Clone());
    }

    public static AiAssistantActionCall ParseActionCall(AiAssistantToolPlan plan)
    {
        if (plan.Kind != "action_call" || plan.Name is null || plan.Arguments is null || !BusinessOperationCatalog.IsAssistantAction(plan.Name))
            throw new ArgumentException("Action inconnue ou décision invalide.");
        using var document = JsonDocument.Parse(plan.Arguments);
        var arguments = document.RootElement;
        if (!BusinessOperationCatalog.IsAssistantAction(plan.Name))
            throw new ArgumentException("Action non autorisée.");
        if (!BusinessOperationCatalog.AssistantToMcpToolNames.ContainsKey(plan.Name))
        {
            if (BusinessOperationCatalog.FindTool(plan.Name) is not { RequiresAdministrator: true }
                || !BusinessOperationCatalog.ValidateArguments(plan.Name, arguments))
                throw new ArgumentException("Arguments d’action invalides.");
            return new(plan.Name, Guid.Empty, 0, Arguments: arguments.Clone());
        }
        var keys = arguments.EnumerateObject().Select(property => property.Name).ToArray();
        var assignment = plan.Name == "assign-library-flow";
        var createTemplate = plan.Name == "create-flow-template";
        var expectedKeys = assignment
            ? new[] { "resourceId", "expectedVersion", "flowId", "flowVersion" }
            : createTemplate
                ? new[] { "resourceId", "expectedVersion", "templateName" }
            : new[] { "resourceId", "expectedVersion" };
        if (keys.Length != expectedKeys.Length || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length
            || expectedKeys.Any(key => !keys.Contains(key, StringComparer.Ordinal)))
            throw new ArgumentException("Arguments d’action invalides.");
        var resourceValue = arguments.GetProperty("resourceId");
        var versionValue = arguments.GetProperty("expectedVersion");
        if (resourceValue.ValueKind != JsonValueKind.String
            || !Guid.TryParse(resourceValue.GetString(), out var resourceId) || resourceId == Guid.Empty
            || versionValue.ValueKind != JsonValueKind.Number || !versionValue.TryGetInt32(out var version) || version < 0)
            throw new ArgumentException("Identifiant ou version d’action invalide.");
        if (createTemplate)
        {
            var nameValue = arguments.GetProperty("templateName");
            if (nameValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nameValue.GetString())
                || nameValue.GetString()!.Trim().Length > 128)
                throw new ArgumentException("Nom de modèle invalide.");
            return new(plan.Name, resourceId, version, Text: nameValue.GetString()!.Trim());
        }
        if (!assignment) return new(plan.Name, resourceId, version);
        var flowIdValue = arguments.GetProperty("flowId");
        var flowVersionValue = arguments.GetProperty("flowVersion");
        if (flowIdValue.ValueKind != JsonValueKind.String
            || !Guid.TryParse(flowIdValue.GetString(), out var flowId) || flowId == Guid.Empty
            || flowVersionValue.ValueKind != JsonValueKind.Number
            || !flowVersionValue.TryGetInt32(out var flowVersion) || flowVersion < 1)
            throw new ArgumentException("Flow ou version publiée invalide.");
        return new(plan.Name, resourceId, version, flowId, flowVersion);
    }
}
