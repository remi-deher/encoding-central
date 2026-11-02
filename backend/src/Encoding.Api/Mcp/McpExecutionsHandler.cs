using System.Security.Claims;
using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Encoding.Api.Executions;
using Encoding.Api.Flows;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Libraries;
using Encoding.Api.Logging;
using Encoding.Api.Nodes;
using Encoding.Api.Plugins;
using Encoding.Api.Resources;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Logging;
using Encoding.Contracts.Plugins;
using Encoding.Core.Logging;
using Encoding.Contracts.Tools;

namespace Encoding.Api.Mcp;

public sealed class McpExecutionsHandler(
    ExecutionRepository executions,
    FlowRepository flows,
    FlowComponentRegistry components,
    FlowDefinitionValidator validator,
    NodeRepository nodes,
    NodePresenceOptions presenceOptions)
    : IMcpDomainHandler
{
    private static JsonSerializerOptions JsonOptions => McpToolHelpers.JsonOptions;

    private static McpCallToolResult Invalid(string message) => McpToolHelpers.Invalid(message);
    private static McpCallToolResult Denied(string message) => McpToolHelpers.Denied(message);
    private static bool TryGuid(JsonElement args, string name, out Guid value) => McpToolHelpers.TryGuid(args, name, out value);
    private static bool TryInt(JsonElement args, string name, out int value) => McpToolHelpers.TryInt(args, name, out value);
    private static bool TryDeserialize<T>(JsonElement args, out T value) where T : class => McpToolHelpers.TryDeserialize(args, out value);

    public IReadOnlyCollection<string> ToolNames { get; } =
    [
        "list_nodes",
        "get_execution",
        "get_execution_events",
        "get_execution_pipeline",
        "cancel_execution",
        "retry_execution",
        "resume_execution",
        "get_flow_schema",
        "list_flows",
        "get_flow",
        "create_flow",
        "clone_flow",
        "delete_flow",
        "validate_script_draft"
    ];

    public async Task<McpCallToolResult> HandleAsync(string toolName, JsonElement arguments, bool isAdmin, CancellationToken ct) =>
        toolName switch
        {
            "list_nodes" => await ToolListNodesAsync(ct),
            "get_execution" => await ToolGetExecutionAsync(arguments, ct),
            "get_execution_events" => await ToolGetExecutionEventsAsync(arguments, ct),
            "get_execution_pipeline" => await ToolGetExecutionPipelineAsync(arguments, ct),
            "cancel_execution" => await ToolCancelExecutionAsync(arguments, isAdmin, ct),
            "retry_execution" => await ToolRetryExecutionAsync(arguments, isAdmin, ct),
            "resume_execution" => await ToolResumeExecutionAsync(arguments, isAdmin, ct),
            "get_flow_schema" => ToolGetFlowSchema(),
            "list_flows" => await ToolListFlowsAsync(ct),
            "get_flow" => await ToolGetFlowAsync(arguments, ct),
            "create_flow" => await ToolCreateFlowAsync(arguments, isAdmin, ct),
            "clone_flow" => await ToolCloneFlowAsync(arguments, isAdmin, ct),
            "delete_flow" => await ToolDeleteFlowAsync(arguments, isAdmin, ct),
            "validate_script_draft" => ToolValidateScriptDraft(arguments),
            _ => McpCallToolResult.Text($"Outil introuvable : '{toolName}'", isError: true)
        };

    public async Task<McpCallToolResult> ToolGetExecutionAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryGuid(args, "id", out var id)) return Invalid("Paramètre 'id' (UUID) requis.");
        var result = await executions.GetAsync(id, ct);
        return result is null ? Invalid("Exécution introuvable.") : McpCallToolResult.Text(JsonSerializer.Serialize(result, JsonOptions));
    }

    public async Task<McpCallToolResult> ToolGetExecutionEventsAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryGuid(args, "id", out var id)) return Invalid("Paramètre 'id' (UUID) requis.");
        if (await executions.GetAsync(id, ct) is null) return Invalid("Exécution introuvable.");
        int? after = TryInt(args, "afterRevision", out var revision) ? Math.Max(0, revision) : null;
        int? limit = TryInt(args, "limit", out var count) ? Math.Clamp(count, 1, 1000) : null;
        return McpCallToolResult.Text(JsonSerializer.Serialize(await executions.EventsAsync(id, after, limit, ct), JsonOptions));
    }

    public async Task<McpCallToolResult> ToolGetExecutionPipelineAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryGuid(args, "id", out var id)) return Invalid("Paramètre 'id' (UUID) requis.");
        if (await executions.GetAsync(id, ct) is null) return Invalid("Exécution introuvable.");
        return McpCallToolResult.Text(JsonSerializer.Serialize(await executions.PipelineAsync(id, ct), JsonOptions));
    }

    public async Task<McpCallToolResult> ToolCancelExecutionAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("L’annulation d’une exécution exige le rôle administrateur.");
        if (!TryGuid(args, "id", out var id)) return Invalid("Paramètre 'id' (UUID) requis.");
        int? revision = TryInt(args, "expectedRevision", out var value) ? value : null;
        return await executions.CancelAsync(id, ct, revision) ? McpCallToolResult.Text("Annulation demandée.") : Invalid("Exécution introuvable ou non annulable dans cet état.");
    }

    public async Task<McpCallToolResult> ToolRetryExecutionAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La relance d’une exécution exige le rôle administrateur.");
        if (!TryGuid(args, "id", out var id)) return Invalid("Paramètre 'id' (UUID) requis.");
        var next = await executions.RetryAsync(id, ct);
        return next is null ? Invalid("La relance est réservée aux exécutions en échec et ne peut être enregistrée qu’une fois.") : McpCallToolResult.Text(JsonSerializer.Serialize(new { id = next },
            JsonOptions));
    }

    public async Task<McpCallToolResult> ToolResumeExecutionAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La reprise d’une exécution exige le rôle administrateur.");
        if (!TryGuid(args, "id", out var id)) return Invalid("Paramètre 'id' (UUID) requis.");
        var resumedId = await executions.ResumeAsync(id, ct);
        return resumedId is { } next
            ? McpCallToolResult.Text(JsonSerializer.Serialize(new { id = next, state = "queued", sourceExecutionId = id }, JsonOptions))
            : Invalid("Reprise impossible : seule une exécution annulée sans reprise précédente peut être relancée.");
    }

    public McpCallToolResult ToolGetFlowSchema()
    {
        var available = components.Plugins.SelectMany(p => p.Components.Select(c => new
        {
            componentId = c.Id,
            pluginId = p.Id,
            name = c.Name,
            description = c.Description,
            category = c.Kind,
            mediaType = c.MediaType,
            supportedFlowMediaTypes = c.SupportedFlowMediaTypes,
            outputs = c.Outputs,
            schema = c.ConfigurationSchema
        })).ToList();

        var example = new
        {
            formatVersion = 1,
            name = "Exemple Flow Automatique",
            mediaType = "video",
            nodes = new object[]
            {
                new { id = "entry", component = "base-input-file", name = "Entrée Fichier" },
                new { id = "audio_rules", component = "base-audio-rules", name = "Règles Audio" }
            },
            connections = new object[]
            {
                new { id = "c1", sourceNodeId = "entry", sourceOutputId = "success", targetNodeId = "audio_rules" }
            }
        };

        var response = new
        {
            formatVersion = 1,
            supportedMediaTypes = new[] { "video", "image", "music", "subtitles" },
            availableComponents = available,
            example
        };

        return McpCallToolResult.Text(JsonSerializer.Serialize(response, JsonOptions));
    }

    public async Task<McpCallToolResult> ToolListFlowsAsync(CancellationToken ct)
    {
        var list = await flows.ListAsync(ct);
        return McpCallToolResult.Text(JsonSerializer.Serialize(list, JsonOptions));
    }

    public async Task<McpCallToolResult> ToolGetFlowAsync(JsonElement arguments, CancellationToken ct)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("id", out var idProp) || !idProp.TryGetGuid(out var id))
            return McpCallToolResult.Text("Paramètre 'id' (UUID valide) requis.", isError: true);

        if (await flows.DraftAsync(id, ct) is { } draft)
            return McpCallToolResult.Text(JsonSerializer.Serialize(new { isDraft = true, draft }, JsonOptions));

        if (await flows.GetAsync(id, null, ct) is { } flow)
            return McpCallToolResult.Text(JsonSerializer.Serialize(new { isDraft = false, flow }, JsonOptions));

        return McpCallToolResult.Text($"Flow introuvable avec l'ID {id}.", isError: true);
    }

    public async Task<McpCallToolResult> ToolCreateFlowAsync(JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin)
            return McpCallToolResult.Text("Permission refusée : la création de flow requiert le rôle administrateur.", isError: true);

        if (arguments.ValueKind != JsonValueKind.Object)
            return McpCallToolResult.Text("Arguments invalides pour create_flow.", isError: true);

        var definition = FlowEndpoints.ParseFlowDefinition(arguments, components);
        var shouldPublish = !arguments.TryGetProperty("publish", out var pubProp) || pubProp.ValueKind != JsonValueKind.False;

        if (shouldPublish)
        {
            var validation = validator.Validate(definition);
            if (!validation.IsValid)
            {
                var errors = string.Join("; ", validation.Issues.Select(e => $"{e.Code}: {e.Message}"));
                return McpCallToolResult.Text($"Validation échouée pour la publication du flow : {errors}", isError: true);
            }

            var saved = await flows.SaveAsync(new SaveFlowRequest(0, definition), ct);
            if (saved is null)
                return McpCallToolResult.Text("Conflit : un flow avec cet identifiant ou cette version existe déjà.", isError: true);

            return McpCallToolResult.Text(JsonSerializer.Serialize(new
            {
                message = "Flow créé et publié avec succès en version 1.",
                flow = saved
            }, JsonOptions));
        }
        else
        {
            var validation = validator.ValidateDraft(definition);
            if (!validation.IsValid)
            {
                var errors = string.Join("; ", validation.Issues.Select(e => $"{e.Code}: {e.Message}"));
                return McpCallToolResult.Text($"Validation échouée pour le brouillon du flow : {errors}", isError: true);
            }

            var draft = await flows.SaveDraftAsync(new SaveFlowDraftRequest(0, definition), ct);
            if (draft is null)
                return McpCallToolResult.Text("Impossible d'enregistrer le brouillon du flow.", isError: true);

            return McpCallToolResult.Text(JsonSerializer.Serialize(new
            {
                message = "Brouillon de flow enregistré avec succès.",
                draft
            }, JsonOptions));
        }
    }

    public async Task<McpCallToolResult> ToolCloneFlowAsync(JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin)
            return McpCallToolResult.Text("Permission refusée : le clonage de flow requiert le rôle administrateur.", isError: true);

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("sourceFlowId", out var srcProp) || !srcProp.TryGetGuid(out var sourceId)
            || !arguments.TryGetProperty("newName", out var nameProp) || nameProp.GetString() is not { Length: > 0 } newName)
        {
            return McpCallToolResult.Text("Paramètres 'sourceFlowId' (UUID) et 'newName' (chaîne) requis.", isError: true);
        }

        var cloned = await flows.CloneFlowAsync(sourceId, newName.Trim(), ct);
        if (cloned is null)
            return McpCallToolResult.Text($"Flow source introuvable avec l'ID {sourceId}.", isError: true);

        return McpCallToolResult.Text(JsonSerializer.Serialize(new
        {
            message = "Flow cloné avec succès.",
            flow = cloned
        }, JsonOptions));
    }

    public async Task<McpCallToolResult> ToolDeleteFlowAsync(JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin)
            return McpCallToolResult.Text("Permission refusée : la suppression de flow requiert le rôle administrateur.", isError: true);

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("flowId", out var idProp) || !idProp.TryGetGuid(out var flowId))
        {
            return McpCallToolResult.Text("Paramètre 'flowId' (UUID) requis.", isError: true);
        }

        var (deleteResult, reason) = await flows.DeleteFlowAsync(flowId, ct);
        return deleteResult switch
        {
            FlowDeleteResult.Deleted => McpCallToolResult.Text($"Le flow {flowId} a été supprimé avec succès."),
            FlowDeleteResult.NotFound => McpCallToolResult.Text($"Flow introuvable avec l'ID {flowId}.", isError: true),
            FlowDeleteResult.Conflict => McpCallToolResult.Text($"Impossible de supprimer le flow {flowId} : {reason ?? "conflit"}", isError: true),
            _ => McpCallToolResult.Text("Erreur inconnue lors de la suppression du flow.", isError: true)
        };
    }

    public async Task<McpCallToolResult> ToolListNodesAsync(CancellationToken ct)
    {
        var list = await nodes.ListAsync(presenceOptions.OfflineAfter, ct);
        return McpCallToolResult.Text(JsonSerializer.Serialize(list, JsonOptions));
    }

    public McpCallToolResult ToolValidateScriptDraft(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return Invalid("Objet brouillon requis.");
        var errors = new Dictionary<string, string[]>();
        var name = args.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String ? nameValue.GetString() : null;
        var language = args.TryGetProperty("language", out var languageValue) && languageValue.ValueKind == JsonValueKind.String ? languageValue.GetString() : null;
        var description = args.TryGetProperty("description", out var descriptionValue) && descriptionValue.ValueKind == JsonValueKind.String ? descriptionValue.GetString() : null;
        var code = args.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.String ? codeValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128) errors["name"] = ["Le nom est obligatoire et limité à 128 caractères."];
        if (language is not ("javascript" or "python")) errors["language"] = ["Choisissez JavaScript ou Python."];
        if (description is null || description.Length > 1000) errors["description"] = ["La description est obligatoire et limitée à 1 000 caractères."];
        if (code is null || code.Length > 200000) errors["code"] = ["Le code est obligatoire et limité à 200 000 caractères."];
        return McpCallToolResult.Text(JsonSerializer.Serialize(new { valid = errors.Count == 0, errors }, JsonOptions));
    }
}
