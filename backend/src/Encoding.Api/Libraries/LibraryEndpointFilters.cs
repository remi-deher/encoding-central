using Encoding.Api.Infrastructure;
using Encoding.Contracts.Libraries;

namespace Encoding.Api.Libraries;

/// <summary>Filtre de validation du nom de bibliothèque lors de la création ou de la modification.</summary>
public sealed class ValidateLibraryNameFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var name = context.Arguments.OfType<CreateLibraryRequest>().FirstOrDefault()?.Name
            ?? context.Arguments.OfType<UpdateLibraryRequest>().FirstOrDefault()?.Name;

        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name))
            errors["name"] = ["Le nom de la bibliothèque est obligatoire."];
        else if (name.Trim().Length > 128)
            errors["name"] = ["Le nom ne peut pas dépasser 128 caractères."];

        if (errors.Count > 0)
            return ApiProblem.Validation(errors);

        return await next(context);
    }
}
