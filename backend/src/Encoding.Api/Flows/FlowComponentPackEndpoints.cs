using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Flows;

public static class FlowComponentPackEndpoints
{
    public static void MapFlowComponentPackEndpoints(this WebApplication app)
    {
        var packs = app.MapGroup("/api/v1/component-packs").WithTags("Packs de composants");
        packs.MapGet("", async (FlowCoordinatorService coordinator, CancellationToken ct) =>
            Results.Ok(await coordinator.ListComponentPacksAsync(ct))).WithName("ListFlowComponentPacks");
        packs.MapGet("/{id:guid}", async (Guid id, FlowCoordinatorService coordinator, CancellationToken ct) =>
            await coordinator.GetComponentPackAsync(id, ct) is { } pack ? Results.Ok(pack) : Results.NotFound())
            .WithName("GetFlowComponentPack");
        packs.MapPost("/from-flow", async (
            CreateFlowComponentPackRequest request,
            FlowCoordinatorService coordinator,
            CancellationToken ct) =>
        {
            var errors = Validate(request);
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            return await UniqueConflict.GuardAsync(async () => {
                var pack = await coordinator.CreateComponentPackFromFlowAsync(Guid.NewGuid(), request, ct);
                return pack is null
                    ? Results.UnprocessableEntity(new { title = "Le flow publié doit contenir une entrée reliée à au moins un composant." })
                    : Results.Created($"/api/v1/component-packs/{pack.Pack.Id:D}", pack);
            }, () => Results.Conflict(new { title = "Un pack de composants porte déjà ce nom." }));
        }).WithName("CreateFlowComponentPackFromFlow");
    }

    private static Dictionary<string, string[]> Validate(CreateFlowComponentPackRequest? request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null) return new() { ["request"] = ["La demande est obligatoire."] };
        if (request.SourceFlowId == Guid.Empty) errors["sourceFlowId"] = ["Choisissez un flow publié."];
        if (request.SourceVersion < 1) errors["sourceVersion"] = ["La version publiée doit être positive."];
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
            errors["name"] = ["Le nom est obligatoire et limité à 128 caractères."];
        if (request.Description is null || request.Description.Length > 1000)
            errors["description"] = ["La description est limitée à 1 000 caractères."];
        return errors;
    }
}
