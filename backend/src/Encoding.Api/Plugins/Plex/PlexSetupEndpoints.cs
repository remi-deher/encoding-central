using Encoding.Contracts.Plugins;

namespace Encoding.Api.Plugins.Plex;

/// <summary>Assistant de configuration Plex (0008), réservé aux administrateurs comme la configuration des plugins.</summary>
public static class PlexSetupEndpoints
{
    public static void MapPlexSetupEndpoints(this WebApplication app)
    {
        var setup = app.MapGroup("/api/v1/plugins/plex/setup").WithTags("Plugins");
        setup.MapPost("/pin", (PlexSetupService service, CancellationToken ct) => Run(() => service.CreatePinAsync(ct)));
        setup.MapPost("/pin/check", (PlexPinCheckRequest request, PlexSetupService service, CancellationToken ct) =>
            Run(() => service.CheckPinAsync(request, ct)));
        setup.MapPost("/server", (PlexSelectServerRequest request, PlexSetupService service, CancellationToken ct) =>
            Run(() => service.SelectServerAsync(request, ct)));
        setup.MapPost("/libraries", (PlexLibrariesRequest request, PlexSetupService service, CancellationToken ct) =>
            Run(() => service.LibrariesAsync(request, ct)));
        setup.MapPut("/mappings", (PlexSaveMappingsRequest request, PlexSetupService service, CancellationToken ct) =>
            Run(() => service.SaveMappingsAsync(request, ct)));
    }

    /// <summary>Erreurs attendues → 422 (saisie ou session) ou 502 (Plex injoignable / refus), toujours sans secret.</summary>
    private static async Task<IResult> Run<T>(Func<Task<T>> action)
    {
        try { return Results.Ok(await action()); }
        catch (Exception exception) when (exception is PlexSetupException or PluginConfigurationValidationException)
        {
            return Results.UnprocessableEntity(new { title = exception.Message });
        }
        catch (PlexApiException exception)
        {
            return Results.Problem(title: exception.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
