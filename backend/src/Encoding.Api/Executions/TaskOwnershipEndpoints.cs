using Encoding.Api.Auth;
using Encoding.Contracts.Executions;

namespace Encoding.Api.Executions;

public static class TaskOwnershipEndpoints
{
    public static void MapTaskOwnershipEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/executions/{id:guid}/ownership", async (Guid id, RuntimeExecutionCoordinator coordinator, CancellationToken ct) =>
            await coordinator.GetTaskOwnershipAsync(id, ct) is { } state ? (IResult)Results.Ok(state) : Results.NotFound())
            .Produces<TaskOwnershipStatus>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization()
            .AllowViewer()
            .WithTags("Executions")
            .WithName("GetExecutionOwnership");
    }
}
