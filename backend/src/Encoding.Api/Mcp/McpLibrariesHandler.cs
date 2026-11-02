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

public sealed class McpLibrariesHandler(
    LibraryRepository libraries,
    LibraryWorkflowRepository libraryWorkflows,
    LibraryNodeAccessRepository libraryNodeAccess,
    LibraryProcessingRepository libraryProcessing,
    NodeRepository nodes,
    NodeGroupRepository nodeGroups)
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
        "get_libraries_overview",
        "list_libraries",
        "create_library",
        "update_library",
        "delete_library",
        "get_library_node_access",
        "get_library_workflow",
        "get_library_processing",
        "save_library_processing",
        "configure_library_node_access",
        "set_library_activation",
        "assign_library_flow",
        "request_library_discovery",
        "list_node_groups",
        "get_node_group",
        "save_node_group",
        "delete_node_group",
        "get_node_activity",
        "get_node_identity",
        "save_node_identity",
        "set_node_draining",
        "get_node_processing",
        "save_node_processing"
    ];

    public async Task<McpCallToolResult> HandleAsync(string toolName, JsonElement arguments, bool isAdmin, CancellationToken ct) =>
        toolName switch
        {
            "get_libraries_overview" => await ToolGetLibrariesOverviewAsync(ct),
            "list_libraries" => await ToolListLibrariesAsync(ct),
            "create_library" => await ToolCreateLibraryAsync(arguments, isAdmin, ct),
            "update_library" => await ToolUpdateLibraryAsync(arguments, isAdmin, ct),
            "delete_library" => await ToolDeleteLibraryAsync(arguments, isAdmin, ct),
            "get_library_node_access" => await ToolGetLibraryNodeAccessAsync(arguments, ct),
            "get_library_workflow" => await ToolGetLibraryWorkflowAsync(arguments, ct),
            "get_library_processing" => await ToolGetLibraryProcessingAsync(arguments, ct),
            "save_library_processing" => await ToolSaveLibraryProcessingAsync(arguments, isAdmin, ct),
            "configure_library_node_access" => await ToolConfigureLibraryNodeAccessAsync(arguments, isAdmin, ct),
            "set_library_activation" => await ToolSetLibraryActivationAsync(arguments, isAdmin, ct),
            "assign_library_flow" => await ToolAssignLibraryFlowAsync(arguments, isAdmin, ct),
            "request_library_discovery" => await ToolRequestLibraryDiscoveryAsync(arguments, isAdmin, ct),
            "list_node_groups" => await ToolListNodeGroupsAsync(ct),
            "get_node_group" => await ToolGetNodeGroupAsync(arguments, ct),
            "save_node_group" => await ToolSaveNodeGroupAsync(arguments, isAdmin, ct),
            "delete_node_group" => await ToolDeleteNodeGroupAsync(arguments, isAdmin, ct),
            "get_node_activity" => await ToolGetNodeActivityAsync(arguments, ct),
            "get_node_identity" => await ToolGetNodeIdentityAsync(arguments, ct),
            "save_node_identity" => await ToolSaveNodeIdentityAsync(arguments, isAdmin, ct),
            "set_node_draining" => await ToolSetNodeDrainingAsync(arguments, isAdmin, ct),
            "get_node_processing" => await ToolGetNodeProcessingAsync(arguments, ct),
            "save_node_processing" => await ToolSaveNodeProcessingAsync(arguments, isAdmin, ct),
            _ => McpCallToolResult.Text($"Outil introuvable : '{toolName}'", isError: true)
        };

    public async Task<McpCallToolResult> ToolGetLibrariesOverviewAsync(CancellationToken ct)
    {
        var overview = await libraries.OverviewAsync(ct);
        return McpCallToolResult.Text(JsonSerializer.Serialize(overview, JsonOptions));
    }

    public async Task<McpCallToolResult> ToolListLibrariesAsync(CancellationToken ct) =>
        McpCallToolResult.Text(JsonSerializer.Serialize(await libraries.ListAsync(ct), JsonOptions));

    public async Task<McpCallToolResult> ToolCreateLibraryAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La création d’une bibliothèque exige le rôle administrateur.");
        if (!args.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(name.GetString()) || name.GetString()!.Trim().Length > 128)
            return Invalid("Le nom de bibliothèque est obligatoire et limité à 128 caractères.");
        var created = await libraries.CreateAsync(new(name.GetString()!.Trim()), ct);
        return created is null ? Invalid("Une bibliothèque porte déjà ce nom.") : McpCallToolResult.Text(JsonSerializer.Serialize(created, JsonOptions));
    }

    public async Task<McpCallToolResult> ToolUpdateLibraryAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La modification d’une bibliothèque exige le rôle administrateur.");
        if (!TryGuid(args, "libraryId", out var id) || !args.TryGetProperty("name", out var name)
            || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()) || name.GetString()!.Trim().Length > 128)
            return Invalid("Paramètres 'libraryId' et 'name' invalides.");
        try
        {
            var updated = await libraries.UpdateAsync(id, new(name.GetString()!.Trim()), ct);
            return updated is null ? Invalid("Bibliothèque introuvable ou nom déjà utilisé.") : McpCallToolResult.Text(JsonSerializer.Serialize(updated, JsonOptions));
        }
        catch (System.Data.Common.DbException exception) when (Encoding.Api.Infrastructure.Data.Sql.IsUniqueViolation(exception))
        {
            return Invalid("Une bibliothèque porte déjà ce nom.");
        }
    }

    public async Task<McpCallToolResult> ToolDeleteLibraryAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La suppression d’une bibliothèque exige le rôle administrateur.");
        if (!TryGuid(args, "libraryId", out var id)) return Invalid("Paramètre 'libraryId' (UUID) requis.");
        return await libraries.DeleteAsync(id, ct) ? McpCallToolResult.Text("Définition de bibliothèque supprimée; les fichiers média ne sont pas touchés.") : Invalid("Bibliothèque introuvable.");
    }

    public async Task<McpCallToolResult> ToolGetLibraryNodeAccessAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryGuid(args, "libraryId", out var id)) return Invalid("Paramètre 'libraryId' (UUID) requis.");
        var access = await libraryNodeAccess.ListAsync(id, ct);
        return access is null ? Invalid("Bibliothèque introuvable.") : McpCallToolResult.Text(JsonSerializer.Serialize(access, JsonOptions));
    }

    public async Task<McpCallToolResult> ToolGetLibraryWorkflowAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryGuid(args, "libraryId", out var id)) return Invalid("Paramètre 'libraryId' (UUID) requis.");
        if (!(await libraries.ListAsync(ct)).Any(library => library.Id == id)) return Invalid("Bibliothèque introuvable.");
        var search = args.TryGetProperty("search", out var searchValue) && searchValue.ValueKind == JsonValueKind.String ? searchValue.GetString() : null;
        var limit = args.TryGetProperty("limit", out var limitValue) && limitValue.TryGetInt32(out var l) ? Math.Clamp(l, 1, 10000) : 1000;
        var offset = args.TryGetProperty("offset", out var offsetValue) && offsetValue.TryGetInt32(out var o) ? Math.Max(0, o) : 0;
        var result = await libraryWorkflows.GetAsync(id, search, limit, offset, ct);
        return McpCallToolResult.Text(JsonSerializer.Serialize(result, JsonOptions));
    }

    public async Task<McpCallToolResult> ToolGetLibraryProcessingAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryGuid(args, "libraryId", out var id)) return Invalid("Paramètre 'libraryId' (UUID) requis.");
        if (!(await libraries.ListAsync(ct)).Any(library => library.Id == id)) return Invalid("Bibliothèque introuvable.");
        return McpCallToolResult.Text(JsonSerializer.Serialize(await libraryProcessing.GetAsync(id, ct), JsonOptions));
    }

    public async Task<McpCallToolResult> ToolSaveLibraryProcessingAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La configuration du traitement d’une bibliothèque exige le rôle administrateur.");
        if (!TryGuid(args, "libraryId", out var id) || !TryDeserialize<Encoding.Contracts.Libraries.SaveLibraryProcessingSettings>(args, out var request))
            return Invalid("Paramètres de surveillance de bibliothèque invalides.");
        try
        {
            var saved = await libraryProcessing.SaveAsync(id, request, ct);
            return saved ? McpCallToolResult.Text(JsonSerializer.Serialize(await libraryProcessing.GetAsync(id, ct),
                JsonOptions)) : Invalid("La bibliothèque ou sa révision de configuration a changé.");
        }
        catch (ArgumentException exception) { return Invalid(exception.Message); }
    }

    public async Task<McpCallToolResult> ToolConfigureLibraryNodeAccessAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La configuration des accès exige le rôle administrateur.");
        if (!TryGuid(args, "libraryId", out var id) || !TryDeserialize<Encoding.Contracts.Libraries.ConfigureLibraryNodeAccessRequest>(args, out var request))
            return Invalid("Paramètres 'libraryId' et 'nodes' invalides.");
        try
        {
            var result = await libraryNodeAccess.ConfigureAsync(id, request, ct);
            return result is null ? Invalid("Bibliothèque introuvable.") : McpCallToolResult.Text(JsonSerializer.Serialize(result, JsonOptions));
        }
        catch (LibraryNodeAccessValidationException exception)
        {
            return Invalid(JsonSerializer.Serialize(exception.Errors, JsonOptions));
        }
    }

    public async Task<McpCallToolResult> ToolSetLibraryActivationAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La modification d’une bibliothèque exige le rôle administrateur.");
        if (!TryGuid(args, "libraryId", out var id) || !args.TryGetProperty("isActive", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return Invalid("Paramètres 'libraryId' et 'isActive' requis.");
        var result = await libraries.SetActiveAsync(id, enabled.GetBoolean(), ct);
        return result is null ? Invalid("Bibliothèque introuvable.") : McpCallToolResult.Text(JsonSerializer.Serialize(result, JsonOptions));
    }

    public async Task<McpCallToolResult> ToolAssignLibraryFlowAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("L’affectation d’un flow exige le rôle administrateur.");
        if (!TryGuid(args, "libraryId", out var id) || !TryInt(args, "expectedRevision", out var revision) || revision < 0)
            return Invalid("Paramètres 'libraryId' et 'expectedRevision' invalides.");
        Guid? flowId = null;
        int? version = null;
        if (args.TryGetProperty("flowId", out var flowValue) && flowValue.ValueKind != JsonValueKind.Null)
        {
            if (!Guid.TryParse(flowValue.GetString(), out var parsedFlow) || parsedFlow == Guid.Empty || !TryInt(args, "version", out var parsedVersion) || parsedVersion < 1)
                return Invalid("Une affectation exige un 'flowId' et une version publiée valide.");
            flowId = parsedFlow;
            version = parsedVersion;
        }
        else if (args.TryGetProperty("version", out _))
        {
            return Invalid("Le paramètre 'version' nécessite un 'flowId'.");
        }
        var updated = await libraryWorkflows.AssignAsync(id, new(flowId, version, revision), ct);
        return updated ? McpCallToolResult.Text(JsonSerializer.Serialize(await libraryWorkflows.GetAsync(id, ct),
            JsonOptions)) : Invalid("L’affectation a changé ou la version du flow n’est pas publiable.");
    }

    public async Task<McpCallToolResult> ToolRequestLibraryDiscoveryAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La découverte de fichiers exige le rôle administrateur.");
        if (!TryGuid(args, "libraryId", out var id)) return Invalid("Paramètre 'libraryId' (UUID) requis.");
        var scan = await libraryWorkflows.RequestScanAsync(id, ct);
        return scan is null ? Invalid("Activez la bibliothèque et affectez-lui un flow publié avant la découverte.") : McpCallToolResult.Text(JsonSerializer.Serialize(new { scanId = scan },
            JsonOptions));
    }

    public async Task<McpCallToolResult> ToolListNodeGroupsAsync(CancellationToken ct) =>
        McpCallToolResult.Text(JsonSerializer.Serialize(await nodeGroups.ListAsync(ct), JsonOptions));

    public async Task<McpCallToolResult> ToolGetNodeGroupAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryGuid(args, "id", out var id)) return Invalid("Paramètre 'id' (UUID) requis.");
        var group = await nodeGroups.GetAsync(id, ct);
        return group is null ? Invalid("Groupe de nœuds introuvable.") : McpCallToolResult.Text(JsonSerializer.Serialize(group, JsonOptions));
    }

    public async Task<McpCallToolResult> ToolSaveNodeGroupAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La modification des groupes exige le rôle administrateur.");
        if (!TryDeserialize<Encoding.Contracts.Nodes.SaveNodeGroupRequest>(args, out var request)) return Invalid("Définition de groupe invalide.");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128
            || request.Description is null || request.Description.Length > 2000
            || request.Criteria is null || request.Labels is null || request.NodeIds is null || request.NodeIds.Count > 2048
            || request.Labels.Count > 128 || !Enum.IsDefined(request.Mode)
            || request.Mode == Encoding.Contracts.Nodes.NodeGroupMode.Automatic && request.NodeIds.Count > 0)
            return Invalid("Les champs du groupe ou ses limites sont invalides.");
        Guid? id = null;
        if (args.TryGetProperty("id", out var idValue))
        {
            if (idValue.ValueKind != JsonValueKind.String || !Guid.TryParse(idValue.GetString(), out var parsed) || parsed == Guid.Empty)
                return Invalid("Le paramètre 'id' doit être un UUID valide.");
            id = parsed;
        }
        try
        {
            var saved = await nodeGroups.SaveAsync(id, request, ct);
            if (!saved) return Invalid("Groupe introuvable ou paramètres invalides.");
            var group = id is { } groupId
                ? await nodeGroups.GetAsync(groupId, ct)
                : (await nodeGroups.ListAsync(ct)).FirstOrDefault(item => item.Name == request.Name.Trim());
            return McpCallToolResult.Text(JsonSerializer.Serialize(group, JsonOptions));
        }
        catch (System.Data.Common.DbException ex) when (Encoding.Api.Infrastructure.Data.Sql.IsUniqueViolation(ex))
        {
            return Invalid("Un groupe porte déjà ce nom.");
        }
    }

    public async Task<McpCallToolResult> ToolDeleteNodeGroupAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La suppression des groupes exige le rôle administrateur.");
        if (!TryGuid(args, "id", out var id)) return Invalid("Paramètre 'id' (UUID) requis.");
        return await nodeGroups.DeleteAsync(id, ct) switch
        {
            "deleted" => McpCallToolResult.Text("Groupe supprimé."),
            "in-use" => Invalid("Le groupe est référencé par un flow et ne peut pas être supprimé."),
            _ => Invalid("Groupe de nœuds introuvable.")
        };
    }

    public async Task<McpCallToolResult> ToolGetNodeActivityAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryGuid(args, "nodeId", out var id)) return Invalid("Paramètre 'nodeId' (UUID) requis.");
        var activity = await nodes.ActivityAsync(id, ct);
        return McpCallToolResult.Text(JsonSerializer.Serialize(activity, JsonOptions));
    }

    public async Task<McpCallToolResult> ToolGetNodeIdentityAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryGuid(args, "nodeId", out var id)) return Invalid("Paramètre 'nodeId' (UUID) requis.");
        var identity = await nodeGroups.GetNodeIdentityAsync(id, ct);
        return identity is null ? Invalid("Nœud introuvable.") : McpCallToolResult.Text(JsonSerializer.Serialize(identity, JsonOptions));
    }

    public async Task<McpCallToolResult> ToolSaveNodeIdentityAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La modification de l’identité d’un nœud exige le rôle administrateur.");
        if (!TryGuid(args, "nodeId", out var id) || !TryDeserialize<Encoding.Contracts.Nodes.SaveNodeIdentityRequest>(args, out var request))
            return Invalid("Paramètres d’identité de nœud invalides.");
        if (request.Labels is null || request.Labels.Count > 128 || request.DisplayName?.Length > 128
            || request.Labels.Any(pair => !System.Text.RegularExpressions.Regex.IsMatch(pair.Key, "^[a-zA-Z][a-zA-Z0-9_.-]{0,63}$") || pair.Value?.Length > 256))
            return Invalid("Le nom ou les étiquettes du nœud sont invalides.");
        var saved = await nodeGroups.SaveNodeIdentityAsync(id, request, ct);
        return saved ? McpCallToolResult.Text(JsonSerializer.Serialize(await nodeGroups.GetNodeIdentityAsync(id, ct), JsonOptions)) : Invalid("Nœud introuvable.");
    }

    public async Task<McpCallToolResult> ToolSetNodeDrainingAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La vidange d’un nœud exige le rôle administrateur.");
        if (!TryGuid(args, "nodeId", out var id) || !args.TryGetProperty("draining", out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Invalid("Paramètres 'nodeId' et 'draining' invalides.");
        return await nodes.SetDrainingAsync(id, value.GetBoolean(), ct) ? McpCallToolResult.Text("État de vidange mis à jour.") : Invalid("Nœud introuvable.");
    }

    public async Task<McpCallToolResult> ToolGetNodeProcessingAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryGuid(args, "nodeId", out var id)) return Invalid("Paramètre 'nodeId' (UUID) requis.");
        var settings = await libraryProcessing.NodeAsync(id, ct);
        return settings is null ? Invalid("Nœud introuvable.") : McpCallToolResult.Text(JsonSerializer.Serialize(settings, JsonOptions));
    }

    public async Task<McpCallToolResult> ToolSaveNodeProcessingAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La configuration de capacité d’un nœud exige le rôle administrateur.");
        if (!TryGuid(args, "nodeId", out var id) || !TryDeserialize<Encoding.Contracts.Nodes.SaveNodeProcessingSettings>(args, out var request))
            return Invalid("Paramètres de capacité du nœud invalides.");
        try
        {
            var saved = await libraryProcessing.SaveNodeAsync(id, request, ct);
            return saved ? McpCallToolResult.Text(JsonSerializer.Serialize(await libraryProcessing.NodeAsync(id, ct), JsonOptions)) : Invalid("Le nœud ou sa révision de configuration a changé.");
        }
        catch (ArgumentException exception) { return Invalid(exception.Message); }
    }
}
