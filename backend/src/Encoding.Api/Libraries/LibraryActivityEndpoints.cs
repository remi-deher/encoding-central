using Encoding.Contracts.Libraries;
using Encoding.Api.Executions;
using Encoding.Api.Infrastructure;

namespace Encoding.Api.Libraries;

public static class LibraryActivityEndpoints
{
    public static void MapLibraryActivityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/activity/files", async (
            string? status,
            Guid? libraryId,
            string? flow,
            Guid? nodeId,
            string? search,
            int? limit,
            int? offset,
            LibraryActivityRepository repository,
            CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(status) &&
                status.Trim().ToLowerInvariant() is not ("untreated" or "active" or "done" or "failed" or "cancelled" or "disabled"))
                return ApiProblem.BadRequest("État d’activité invalide.");
            return Results.Ok(await repository.GetAsync(status, libraryId, flow, nodeId, search, limit, offset, ct));
        })
            .WithName("GetLibraryActivityFiles");

        app.MapGet("/api/v1/activity/stream", async (
            string? status,
            Guid? libraryId,
            string? flow,
            Guid? nodeId,
            string? search,
            int? limit,
            int? offset,
            bool? once,
            HttpContext http,
            LibraryActivityRepository repository,
            ExecutionStreamBroadcaster broadcaster,
            CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(status) &&
                status.Trim().ToLowerInvariant() is not ("untreated" or "active" or "done" or "failed" or "cancelled" or "disabled"))
                return ApiProblem.BadRequest("État d’activité invalide.");

            ExecutionStreamBroadcaster.PrepareResponse(http.Response);
            var version = broadcaster.CurrentVersion;
            var initial = await repository.GetAsync(status, libraryId, flow, nodeId, search, limit, offset, ct);
            await ExecutionStreamBroadcaster.WriteEventAsync(http.Response, "activity", initial, ExecutionStreamBroadcaster.SseJsonOptions, ct);
            if (once == true) return (IResult)Results.Empty;

            while (!ct.IsCancellationRequested)
            {
                version = await broadcaster.WaitForChangeAsync(version, TimeSpan.FromSeconds(15), ct);
                if (ct.IsCancellationRequested) break;
                var snapshot = await repository.GetAsync(status, libraryId, flow, nodeId, search, limit, offset, ct);
                await ExecutionStreamBroadcaster.WriteEventAsync(http.Response, "activity", snapshot, ExecutionStreamBroadcaster.SseJsonOptions, ct);
            }
            return (IResult)Results.Empty;
        })
            .WithName("StreamLibraryActivity");
    }
}
