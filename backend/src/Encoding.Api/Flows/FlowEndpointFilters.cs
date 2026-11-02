using System.Text.Json;
using Encoding.Api.Infrastructure;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Flows;

/// <summary>Filtre de validation d’enveloppe pour l’enregistrement d’un brouillon de flow.</summary>
public sealed class ValidateFlowDraftFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var id = context.Arguments.OfType<Guid>().FirstOrDefault();
        var request = context.Arguments.OfType<SaveFlowDraftRequest>().FirstOrDefault();
        var d = request?.Definition;
        if (request is null || d is null || d.Id != id || id == Guid.Empty
            || request.ExpectedRevision < 0 || request.ExpectedRevision == int.MaxValue
            || d.FormatVersion != 1 || d.Version < 1
            || string.IsNullOrWhiteSpace(d.Name) || d.Name.Length > 128
            || d.MediaType is not ("video" or "image" or "music" or "subtitles")
            || d.Nodes is null || d.Connections is null
            || d.Nodes.Count > 1000 || d.Connections.Count > 4000
            || JsonSerializer.Serialize(d, FlowRepository.JsonOptions).Length > 1048576)
        {
            return ApiProblem.BadRequest("Le document de brouillon est invalide.");
        }
        return await next(context);
    }
}

/// <summary>Filtre de validation d’enveloppe pour la publication d’une version de flow.</summary>
public sealed class ValidateSaveFlowFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var id = context.Arguments.OfType<Guid>().FirstOrDefault();
        var request = context.Arguments.OfType<SaveFlowRequest>().FirstOrDefault();
        if (request?.Definition is null || id != request.Definition.Id
            || request.ExpectedVersion < 0 || request.ExpectedVersion == int.MaxValue
            || request.Definition.Version != request.ExpectedVersion + 1)
        {
            return ApiProblem.BadRequest("Identifiant ou version attendue invalide.");
        }
        return await next(context);
    }
}
