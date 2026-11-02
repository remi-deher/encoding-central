using Encoding.Api.Infrastructure.Operations;

namespace Encoding.Api.Infrastructure;

/// <summary>
/// Fabrique centralisée de réponses RFC 7807 (<c>application/problem+json</c>) enrichies avec l’identifiant de corrélation.
/// </summary>
public static class ApiProblem
{
    public static IServiceCollection AddApiProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Instance ??= context.HttpContext.Request.Path.Value;
                if (context.HttpContext.Response.Headers.TryGetValue(OperationsEndpoints.CorrelationHeader, out var correlationId)
                    && !string.IsNullOrWhiteSpace(correlationId))
                {
                    context.ProblemDetails.Extensions["correlationId"] = correlationId.ToString();
                }
            };
        });
        return services;
    }

    public static IResult BadRequest(string title, string? detail = null) =>
        Results.Problem(
            title: title,
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            type: "https://tools.ietf.org/html/rfc9110#section-15.5.1");

    public static IResult Unauthorized(string title, string? detail = null) =>
        Results.Problem(
            title: title,
            detail: detail,
            statusCode: StatusCodes.Status401Unauthorized,
            type: "https://tools.ietf.org/html/rfc9110#section-15.5.2");

    public static IResult NotFound(string title = "Ressource introuvable.", string? detail = null) =>
        Results.Problem(
            title: title,
            detail: detail,
            statusCode: StatusCodes.Status404NotFound,
            type: "https://tools.ietf.org/html/rfc9110#section-15.5.5");

    public static IResult Conflict(string title, string? detail = null) =>
        Results.Problem(
            title: title,
            detail: detail,
            statusCode: StatusCodes.Status409Conflict,
            type: "https://tools.ietf.org/html/rfc9110#section-15.5.10");

    public static IResult Gone(string title, string? detail = null) =>
        Results.Problem(
            title: title,
            detail: detail,
            statusCode: StatusCodes.Status410Gone,
            type: "https://tools.ietf.org/html/rfc9110#section-15.5.11");

    public static IResult TooManyRequests(string title = "Trop de requêtes. Veuillez réessayer ultérieurement.", string? detail = null) =>
        Results.Problem(
            title: title,
            detail: detail,
            statusCode: StatusCodes.Status429TooManyRequests,
            type: "https://tools.ietf.org/html/rfc6585#section-4");

    public static IResult Validation(IDictionary<string, string[]> errors, string title = "Un ou plusieurs champs sont invalides.") =>
        Results.ValidationProblem(
            errors,
            title: title,
            type: "https://tools.ietf.org/html/rfc9110#section-15.5.1");
}
