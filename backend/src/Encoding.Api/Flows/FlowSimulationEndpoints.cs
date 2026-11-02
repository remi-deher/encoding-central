using Encoding.Api.Auth;
using System.ComponentModel;
using System.Text.Json;
using Encoding.Contracts.Flows;
using Microsoft.AspNetCore.Http.Features;

namespace Encoding.Api.Flows;

public static class FlowSimulationEndpoints
{
    public static void MapFlowSimulationEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/flows/simulate", async (FlowSimulationRequest request, FlowSimulator simulator,
            FlowDefinitionValidator validator, IServiceProvider services, CancellationToken ct) =>
        {
            try
            {
                if (JsonSerializer.Serialize(request).Length > 1048576) return (IResult)Results.BadRequest(new { title = "Le scénario dépasse 1 Mio." });
                var (flow, validation) = validator.ValidateDocument(request.Definition);
                if (flow is null || !validation.IsValid) return Results.UnprocessableEntity(simulator.Simulate(request));
                var coordinator = services.GetService(typeof(FlowCoordinatorService)) as FlowCoordinatorService
                    ?? new FlowCoordinatorService(services);
                var defaults = await coordinator.ResolveHandoffDefaultsAsync(flow, ct);
                var result = simulator.Simulate(request, defaults);
                return result.Status == "invalid" ? Results.UnprocessableEntity(result) : Results.Ok(result);
            }
            catch (ArgumentException e) { return Results.BadRequest(new { title = e.Message }); }
            catch (Exception e) when (e is JsonException or InvalidOperationException)
            { return Results.BadRequest(new { title = "Profil ou résultat simulé invalide. Vérifiez les types et identifiants." }); }
        }).AllowViewer().WithTags("Flows");
        app.MapPost("/api/v1/flows/inspect-metadata", async (HttpRequest request, FlowMetadataImporter importer, CancellationToken ct) =>
        {
            if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = FlowMetadataImporter.MaxBytes;
            if (request.ContentLength > FlowMetadataImporter.MaxBytes) return (IResult)Results.Json(new { title = "Le fichier dépasse 512 Mio." }, statusCode: 413);
            try { return Results.Ok(new { metadata = await importer.ImportAsync(request.Body, ct) }); }
            catch (InvalidOperationException e) { return Results.Json(new { title = e.Message }, statusCode: 429); }
            catch (Win32Exception) { return Results.Json(new { title = "MediaInfo est indisponible sur ce serveur." }, statusCode: 503); }
            catch (Exception e) when (e is InvalidDataException or JsonException)
            { return Results.BadRequest(new { title = e is InvalidDataException ? e.Message : "Rapport MediaInfo invalide." }); }
        }).AllowViewer().WithTags("Flows");
    }
}
