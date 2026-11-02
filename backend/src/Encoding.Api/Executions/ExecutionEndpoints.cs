using System.Text.Json;
using System.Text.Json.Serialization;
using Encoding.Api.Auth;
using Encoding.Api.Infrastructure;
using Encoding.Contracts.Executions;

namespace Encoding.Api.Executions;

public static class ExecutionEndpoints
{
    private static readonly JsonSerializerOptions SseJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void MapExecutionEndpoints(this WebApplication app)
    {
        var executions = app.MapGroup("/api/v1/executions").WithTags("Executions");

        executions.MapGet("", async (
            string? state,
            Guid? libraryId,
            Guid? nodeId,
            DateTimeOffset? since,
            int? limit,
            int? offset,
            ExecutionRepository repository,
            CancellationToken ct) =>
        {
            var query = new ExecutionFilterQuery(state, libraryId, nodeId, since, limit ?? 200, offset ?? 0);
            return Results.Ok(await repository.OverviewAsync(query, ct));
        }).WithName("GetExecutionOverview");

        executions.MapGet("/stream", async (
            string? state,
            Guid? libraryId,
            Guid? nodeId,
            int? limit,
            int? offset,
            bool? once,
            HttpContext http,
            ExecutionRepository repository,
            ExecutionStreamBroadcaster broadcaster,
            CancellationToken ct) =>
        {
            ExecutionStreamBroadcaster.PrepareResponse(http.Response);
            var query = new ExecutionFilterQuery(state, libraryId, nodeId, null, limit ?? 200, offset ?? 0);
            var version = broadcaster.CurrentVersion;
            var initial = await repository.OverviewAsync(query, ct);
            await ExecutionStreamBroadcaster.WriteEventAsync(http.Response, "overview", initial, SseJsonOptions, ct);
            if (once == true) return;

            while (!ct.IsCancellationRequested)
            {
                version = await broadcaster.WaitForChangeAsync(version, TimeSpan.FromSeconds(15), ct);
                if (ct.IsCancellationRequested) break;
                var snapshot = await repository.OverviewAsync(query, ct);
                await ExecutionStreamBroadcaster.WriteEventAsync(http.Response, "overview", snapshot, SseJsonOptions, ct);
            }
        }).WithName("StreamExecutionOverview");

        executions.MapGet("/{id:guid}", async (Guid id, ExecutionRepository repository, CancellationToken ct) =>
        {
            var execution = await repository.GetAsync(id, ct);
            return execution is null ? ApiProblem.NotFound() : Results.Ok(execution);
        }).AllowNodeCredentials().WithName("GetExecution");

        executions.MapGet("/{id:guid}/stream", async (
            Guid id,
            bool? once,
            HttpContext http,
            ExecutionRepository repository,
            ExecutionStreamBroadcaster broadcaster,
            CancellationToken ct) =>
        {
            var current = await repository.GetAsync(id, ct);
            if (current is null)
            {
                await ApiProblem.NotFound().ExecuteAsync(http);
                return;
            }

            ExecutionStreamBroadcaster.PrepareResponse(http.Response);
            var version = broadcaster.CurrentVersion;
            var events = await repository.EventsAsync(id, ct);
            var pipeline = await repository.PipelineAsync(id, ct);
            var initial = new ExecutionDetailSnapshot(current, events, pipeline, DateTimeOffset.UtcNow);
            await ExecutionStreamBroadcaster.WriteEventAsync(http.Response, "detail", initial, SseJsonOptions, ct);
            if (once == true || IsTerminalState(current.State)) return;

            while (!ct.IsCancellationRequested)
            {
                version = await broadcaster.WaitForChangeAsync(version, TimeSpan.FromSeconds(10), ct);
                if (ct.IsCancellationRequested) break;
                var next = await repository.GetAsync(id, ct);
                if (next is null) break;
                var nextEvents = await repository.EventsAsync(id, ct);
                var nextPipeline = await repository.PipelineAsync(id, ct);
                var snapshot = new ExecutionDetailSnapshot(next, nextEvents, nextPipeline, DateTimeOffset.UtcNow);
                await ExecutionStreamBroadcaster.WriteEventAsync(http.Response, "detail", snapshot, SseJsonOptions, ct);
                if (IsTerminalState(next.State)) break;
            }
        }).WithName("StreamExecutionDetail");

        executions.MapGet("/{id:guid}/events", async (
            Guid id,
            int? afterRevision,
            int? limit,
            ExecutionRepository repository,
            CancellationToken ct) =>
            await repository.GetAsync(id, ct) is null
                ? ApiProblem.NotFound()
                : Results.Ok(await repository.EventsAsync(id, afterRevision, limit, ct)))
            .WithName("GetExecutionEvents");

        executions.MapGet("/{id:guid}/pipeline", async (Guid id, ExecutionRepository repository, CancellationToken ct) =>
            await repository.GetAsync(id, ct) is null
                ? ApiProblem.NotFound()
                : Results.Ok(await repository.PipelineAsync(id, ct)))
            .WithName("GetExecutionPipeline");

        executions.MapGet("/{id:guid}/flow", async (Guid id, ExecutionRepository repository, CancellationToken ct) =>
        {
            var flow = await repository.FlowAsync(id, ct);
            return flow is null ? ApiProblem.NotFound() : Results.Ok(flow);
        }).WithName("GetExecutionFlow");

        executions.MapPost("", () => ApiProblem.Gone("Lancez les traitements depuis les fichiers détectés de la bibliothèque."))
            .WithName("CreateExecutionDeprecated");

        executions.MapPost("/{id:guid}/cancel", async (Guid id, ExecutionRepository repository, CancellationToken ct) =>
            await repository.CancelAsync(id, ct)
                ? Results.NoContent()
                : ApiProblem.Conflict("Cette exécution ne peut pas être annulée dans son état actuel."))
            .WithName("CancelExecution");

        executions.MapPost("/{id:guid}/retry", async (Guid id, ExecutionRepository repository, CancellationToken ct) =>
        {
            var retryId = await repository.RetryAsync(id, ct);
            return retryId is null
                ? ApiProblem.Conflict("Relance impossible ou déjà enregistrée pour cette tentative.")
                : Results.Created($"/api/v1/executions/{retryId}", new { id = retryId });
        }).WithName("RetryExecution");

        executions.MapPost("/{id:guid}/resume", async (Guid id, ExecutionRepository repository, CancellationToken ct) =>
        {
            var resumedId = await repository.ResumeAsync(id, ct);
            return resumedId is null
                ? ApiProblem.Conflict("Reprise impossible : seule une exécution annulée sans reprise précédente peut être relancée.")
                : Results.Created($"/api/v1/executions/{resumedId}", new { id = resumedId });
        }).WithName("ResumeExecution");
    }

    private static bool IsTerminalState(string state) => ExecutionStates.IsTerminal(state);
}
