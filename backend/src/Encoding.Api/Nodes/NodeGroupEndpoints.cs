using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using System.Text.RegularExpressions;
using Encoding.Contracts.Nodes;

namespace Encoding.Api.Nodes;

public static partial class NodeGroupEndpoints
{
    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelKeyPattern();

    public static void MapNodeGroupEndpoints(this WebApplication app)
    {
        var groups = app.MapGroup("/api/v1/node-groups").WithTags("Node groups");
        groups.MapGet("", async (NodeGroupRepository repository, CancellationToken ct) => Results.Ok(await repository.ListAsync(ct)))
            .WithName("ListNodeGroups");
        groups.MapGet("/{id:guid}", async (Guid id, NodeGroupRepository repository, CancellationToken ct) =>
            await repository.GetAsync(id, ct) is { } group ? Results.Ok(group) : Results.NotFound())
            .WithName("GetNodeGroup");
        groups.MapPost("", async (SaveNodeGroupRequest request, NodeGroupRepository repository, CancellationToken ct) =>
        {
            var errors = Validate(request);
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            return await UniqueConflict.GuardAsync(async () => {
                if (!await repository.SaveAsync(null, request, ct)) return Results.Conflict();
                var created = (await repository.ListAsync(ct)).FirstOrDefault(g => g.Name == request.Name.Trim());
                return Results.Created("/api/v1/node-groups/" + created?.Id, created);
            }, () => Results.Conflict(new { title = "Un groupe porte déjà ce nom." }));
        }).WithName("CreateNodeGroup");
        groups.MapPut("/{id:guid}", async (Guid id, SaveNodeGroupRequest request, NodeGroupRepository repository, CancellationToken ct) =>
        {
            var errors = Validate(request);
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            return await UniqueConflict.GuardAsync(async () => { return await repository.SaveAsync(id, request, ct) ? Results.Ok(await repository.GetAsync(id,
                ct)) : Results.NotFound(); }, () => Results.Conflict(new { title = "Un groupe porte déjà ce nom." }));
        }).WithName("UpdateNodeGroup");
        groups.MapDelete("/{id:guid}", async (Guid id, NodeGroupRepository repository, CancellationToken ct) =>
            await repository.DeleteAsync(id, ct) switch
            {
                "deleted" => (IResult)Results.NoContent(),
                "in-use" => Results.Conflict(new { title = "Ce groupe est ciblé par un flow (version publiée ou brouillon). Retirez-le du flow avant de le supprimer." }),
                _ => Results.NotFound()
            })
            .WithName("DeleteNodeGroup");
        app.MapGet("/api/v1/nodes/{id:guid}/identity", async (Guid id, NodeGroupRepository repository, CancellationToken ct) =>
            await repository.GetNodeIdentityAsync(id, ct) is { } identity ? Results.Ok(identity) : Results.NotFound())
            .WithName("GetNodeIdentity");
        app.MapPut("/api/v1/nodes/{id:guid}/identity", async (Guid id, SaveNodeIdentityRequest request, NodeGroupRepository repository, CancellationToken ct) =>
        {
            if (request.Labels is null) return (IResult)Results.ValidationProblem(new Dictionary<string, string[]> { ["labels"] = ["Les étiquettes sont obligatoires."] });
            var errors = ValidateLabels(request.Labels);
            if (request.DisplayName?.Length > 128) errors["displayName"] = ["Le nom d’affichage est limité à 128 caractères."];
            if (errors.Count > 0) return (IResult)Results.ValidationProblem(errors);
            return await repository.SaveNodeIdentityAsync(id, request, ct) ? Results.NoContent() : Results.NotFound();
        }).WithName("SaveNodeIdentity");
    }

    private static Dictionary<string, string[]> Validate(SaveNodeGroupRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        if (r.Criteria is null || r.Labels is null || r.NodeIds is null)
            return new Dictionary<string, string[]> { ["request"] = ["Les critères, étiquettes et membres sont obligatoires."] };
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 128) errors["name"] = ["Le nom est obligatoire et limité à 128 caractères."];
        if (r.Description?.Length > 2000) errors["description"] = ["La description est limitée à 2000 caractères."];
        if (r.Criteria.MinimumMemoryMb is < 0 or > 4_194_304 || r.Criteria.MinimumConcurrentTasks is < 0 or > 1024
            || r.Criteria.MinimumProcessorCount is < 0 or > 4096
            || r.Criteria.OperatingSystem?.Length > 128 || r.Criteria.Architecture?.Length > 64
            || r.Criteria.RequiredToolIds?.Count > 128 || r.NodeIds.Count > 2048) errors["criteria"] = ["Les critères ou la taille du groupe sont invalides."];
        if (r.Criteria.Roles?.Distinct().Count() != r.Criteria.Roles?.Count) errors["criteria.roles"] = ["Les rôles ne doivent pas contenir de doublons."];
        if (r.Mode == NodeGroupMode.Automatic && r.NodeIds.Count > 0) errors["nodeIds"] = ["Les membres d’un groupe automatique sont calculés depuis ses critères."];
        if (r.Mode == NodeGroupMode.Automatic && (r.Criteria.Roles?.Count ?? 0) == 0
            && string.IsNullOrWhiteSpace(r.Criteria.OperatingSystem) && string.IsNullOrWhiteSpace(r.Criteria.Architecture)
            && r.Criteria.MinimumMemoryMb is null && (r.Criteria.RequiredToolIds?.Count ?? 0) == 0
            && r.Criteria.MinimumConcurrentTasks is null && r.Criteria.MinimumProcessorCount is null) errors["criteria"] = ["Un groupe automatique doit définir au moins un critère."];
        if (r.Mode == NodeGroupMode.Manual && (r.Criteria.Roles?.Count > 0 || r.Criteria.OperatingSystem is not null || r.Criteria.Architecture is not null
            || r.Criteria.MinimumMemoryMb is not null || r.Criteria.RequiredToolIds?.Count > 0 || r.Criteria.MinimumConcurrentTasks is not null
            || r.Criteria.MinimumProcessorCount is not null)) errors["criteria"] = ["Un groupe manuel ne peut pas définir de critères automatiques."];
        if (r.Labels.Count > 128) errors["labels"] = ["Un groupe ne peut pas déclarer plus de 128 étiquettes."];
        foreach (var (key, value) in r.Labels)
            if (!LabelKeyPattern().IsMatch(key) || value?.Length > 256) errors[$"labels.{key}"] = ["Une clé d’étiquette est invalide ou sa valeur dépasse 256 caractères."];
        return errors;
    }

    private static Dictionary<string, string[]> ValidateLabels(IReadOnlyDictionary<string, string?> labels)
    {
        var errors = new Dictionary<string, string[]>();
        if (labels.Count > 128) errors["labels"] = ["Un nœud ne peut pas déclarer plus de 128 étiquettes."];
        foreach (var (key, value) in labels)
            if (!LabelKeyPattern().IsMatch(key) || value?.Length > 256) errors[$"labels.{key}"] = ["Une clé d’étiquette est invalide ou sa valeur dépasse 256 caractères."];
        return errors;
    }
}
