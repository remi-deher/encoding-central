using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Flows;

public static class FlowTemplateEndpoints
{
    public static void MapFlowTemplateEndpoints(this WebApplication app)
    {
        var templates = app.MapGroup("/api/v1/templates").WithTags("Modèles");
        templates.MapGet("", async (FlowTemplateRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.ListAsync(ct))).WithName("ListFlowTemplates");
        templates.MapGet("/{id:guid}", async (Guid id, FlowTemplateRepository repository, CancellationToken ct) =>
            await repository.GetAsync(id, ct) is { } template ? Results.Ok(template) : Results.NotFound())
            .WithName("GetFlowTemplate");
        templates.MapPost("/from-flow", async (
            CreateFlowTemplateFromFlowRequest request,
            FlowTemplateRepository repository,
            FlowRepository flows,
            CancellationToken ct) =>
        {
            var errors = Validate(request);
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            var source = await flows.GetAsync(request.SourceFlowId, request.SourceVersion, ct);
            if (source is null) return Results.NotFound(new { title = "Cette version publiée du flow est introuvable." });
            var configurationError = repository.ValidateConfiguration(source.Definition, request.Configuration);
            if (configurationError is not null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["configuration"] = [configurationError] });
            return await UniqueConflict.GuardAsync(async () => {
                var id = Guid.NewGuid();
                var template = await repository.CreateFromFlowAsync(id, request, ct);
                return template is null
                    ? Results.NotFound(new { title = "Cette version publiée du flow est introuvable." })
                    : Results.Created($"/api/v1/templates/{id:D}", template);
            }, () => Results.Conflict(new { title = "Un modèle porte déjà ce nom." }));
        }).WithName("CreateFlowTemplateFromFlow");

        templates.MapPut("/{id:guid}", async (
            Guid id,
            UpdateFlowTemplateRequest request,
            FlowTemplateRepository repository,
            FlowDefinitionValidator validator,
            CancellationToken ct) =>
        {
            var errors = Validate(request);
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            var currentTemplate = await repository.GetAsync(id, ct);
            if (currentTemplate is null) return Results.NotFound();
            if (request.Definition.Id != currentTemplate.Definition.Id
                || request.Definition.MediaType != currentTemplate.Definition.MediaType)
                return Results.ValidationProblem(new Dictionary<string, string[]> {
                    ["definition"] = ["La modification d’un modèle conserve son identité et son type de média."]
                });
            var definitionValidation = validator.Validate(request.Definition);
            if (!definitionValidation.IsValid)
                return Results.ValidationProblem(new Dictionary<string, string[]> {
                    ["definition"] = definitionValidation.Issues.Select(issue => issue.Message).ToArray()
                });
            var configurationError = repository.ValidateConfiguration(request.Definition, request.Configuration);
            if (configurationError is not null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["configuration"] = [configurationError] });
            return await UniqueConflict.GuardAsync(async () => {
                var updated = await repository.UpdateAsync(id, request, ct);
                if (updated is not null) return Results.Ok(updated);
                var current = await repository.GetAsync(id, ct);
                return current is null
                    ? Results.NotFound()
                    : Results.Conflict(new { title = "Le modèle a été modifié depuis son ouverture. Rechargez-le puis réessayez." });
            }, () => Results.Conflict(new { title = "Un modèle porte déjà ce nom." }));
        }).WithName("UpdateFlowTemplate");
    }

    private static Dictionary<string, string[]> Validate(CreateFlowTemplateFromFlowRequest? request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null)
            return new() { ["request"] = ["La demande est obligatoire."] };
        if (request.SourceFlowId == Guid.Empty)
            errors["sourceFlowId"] = ["Choisissez un flow publié."];
        if (request.SourceVersion < 1)
            errors["sourceVersion"] = ["La version publiée doit être positive."];
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
            errors["name"] = ["Le nom du modèle est obligatoire et limité à 128 caractères."];
        if (request.Description is null || request.Description.Length > 1000)
            errors["description"] = ["La description est limitée à 1 000 caractères."];
        return errors;
    }

    private static Dictionary<string, string[]> Validate(UpdateFlowTemplateRequest? request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null) return new() { ["request"] = ["La demande est obligatoire."] };
        if (request.ExpectedRevision < 1) errors["expectedRevision"] = ["La révision attendue doit être positive."];
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
            errors["name"] = ["Le nom du modèle est obligatoire et limité à 128 caractères."];
        if (request.Description is null || request.Description.Length > 1000)
            errors["description"] = ["La description est limitée à 1 000 caractères."];
        if (request.Configuration is null) errors["configuration"] = ["La configuration est obligatoire."];
        if (request.Definition is null) errors["definition"] = ["La définition du modèle est obligatoire."];
        return errors;
    }
}
