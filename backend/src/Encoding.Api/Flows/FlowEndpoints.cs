using System.Text.Json;
using Encoding.Api.Auth;
using Encoding.Api.Infrastructure;
using Encoding.Api.Plugins;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Flows;

public static class FlowEndpoints
{
    public static void MapFlowEndpoints(this WebApplication app)
    {
        var flows = app.MapGroup("/api/v1/flows").WithTags("Flows");
        flows.MapFlowAuthoringEndpoints();
        flows.MapFlowPreflightAndValidationEndpoints();
        flows.MapFlowDraftEndpoints();
        flows.MapFlowLifecycleEndpoints();
        flows.MapFlowCatalogEndpoints();
        flows.MapFlowArchiveEndpoints();
    }

    private static void MapFlowAuthoringEndpoints(this RouteGroupBuilder flows)
    {
        flows.MapPost("/guided", (GuidedFlowRequest request, FlowComponentRegistry registry, FlowDefinitionValidator validator) =>
        {
            try
            {
                var definition = GuidedFlowBuilder.Build(request, registry);
                var validation = validator.Validate(definition);
                return validation.IsValid ? (IResult)Results.Ok(definition) : Results.UnprocessableEntity(validation);
            }
            catch (ArgumentException exception) { return ApiProblem.BadRequest(exception.Message); }
        }).WithName("GenerateGuidedFlow");

        flows.MapPost("/validate", (JsonElement document, FlowDefinitionValidator validator) =>
        {
            var (_, result) = validator.ValidateDocument(document);
            return result.IsValid ? (IResult)Results.Ok(result) : Results.UnprocessableEntity(result);
        }).AllowViewer().WithName("ValidateFlowDefinition");

        flows.MapGet("/components", (FlowComponentRegistry registry, ClusterExtensionActivationState activation) => Results.Ok(
            registry.Plugins.Select(plugin => new
            {
                plugin.ContractVersion,
                plugin.Id,
                plugin.Version,
                plugin.Name,
                plugin.SupportedHostContractVersions,
                plugin.Components,
                plugin.ConfigurationSchema,
                plugin.PublicationGuard,
                enabled = activation.IsPluginEnabled(plugin.Id)
            })))
            .WithName("GetFlowComponentCatalog");

        flows.MapGet("/schema", (FlowComponentRegistry registry, ClusterExtensionActivationState activation) =>
            Results.Ok(BuildFlowSchema(registry, activation)))
            .AllowViewer().WithName("GetFlowSchema");
    }

    private static void MapFlowPreflightAndValidationEndpoints(this RouteGroupBuilder flows)
    {
        flows.MapGet("/{id:guid}/preflight", async (Guid id, FlowCoordinatorService coordinator,
            Encoding.Api.Nodes.NodePresenceOptions presence, CancellationToken ct) =>
            await coordinator.PreflightAsync(id, presence.OfflineAfter, ct) is { } result
                ? Results.Ok(result)
                : ApiProblem.NotFound("Flow introuvable."))
            .AllowViewer().WithName("PreflightFlowExecution");
    }

    private static void MapFlowDraftEndpoints(this RouteGroupBuilder flows)
    {
        flows.MapGet("/{id:guid}/edit", async (Guid id, FlowCoordinatorService coordinator, CancellationToken ct) =>
            await coordinator.GetFlowForEditAsync(id, ct) is { } flow ? Results.Ok(flow) : ApiProblem.NotFound())
            .WithName("GetFlowForEdit");

        flows.MapPut("/{id:guid}/draft", async (Guid id, SaveFlowDraftRequest request,
            FlowCoordinatorService coordinator, CancellationToken ct) =>
        {
            var result = await coordinator.SaveDraftAsync(request, ct);
            if (!result.IsValid) return (IResult)Results.UnprocessableEntity(result.Validation);
            return result.Saved is { } saved
                ? Results.Ok(saved)
                : ApiProblem.Conflict("Le brouillon a été modifié ailleurs.");
        }).AddEndpointFilter<ValidateFlowDraftFilter>().WithName("SaveFlowDraft");

        flows.MapDelete("/{id:guid}/draft", async (Guid id, FlowCoordinatorService coordinator, CancellationToken ct) =>
            await coordinator.DeleteDraftAsync(id, ct) ? Results.NoContent() : ApiProblem.NotFound("Aucun brouillon pour ce flow."))
            .WithName("DeleteFlowDraft");

        flows.MapGet("/{id:guid}/draft-backups", async (Guid id, FlowCoordinatorService coordinator, CancellationToken ct) =>
            Results.Ok(await coordinator.DraftBackupsAsync(id, ct))).WithName("ListFlowDraftBackups");

        flows.MapGet("/{id:guid}/draft-backups/{revision:int}", async (Guid id, int revision,
            FlowCoordinatorService coordinator, CancellationToken ct) =>
            await coordinator.DraftBackupAsync(id, revision, ct) is { } saved ? (IResult)Results.Ok(saved) : ApiProblem.NotFound())
            .WithName("GetFlowDraftBackup");
    }

    private static void MapFlowLifecycleEndpoints(this RouteGroupBuilder flows)
    {
        flows.MapGet("", async (FlowCoordinatorService coordinator, CancellationToken ct) =>
            Results.Ok(await coordinator.ListAsync(ct))).WithName("ListFlows");

        flows.MapGet("/{id:guid}", async (Guid id, FlowCoordinatorService coordinator, CancellationToken ct) =>
            await coordinator.GetAsync(id, null, ct) is { } flow ? (IResult)Results.Ok(flow) : ApiProblem.NotFound())
            .WithName("GetFlow");

        flows.MapGet("/{id:guid}/versions/{version:int}", async (Guid id, int version,
            FlowCoordinatorService coordinator, CancellationToken ct) =>
            await coordinator.GetAsync(id, version, ct) is { } flow ? (IResult)Results.Ok(flow) : ApiProblem.NotFound())
            .WithName("GetFlowVersion");

        flows.MapPut("/{id:guid}", async (Guid id, SaveFlowRequest request,
            FlowCoordinatorService coordinator, CancellationToken ct) =>
        {
            var outcome = await coordinator.PublishAsync(request, ct);
            if (!outcome.IsValid) return (IResult)Results.UnprocessableEntity(outcome.Validation);
            if (outcome.MissingResources is { Count: > 0 } missing)
                return Results.UnprocessableEntity(new { title = string.Join(" ", missing), problems = missing });
            if (outcome.Saved is null)
                return ApiProblem.Conflict("Une autre version a été enregistrée. Rechargez le flow avant de publier vos modifications.");
            return Results.Ok(outcome.Saved);
        }).AddEndpointFilter<ValidateSaveFlowFilter>().WithName("PublishFlowVersion");

        flows.MapDelete("/{id:guid}", async (Guid id, bool? detachLibraries,
            FlowCoordinatorService coordinator, CancellationToken ct) =>
        {
            var (result, reason) = await coordinator.DeleteFlowAsync(id, detachLibraries == true, ct);
            return result switch
            {
                FlowDeleteResult.Deleted => Results.NoContent(),
                FlowDeleteResult.NotFound => ApiProblem.NotFound("Ce flow est introuvable."),
                FlowDeleteResult.Conflict => ApiProblem.Conflict(reason ?? "Impossible de supprimer ce flow."),
                _ => ApiProblem.NotFound()
            };
        }).WithName("DeleteFlow");

        flows.MapPost("/{id:guid}/clone", async (Guid id, CloneFlowRequest? request,
            FlowCoordinatorService coordinator, CancellationToken ct) =>
            await coordinator.CloneFlowAsync(id, request?.Name, ct) is { } cloned
                ? Results.Created($"/api/v1/flows/{cloned.Definition.Id}", cloned)
                : ApiProblem.NotFound("Le flow source est introuvable."))
            .WithName("CloneFlow");

        flows.MapPost("/from-template/{templateId:guid}", async (Guid templateId, CreateFlowFromTemplateRequest? request,
            FlowCoordinatorService coordinator, CancellationToken ct) =>
        {
            try
            {
                var created = await coordinator.CreateFromTemplateAsync(templateId, request ?? new(), ct);
                return created is not null
                    ? Results.Created($"/api/v1/flows/{created.Definition.Id}", created)
                    : ApiProblem.NotFound("Le modèle est introuvable.");
            }
            catch (FlowTemplateRevisionConflictException)
            {
                return Results.Conflict(new { title = "Ce modèle a changé. Rechargez-le avant de créer le flow." });
            }
            catch (FlowTemplateSetupConfigurationException exception)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["configuration"] = [exception.Message] });
            }
            catch (FlowTemplateSetupValidationException exception)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["configuration"] = exception.Issues.Select(issue => issue.Message).ToArray()
                });
            }
        }).WithName("CreateFlowFromTemplate");

        flows.MapPost("", async (
            JsonElement document,
            bool? publish,
            FlowCoordinatorService coordinator,
            FlowComponentRegistry registry,
            CancellationToken ct) =>
        {
            var outcome = await coordinator.CreateFlowAsync(document, publish, registry, ct);
            if (!outcome.IsValid) return (IResult)Results.UnprocessableEntity(outcome.Validation);
            if (outcome.ShouldPublish)
            {
                return outcome.Published is not null
                    ? Results.Created($"/api/v1/flows/{outcome.FlowId}", outcome.Published)
                    : ApiProblem.Conflict("Un flow avec cet identifiant ou cette version existe déjà.");
            }
            return outcome.Draft is not null
                ? Results.Created($"/api/v1/flows/{outcome.FlowId}/edit", outcome.Draft)
                : ApiProblem.Conflict("Impossible d'enregistrer le brouillon du flow.");
        }).WithName("CreateFlow");
    }

    private static void MapFlowCatalogEndpoints(this RouteGroupBuilder flows)
    {
        flows.MapGet("/{id:guid}/export", async (Guid id, FlowCoordinatorService coordinator, CancellationToken ct) =>
            await coordinator.ExportSingleAsync(id, ct) is { } export
                ? Results.File(export.Bytes, export.ContentType, export.FileName)
                : ApiProblem.NotFound())
            .WithName("ExportSingleFlow");

        flows.MapGet("/{id:guid}/usage", async (Guid id, FlowCoordinatorService coordinator, CancellationToken ct) =>
            Results.Ok(await coordinator.UsageAsync(id, ct))).WithName("GetFlowLibraryUsage");

        flows.MapGet("/{id:guid}/versions", async (Guid id, FlowCoordinatorService coordinator, CancellationToken ct) =>
            Results.Ok(await coordinator.VersionsAsync(id, ct)))
            .WithName("ListFlowVersions");
    }

    private static void MapFlowArchiveEndpoints(this RouteGroupBuilder flows)
    {
        flows.MapPost("/export", async (Guid[] ids, FlowArchiveService archives, CancellationToken ct) =>
        {
            try
            {
                var bytes = await archives.ExportAsync(ids, ct);
                return bytes is null
                    ? ApiProblem.BadRequest("Sélection invalide ou flow introuvable.")
                    : Results.File(bytes, "application/zip", "flows.zip");
            }
            catch (InvalidDataException e) { return ApiProblem.BadRequest(e.Message); }
        }).AllowViewer().RequireRateLimiting(AuthExtensions.HeavyOperationsPolicy).WithName("ExportFlowsArchive");

        flows.MapPost("/import", async (HttpRequest request, FlowArchiveService archives, CancellationToken ct) =>
        {
            try
            {
                var (result, conflict, count) = await archives.ImportAsync(request.Body, request.ContentType == "application/zip", ct);
                if (!result.IsValid) return (IResult)Results.UnprocessableEntity(result);
                return conflict
                    ? ApiProblem.Conflict("Une version existante a un contenu différent. Aucun flow n’a été importé.")
                    : Results.Ok(new { imported = count });
            }
            catch (Exception e) when (e is InvalidDataException or JsonException)
            {
                return ApiProblem.BadRequest("Fichier JSON ou ZIP invalide ou trop volumineux.");
            }
        }).RequireRateLimiting(AuthExtensions.HeavyOperationsPolicy).WithName("ImportFlowsArchive");
    }

    private static object BuildFlowSchema(FlowComponentRegistry registry, ClusterExtensionActivationState activation)
    {
        var components = registry.Plugins.SelectMany(p => p.Components.Select(c => new
        {
            pluginId = p.Id,
            pluginName = p.Name,
            pluginVersion = p.Version,
            pluginEnabled = activation.IsPluginEnabled(p.Id),
            componentId = c.Id,
            componentName = c.Name,
            description = c.Description,
            kind = c.Kind,
            mediaType = c.MediaType,
            supportedFlowMediaTypes = c.SupportedFlowMediaTypes,
            outputs = c.Outputs.Select(o => new { id = o.Id, label = o.Label, description = o.Description }),
            configurationFields = c.ConfigurationSchema.Fields.Select(f => new
            {
                id = f.Id,
                label = f.Label,
                type = f.Type,
                description = f.Description,
                required = f.Required,
                defaultValue = f.DefaultValue,
                options = f.Options?.Select(opt => new { value = opt.Value, label = opt.Label })
            })
        })).ToArray();

        var example = new
        {
            name = "Exemple Encodage HEVC",
            mediaType = "video",
            publish = true,
            nodes = new object[]
            {
                new
                {
                    id = "entry",
                    name = "Entrée vidéo",
                    component = "base-input-video",
                    configuration = new Dictionary<string, object>()
                },
                new
                {
                    id = "audio_rules",
                    name = "Uniformisation audio",
                    component = "base-audio-rules",
                    configuration = new Dictionary<string, object>()
                }
            },
            connections = new object[]
            {
                new
                {
                    id = "c1",
                    sourceNodeId = "entry",
                    sourceOutputId = "success",
                    targetNodeId = "audio_rules"
                }
            }
        };

        return new
        {
            formatVersion = 1,
            supportedMediaTypes = new[] { "video", "image", "music", "subtitles" },
            availableComponents = components,
            example
        };
    }

    internal static FlowDefinition ParseFlowDefinition(JsonElement element, FlowComponentRegistry registry) =>
        FlowDefinitionParser.Parse(element, registry);
}
