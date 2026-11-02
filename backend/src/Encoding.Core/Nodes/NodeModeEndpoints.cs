using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Encoding.Core.Nodes;

/// <summary>État du mode du nœud tel que l’exposent le serveur et le worker ; <c>Managed</c> est faux hors image unique.</summary>
public sealed record NodeModeStatus(bool Managed, bool Server, bool Worker);

public interface INodeModeTransitionGuard
{
    Task<string?> ValidateAsync(NodeMode requested,CancellationToken ct);
}

public static class NodeModeEndpoints
{
    public const string ControlUrlSetting = "Launcher:ControlUrl";

    /// <summary>Déclare le client du lanceur local si <c>Launcher:ControlUrl</c> est configuré (image unique, architecture 0036).</summary>
    public static void AddNodeModeControl(this IServiceCollection services, string? controlUrl)
    {
        if (string.IsNullOrWhiteSpace(controlUrl)) return;
        services.AddHttpClient<LauncherControlClient>(client =>
        {
            client.BaseAddress = new Uri(controlUrl.EndsWith('/') ? controlUrl : controlUrl + '/');
            client.Timeout = LauncherControlClient.Timeout;
        });
    }

    /// <summary>
    /// Lecture et changement du mode du nœud local. Une définition unique sert le serveur et le worker ;
    /// l’autorisation reste celle de l’hôte (session administrateur côté serveur, secret local côté worker).
    /// </summary>
    public static void MapNodeMode(this IEndpointRouteBuilder app, string pattern)
    {
        app.MapGet(pattern, async (IServiceProvider services, CancellationToken cancellationToken) =>
        {
            var client = services.GetService<LauncherControlClient>();
            var mode = client is null ? null : await client.GetAsync(cancellationToken);
            return Results.Ok(mode is null ? new NodeModeStatus(false, true, true) : new NodeModeStatus(true, mode.Server, mode.Worker));
        }).WithName("GetNodeMode-" + RouteName(pattern));

        app.MapPut(pattern, async (NodeMode requested, IServiceProvider services, CancellationToken cancellationToken) =>
        {
            var client = services.GetService<LauncherControlClient>();
            if (client is null)
                return Results.Conflict(new { title = "Ce nœud n’est pas géré par l’image unique : son mode se règle par la variable Node__Roles." });
            try { NodeMode.Validate(requested); }
            catch (InvalidOperationException exception) { return Results.BadRequest(new { title = exception.Message }); }
            if(services.GetService<INodeModeTransitionGuard>() is {} guard && await guard.ValidateAsync(requested,cancellationToken) is {} reason)
                return Results.Conflict(new {title=reason});
            try
            {
                var applied = await client.SetAsync(requested, cancellationToken);
                return Results.Ok(new NodeModeStatus(true, applied.Server, applied.Worker));
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Le lanceur du nœud ne répond pas.");
            }
        }).WithName("SetNodeMode-" + RouteName(pattern));
    }

    private static string RouteName(string pattern) => pattern.Trim('/').Replace('/', '-');
}
