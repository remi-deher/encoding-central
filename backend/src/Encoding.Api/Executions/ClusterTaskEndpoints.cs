using Encoding.Api.Auth;
using Encoding.Contracts.Executions;
namespace Encoding.Api.Executions;

public static class ClusterTaskCoordinationEndpoints
{
    public static void MapClusterTaskCoordinationEndpoints(this WebApplication app)
    {
        app.MapPost("/"+ClusterTaskTransport.Path,(HttpRequest request,HttpResponse response,ClusterTaskPeerHandler handler,CancellationToken ct)=>handler.HandleAsync(request,response,ct))
            .AllowAnonymous().RequireRateLimiting(AuthExtensions.ClusterMeshPolicy).WithTags("Cluster tasks"); // Self-authenticated admitted-peer RSA, as the mesh endpoint.
        var tasks=app.MapGroup("/api/v1/cluster/tasks").WithTags("Cluster tasks");
        tasks.MapPost("/{id:guid}/move",async(Guid id,ClusterTaskMoveRequest request,ClusterTaskCoordinator coordinator,
            CancellationToken ct)=>Result(await coordinator.MoveAsync(id,request.TargetPeerId,ct))).RequireAuthorization(AuthExtensions.AdministratorPolicy);
        tasks.MapPost("/{id:guid}/recover",async(Guid id,ClusterTaskCoordinator coordinator,CancellationToken ct)=>Result(await coordinator.RecoverAsync(id,
            ct))).RequireAuthorization(AuthExtensions.AdministratorPolicy);
        tasks.MapPost("/{id:guid}/force-source-retry",async(Guid id,ClusterTaskForceSourceRetryRequest request,ClusterTaskCoordinator coordinator,
            CancellationToken ct)=>Result(await coordinator.ForceSourceRetryAsync(id,request,ct))).RequireAuthorization(AuthExtensions.AdministratorPolicy);
        tasks.MapPost("/peers/{peer:guid}/neutralize",async(Guid peer,ClusterTaskNeutralizeRequest request,ClusterTaskCoordinator coordinator,
            CancellationToken ct)=>Result(await coordinator.NeutralizeAsync(peer,request,ct))).RequireAuthorization(AuthExtensions.AdministratorPolicy);
        tasks.MapPost("/peers/{peer:guid}/readmit",async(Guid peer,ClusterTaskCoordinator coordinator,
            CancellationToken ct)=>Result(await coordinator.ReadmitAsync(peer,ct))).RequireAuthorization(AuthExtensions.AdministratorPolicy);
        tasks.MapGet("/status",async(ClusterTaskMaintenance maintenance,CancellationToken ct)=>Results.Ok(await maintenance.StatusAsync(ct))).AllowViewer();
    }
    private static IResult Result(ClusterTaskResult result)=>result.Success?Results.Ok(result):Results.Conflict(result);
}
