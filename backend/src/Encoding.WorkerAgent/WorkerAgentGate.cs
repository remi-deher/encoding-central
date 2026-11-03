using Encoding.Core.Workers;
using Encoding.WorkerAgent.Services;

namespace Encoding.WorkerAgent;

/// <summary>
/// Barrière des routes de l'agent (<c>/api/v1/worker/*</c>) : le secret local est exigé, sauf deux exceptions du premier démarrage
/// de l'image worker, avant tout rattachement :
/// choisir le central (<c>GET</c> et <c>POST centrals</c>, tant qu'aucun central n'est connu : la liste ne porte ni clé ni secret) et lire le matériel détecté (<c>GET hardware</c>,
/// tant que le worker n'est pas rattaché). Les lectures sont aussi protégées : l'état contient les clés de jumelage.
/// </summary>
internal static class WorkerAgentGate
{
    public const string RoutePrefix = "/api/v1/worker";
    public const string ClustersPath = "/api/v1/worker/centrals";
    public const string HardwarePath = "/api/v1/worker/hardware";

    public static void Use(WebApplication app, WorkerAgentOptions options) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(RoutePrefix)
                && !LocalEnrollment.Matches(options.LocalEnrollmentSecretFile, context.Request.Headers[LocalEnrollment.HeaderName].ToString())
                && !await IsOpenBeforeEnrollmentAsync(context, options))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next();
        });

    private static async Task<bool> IsOpenBeforeEnrollmentAsync(HttpContext context, WorkerAgentOptions options)
    {
        if (!options.WorkerImage) return false;
        var path = context.Request.Path;
        var identity = await context.RequestServices.GetRequiredService<WorkerIdentityStore>().GetAsync(context.RequestAborted);
        if ((HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsGet(context.Request.Method))
            && path.Equals(ClustersPath, StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(identity.ApiBaseUrl);
        if (HttpMethods.IsGet(context.Request.Method) && path.Equals(HardwarePath, StringComparison.OrdinalIgnoreCase))
            return !identity.IsEnrolled;
        return false;
    }
}

/// <summary>Matériel détecté, sans identifiant, secret ni état de rattachement.</summary>
public sealed record WorkerHardwareResponse(Encoding.Contracts.Workers.WorkerInventory Inventory, IReadOnlyList<WorkerHardwareTool> Tools);

public sealed record WorkerHardwareTool(string ToolId, IReadOnlyList<string> AccelerationTypes);
