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

public sealed class McpAuthoringHandler(
    ExecutionRepository executions,
    FlowScriptRepository scripts,
    ResourceRepository resources,
    EncodingProfileRepository profiles,
    FlowTemplateRepository templates,
    FlowComponentPackRepository componentPacks)
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
        "list_executions",
        "list_scripts",
        "get_script",
        "save_script",
        "publish_script",
        "delete_script",
        "list_resources",
        "get_resource",
        "create_resource",
        "update_resource",
        "add_resource_version",
        "delete_resource",
        "list_encoding_profiles",
        "get_encoding_profile",
        "save_encoding_profile",
        "publish_encoding_profile",
        "list_templates",
        "get_template",
        "create_template_from_flow",
        "list_component_packs",
        "get_component_pack",
        "create_component_pack_from_flow"
    ];

    public async Task<McpCallToolResult> HandleAsync(string toolName, JsonElement arguments, bool isAdmin, CancellationToken ct) =>
        toolName switch
        {
            "list_executions" => await ToolListExecutionsAsync(arguments, ct),
            "list_scripts" => await ToolListScriptsAsync(ct),
            "get_script" => await ToolGetScriptAsync(arguments, ct),
            "save_script" => await ToolSaveScriptAsync(arguments, isAdmin, ct),
            "publish_script" => await ToolPublishScriptAsync(arguments, isAdmin, ct),
            "delete_script" => await ToolDeleteScriptAsync(arguments, isAdmin, ct),
            "list_resources" => await ToolListResourcesAsync(ct),
            "get_resource" => await ToolGetResourceAsync(arguments, ct),
            "create_resource" => await ToolCreateResourceAsync(arguments, isAdmin, ct),
            "update_resource" => await ToolUpdateResourceAsync(arguments, isAdmin, ct),
            "add_resource_version" => await ToolAddResourceVersionAsync(arguments, isAdmin, ct),
            "delete_resource" => await ToolDeleteResourceAsync(arguments, isAdmin, ct),
            "list_encoding_profiles" => await ToolListEncodingProfilesAsync(ct),
            "get_encoding_profile" => await ToolGetEncodingProfileAsync(arguments, ct),
            "save_encoding_profile" => await ToolSaveEncodingProfileAsync(arguments, isAdmin, ct),
            "publish_encoding_profile" => await ToolPublishEncodingProfileAsync(arguments, isAdmin, ct),
            "list_templates" => await ToolListTemplatesAsync(ct),
            "get_template" => await ToolGetTemplateAsync(arguments, ct),
            "create_template_from_flow" => await ToolCreateTemplateFromFlowAsync(arguments, isAdmin, ct),
            "list_component_packs" => await ToolListComponentPacksAsync(ct),
            "get_component_pack" => await ToolGetComponentPackAsync(arguments, ct),
            "create_component_pack_from_flow" => await ToolCreateComponentPackFromFlowAsync(arguments, isAdmin, ct),
            _ => McpCallToolResult.Text($"Outil introuvable : '{toolName}'", isError: true)
        };

    public async Task<McpCallToolResult> ToolListExecutionsAsync(JsonElement arguments, CancellationToken ct)
    {
        string? state = null;
        var limit = 50;

        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if (arguments.TryGetProperty("state", out var stateProp) && stateProp.GetString() is { Length: > 0 } st)
                state = st.Trim();

            if (arguments.TryGetProperty("limit", out var limitProp) && limitProp.TryGetInt32(out var l) && l > 0)
                limit = Math.Clamp(l, 1, 200);
        }

        var filter = new ExecutionFilterQuery(Limit: limit, State: state);
        var overview = await executions.OverviewAsync(filter, ct);
        return McpCallToolResult.Text(JsonSerializer.Serialize(overview, JsonOptions));
    }

    // --- SCRIPTS ---

    public async Task<McpCallToolResult> ToolListScriptsAsync(CancellationToken ct)
    {
        var list = await scripts.ListAsync(ct);
        return McpCallToolResult.Text(JsonSerializer.Serialize(list, JsonOptions));
    }

    public async Task<McpCallToolResult> ToolGetScriptAsync(JsonElement arguments, CancellationToken ct)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("id", out var idProp) || !idProp.TryGetGuid(out var id))
            return McpCallToolResult.Text("Paramètre 'id' (UUID) requis.", isError: true);

        var script = await scripts.GetAsync(id, ct);
        return script is not null
            ? McpCallToolResult.Text(JsonSerializer.Serialize(script, JsonOptions))
            : McpCallToolResult.Text($"Script introuvable avec l'ID {id}.", isError: true);
    }

    public async Task<McpCallToolResult> ToolSaveScriptAsync(JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin)
            return McpCallToolResult.Text("Permission refusée : la modification de scripts requiert le rôle administrateur.", isError: true);

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("name", out var nameProp) || nameProp.GetString() is not { Length: > 0 } name
            || !arguments.TryGetProperty("language", out var langProp) || langProp.GetString() is not { Length: > 0 } lang
            || !arguments.TryGetProperty("code", out var codeProp) || codeProp.GetString() is not { } code)
        {
            return McpCallToolResult.Text("Paramètres 'name', 'language' (javascript|python) et 'code' requis.", isError: true);
        }

        var desc = arguments.TryGetProperty("description", out var dProp) ? dProp.GetString() ?? "" : "";
        var draft = new FlowScriptDraft(name.Trim(), lang.Trim().ToLowerInvariant(), desc.Trim(), code);

        Guid scriptId;
        if (arguments.TryGetProperty("id", out var idProp) && idProp.TryGetGuid(out var existingId))
        {
            scriptId = existingId;
            var existing = await scripts.GetAsync(scriptId, ct);
            if (existing is not null)
            {
                var expectedRev = arguments.TryGetProperty("expectedRevision", out var revProp) && revProp.TryGetInt32(out var r) ? r : existing.Script.DraftRevision;
                var saved = await scripts.SaveDraftAsync(scriptId, new SaveFlowScriptDraftRequest(expectedRev, draft), ct);
                return saved is not null
                    ? McpCallToolResult.Text(JsonSerializer.Serialize(new { message = "Brouillon de script mis à jour.", script = saved }, JsonOptions))
                    : McpCallToolResult.Text("Conflit de révision ou script introuvable.", isError: true);
            }
            else
            {
                var created = await scripts.CreateAsync(scriptId, draft, ct);
                return McpCallToolResult.Text(JsonSerializer.Serialize(new { message = "Script créé avec succès.", script = created }, JsonOptions));
            }
        }
        else
        {
            scriptId = Guid.NewGuid();
            var created = await scripts.CreateAsync(scriptId, draft, ct);
            return McpCallToolResult.Text(JsonSerializer.Serialize(new { message = "Script créé avec succès.", script = created }, JsonOptions));
        }
    }

    public async Task<McpCallToolResult> ToolPublishScriptAsync(JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin)
            return McpCallToolResult.Text("Permission refusée : la publication de scripts requiert le rôle administrateur.", isError: true);

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("id", out var idProp) || !idProp.TryGetGuid(out var id)
            || !arguments.TryGetProperty("expectedRevision", out var revProp) || !revProp.TryGetInt32(out var rev))
        {
            return McpCallToolResult.Text("Paramètres 'id' (UUID) et 'expectedRevision' (entier) requis.", isError: true);
        }

        var published = await scripts.PublishAsync(id, rev, ct);
        return published is not null
            ? McpCallToolResult.Text(JsonSerializer.Serialize(new { message = $"Script publié en version {published.Version}.", version = published }, JsonOptions))
            : McpCallToolResult.Text("Échec de publication : script introuvable, code vide ou révision conflictuelle.", isError: true);
    }

    public async Task<McpCallToolResult> ToolDeleteScriptAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La suppression de scripts exige le rôle administrateur.");
        if (!TryGuid(args, "id", out var id)) return Invalid("Paramètre 'id' (UUID) requis.");
        return await scripts.DeleteAsync(id, ct) ? McpCallToolResult.Text("Script supprimé.") : Invalid("Script introuvable.");
    }

    public async Task<McpCallToolResult> ToolListResourcesAsync(CancellationToken ct) =>
        McpCallToolResult.Text(JsonSerializer.Serialize(await resources.ListAsync(ct), JsonOptions));

    public async Task<McpCallToolResult> ToolGetResourceAsync(JsonElement args, CancellationToken ct)
    {
        if (!TryGuid(args, "id", out var id)) return Invalid("Paramètre 'id' (UUID) requis.");
        var resource = await resources.GetAsync(id, ct);
        return resource is null ? Invalid("Ressource introuvable.") : McpCallToolResult.Text(JsonSerializer.Serialize(resource, JsonOptions));
    }

    public async Task<McpCallToolResult> ToolCreateResourceAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La création de ressources exige le rôle administrateur.");
        if (args.GetProperty("name").GetString() is not { } name || string.IsNullOrWhiteSpace(name)
            || !TryResourceFiles(args, out var files)) return Invalid("Nom ou liste de fichiers invalide.");
        var description = args.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        try
        {
            var created = await resources.CreateAsync(Guid.NewGuid(), "font", name, description, files, ct);
            return McpCallToolResult.Text(JsonSerializer.Serialize(created, JsonOptions));
        }
        catch (ResourceException ex) { return Invalid(ex.Message); }
        catch (DbException ex) when (Sql.IsUniqueViolation(ex)) { return Invalid("Une ressource porte déjà ce nom."); }
    }

    public async Task<McpCallToolResult> ToolUpdateResourceAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La modification de ressources exige le rôle administrateur.");
        if (!TryGuid(args, "id", out var id) || args.GetProperty("name").GetString() is not { } name
            || args.GetProperty("description").GetString() is not { } description) return Invalid("Paramètres de ressource invalides.");
        try
        {
            var updated = await resources.UpdateAsync(id, name, description, ct);
            return updated is null ? Invalid("Ressource introuvable.") : McpCallToolResult.Text(JsonSerializer.Serialize(updated, JsonOptions));
        }
        catch (DbException ex) when (Sql.IsUniqueViolation(ex)) { return Invalid("Une ressource porte déjà ce nom."); }
    }

    public async Task<McpCallToolResult> ToolAddResourceVersionAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La création d’une version exige le rôle administrateur.");
        if (!TryGuid(args, "id", out var id) || !TryResourceFiles(args, out var files)) return Invalid("Identifiant ou liste de fichiers invalide.");
        try
        {
            var version = await resources.AddVersionAsync(id, files, ct);
            return version is null ? Invalid("Ressource introuvable.") : McpCallToolResult.Text(JsonSerializer.Serialize(version, JsonOptions));
        }
        catch (ResourceException ex) { return Invalid(ex.Message); }
    }

    public async Task<McpCallToolResult> ToolDeleteResourceAsync(JsonElement args, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin) return Denied("La suppression de ressources exige le rôle administrateur.");
        if (!TryGuid(args, "id", out var id)) return Invalid("Paramètre 'id' (UUID) requis.");
        var usage = await resources.UsageAsync(id, ct);
        if (usage.Count > 0) return Invalid("Cette ressource est utilisée par un flow et ne peut pas être supprimée.");
        return await resources.DeleteAsync(id, ct) ? McpCallToolResult.Text("Ressource supprimée.") : Invalid("Ressource introuvable.");
    }

    private static bool TryResourceFiles(JsonElement args, out IReadOnlyList<ResourceFileReference> files)
    {
        files = [];
        if (!args.TryGetProperty("files", out var value) || value.ValueKind != JsonValueKind.Array) return false;
        try
        {
            files = JsonSerializer.Deserialize<IReadOnlyList<ResourceFileReference>>(value.GetRawText(), JsonOptions) ?? [];
            return files.Count > 0;
        }
        catch (JsonException) { return false; }
    }

    // --- PROFILS D'ENCODAGE ---

    public async Task<McpCallToolResult> ToolListEncodingProfilesAsync(CancellationToken ct)
    {
        var list = await profiles.ListAsync(ct);
        return McpCallToolResult.Text(JsonSerializer.Serialize(list, JsonOptions));
    }

    public async Task<McpCallToolResult> ToolGetEncodingProfileAsync(JsonElement arguments, CancellationToken ct)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("id", out var idProp) || !idProp.TryGetGuid(out var id))
            return McpCallToolResult.Text("Paramètre 'id' (UUID) requis.", isError: true);

        var profile = await profiles.GetAsync(id, ct);
        return profile is not null
            ? McpCallToolResult.Text(JsonSerializer.Serialize(profile, JsonOptions))
            : McpCallToolResult.Text($"Profil d'encodage introuvable avec l'ID {id}.", isError: true);
    }

    public async Task<McpCallToolResult> ToolSaveEncodingProfileAsync(JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin)
            return McpCallToolResult.Text("Permission refusée : la modification de profils requiert le rôle administrateur.", isError: true);

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("name", out var nameProp) || nameProp.GetString() is not { Length: > 0 } name
            || !arguments.TryGetProperty("targets", out var targetsProp) || targetsProp.ValueKind != JsonValueKind.Array)
        {
            return McpCallToolResult.Text("Paramètres 'name' et 'targets' (tableau de cibles d'encodage) requis.", isError: true);
        }

        var desc = arguments.TryGetProperty("description", out var dProp) ? dProp.GetString() ?? "" : "";
        var targets = new List<EncodingProfileTarget>();
        foreach (var t in targetsProp.EnumerateArray())
        {
            var encId = t.TryGetProperty("encoderId", out var ep) ? ep.GetString() ?? "" : "";
            var q = t.TryGetProperty("quality", out var qp) && qp.TryGetDecimal(out var qVal) ? qVal : 22m;
            var preset = t.TryGetProperty("preset", out var pp) ? pp.GetString() ?? "medium" : "medium";
            var priority = t.TryGetProperty("priority", out var prioProp) && prioProp.TryGetInt32(out var prio) ? prio : 10;
            targets.Add(new EncodingProfileTarget(encId, q, preset, priority));
        }

        var draft = new EncodingProfileDraft(name.Trim(), desc.Trim(), targets);

        Guid profileId;
        if (arguments.TryGetProperty("id", out var idProp) && idProp.TryGetGuid(out var existingId))
        {
            profileId = existingId;
            var existing = await profiles.GetAsync(profileId, ct);
            if (existing is not null)
            {
                var expectedRev = arguments.TryGetProperty("expectedRevision", out var revProp) && revProp.TryGetInt32(out var r) ? r : existing.Profile.DraftRevision;
                var saved = await profiles.SaveDraftAsync(profileId, new SaveEncodingProfileDraftRequest(expectedRev, draft), ct);
                return saved is not null
                    ? McpCallToolResult.Text(JsonSerializer.Serialize(new { message = "Brouillon de profil d'encodage mis à jour.", profile = saved }, JsonOptions))
                    : McpCallToolResult.Text("Conflit de révision ou profil introuvable / intégré.", isError: true);
            }
            else
            {
                var created = await profiles.CreateAsync(profileId, draft, ct);
                return McpCallToolResult.Text(JsonSerializer.Serialize(new { message = "Profil d'encodage créé.", profile = created }, JsonOptions));
            }
        }
        else
        {
            profileId = Guid.NewGuid();
            var created = await profiles.CreateAsync(profileId, draft, ct);
            return McpCallToolResult.Text(JsonSerializer.Serialize(new { message = "Profil d'encodage créé.", profile = created }, JsonOptions));
        }
    }

    public async Task<McpCallToolResult> ToolPublishEncodingProfileAsync(JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin)
            return McpCallToolResult.Text("Permission refusée : la publication de profils requiert le rôle administrateur.", isError: true);

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("id", out var idProp) || !idProp.TryGetGuid(out var id)
            || !arguments.TryGetProperty("expectedRevision", out var revProp) || !revProp.TryGetInt32(out var rev))
        {
            return McpCallToolResult.Text("Paramètres 'id' (UUID) et 'expectedRevision' (entier) requis.", isError: true);
        }

        var published = await profiles.PublishAsync(id, rev, ct);
        return published is not null
            ? McpCallToolResult.Text(JsonSerializer.Serialize(new { message = $"Profil publié en version {published.Version}.", version = published }, JsonOptions))
            : McpCallToolResult.Text("Échec de publication : profil introuvable ou révision conflictuelle.", isError: true);
    }

    // --- MODÈLES (TEMPLATES) ---

    public async Task<McpCallToolResult> ToolListTemplatesAsync(CancellationToken ct)
    {
        var list = await templates.ListAsync(ct);
        return McpCallToolResult.Text(JsonSerializer.Serialize(list, JsonOptions));
    }

    public async Task<McpCallToolResult> ToolGetTemplateAsync(JsonElement arguments, CancellationToken ct)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("id", out var idProp) || !idProp.TryGetGuid(out var id))
            return McpCallToolResult.Text("Paramètre 'id' (UUID) requis.", isError: true);

        var template = await templates.GetAsync(id, ct);
        return template is not null
            ? McpCallToolResult.Text(JsonSerializer.Serialize(template, JsonOptions))
            : McpCallToolResult.Text($"Modèle introuvable avec l'ID {id}.", isError: true);
    }

    public async Task<McpCallToolResult> ToolCreateTemplateFromFlowAsync(JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin)
            return McpCallToolResult.Text("Permission refusée : la création de modèles requiert le rôle administrateur.", isError: true);

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("sourceFlowId", out var srcProp) || !srcProp.TryGetGuid(out var srcId)
            || !arguments.TryGetProperty("sourceVersion", out var verProp) || !verProp.TryGetInt32(out var ver)
            || !arguments.TryGetProperty("name", out var nameProp) || nameProp.GetString() is not { Length: > 0 } name)
        {
            return McpCallToolResult.Text("Paramètres 'sourceFlowId' (UUID), 'sourceVersion' (entier) et 'name' requis.", isError: true);
        }

        var desc = arguments.TryGetProperty("description", out var dProp) ? dProp.GetString() ?? "" : "";
        var request = new CreateFlowTemplateFromFlowRequest(srcId, ver, name.Trim(), desc.Trim());
        var created = await templates.CreateFromFlowAsync(Guid.NewGuid(), request, ct);

        return created is not null
            ? McpCallToolResult.Text(JsonSerializer.Serialize(new { message = "Modèle créé à partir du flow avec succès.", template = created }, JsonOptions))
            : McpCallToolResult.Text("Flow source ou version publiée introuvable.", isError: true);
    }

    // --- PACKS DE COMPOSANTS ---

    public async Task<McpCallToolResult> ToolListComponentPacksAsync(CancellationToken ct)
    {
        var list = await componentPacks.ListAsync(ct);
        return McpCallToolResult.Text(JsonSerializer.Serialize(list, JsonOptions));
    }

    public async Task<McpCallToolResult> ToolGetComponentPackAsync(JsonElement arguments, CancellationToken ct)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("id", out var idProp) || !idProp.TryGetGuid(out var id))
            return McpCallToolResult.Text("Paramètre 'id' (UUID) requis.", isError: true);

        var pack = await componentPacks.GetAsync(id, ct);
        return pack is not null
            ? McpCallToolResult.Text(JsonSerializer.Serialize(pack, JsonOptions))
            : McpCallToolResult.Text($"Pack de composants introuvable avec l'ID {id}.", isError: true);
    }

    public async Task<McpCallToolResult> ToolCreateComponentPackFromFlowAsync(JsonElement arguments, bool isAdmin, CancellationToken ct)
    {
        if (!isAdmin)
            return McpCallToolResult.Text("Permission refusée : la création de packs requiert le rôle administrateur.", isError: true);

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("sourceFlowId", out var srcProp) || !srcProp.TryGetGuid(out var srcId)
            || !arguments.TryGetProperty("sourceVersion", out var verProp) || !verProp.TryGetInt32(out var ver)
            || !arguments.TryGetProperty("name", out var nameProp) || nameProp.GetString() is not { Length: > 0 } name)
        {
            return McpCallToolResult.Text("Paramètres 'sourceFlowId' (UUID), 'sourceVersion' (entier) et 'name' requis.", isError: true);
        }

        var desc = arguments.TryGetProperty("description", out var dProp) ? dProp.GetString() ?? "" : "";
        var request = new CreateFlowComponentPackRequest(srcId, ver, name.Trim(), desc.Trim());
        var created = await componentPacks.CreateFromFlowAsync(Guid.NewGuid(), request, ct);

        return created is not null
            ? McpCallToolResult.Text(JsonSerializer.Serialize(new { message = "Pack de composants créé avec succès.", pack = created }, JsonOptions))
            : McpCallToolResult.Text("Impossible de créer le pack : le flow doit contenir une entrée reliée à au moins un composant.", isError: true);
    }

    // --- PLUGINS ---
}
