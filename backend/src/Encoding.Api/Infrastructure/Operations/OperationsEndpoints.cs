using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Api.Executions;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Encoding.Api.Infrastructure.Operations;

/// <summary>Point d’entrée unique de l’outillage d’exploitation : health checks, métriques, journalisation et rétention.</summary>
public static class OperationsEndpoints
{
    public const string CorrelationHeader = "X-Correlation-Id";
    private const string ReadyTag = "ready";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static void AddOperations(this WebApplicationBuilder builder)
    {
        var options = OperationsOptions.From(builder.Configuration);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ClusterReconciliationHealth>();
        builder.Services.AddSingleton<ClusterMetricsCollector>();
        builder.Services.AddSingleton<ArtifactRetention>();
        builder.Services.AddHostedService<ArtifactRetentionService>();

        var artifacts = Path.GetFullPath(builder.Configuration["Runtime:ArtifactDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "runtime-artifacts"));
        var backups = Path.GetFullPath(builder.Configuration["FlowBackups:Directory"] ?? Path.Combine(AppContext.BaseDirectory, "flow-backups"));
        builder.Services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: [ReadyTag])
            .AddCheck<MigrationsHealthCheck>("migrations")
            .AddCheck<ClusterHealthCheck>("cluster")
            .AddCheck<ClusterReconciliationHealthCheck>("cluster-reconciliation")
            .AddCheck<FlowBackupHealthCheck>("flow-backups")
            .Add(new HealthCheckRegistration("artifact-storage", _ => new StorageHealthCheck(artifacts, options.Health.MinFreeDiskBytes), null, null))
            .Add(new HealthCheckRegistration("backup-storage", _ => new StorageHealthCheck(backups, options.Health.MinFreeDiskBytes), null, null));
    }

    public static void MapOperationsEndpoints(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<OperationsOptions>();
        // Vivacité : le processus répond. Aucun contrôle de base ou de pair, pour qu’un orchestrateur ne redémarre pas un nœud isolé.
        app.MapGet("/health/live", (HttpContext context) =>
        {
            context.Response.Headers.AccessControlAllowOrigin = "*";
            return Results.Ok(new { status = "ok" });
        }).AllowAnonymous().WithTags("Health");
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(ReadyTag),
            ResponseWriter = (context, report) =>
            {
                context.Response.Headers.AccessControlAllowOrigin = "*";
                return WriteReportAsync(context, report, detailed: false);
            },
        }).AllowAnonymous().WithTags("Health");
        app.MapHealthChecks("/health/details", new HealthCheckOptions
        {
            ResponseWriter = (context, report) => WriteReportAsync(context, report, detailed: true),
        }).WithTags("Health");

        if (options.Metrics.Enabled)
        {
            // Sans jeton Bearer configuré, les métriques exigent une session ; avec jeton, un collecteur Prometheus s’authentifie par ce seul jeton.
            var metrics = app.MapGet("/metrics", async (HttpContext context, ClusterMetricsCollector collector, CancellationToken cancellationToken) =>
            {
                if (!Authorized(context, options.Metrics.Token)) return Results.Unauthorized();
                return Results.Text(await collector.CollectAsync(cancellationToken), "text/plain; version=0.0.4; charset=utf-8");
            }).WithTags("Operations");
            if (!string.IsNullOrEmpty(options.Metrics.Token)) metrics.AllowAnonymous();
        }
    }

    /// <summary>Attribue un identifiant de corrélation, l’ajoute aux journaux et alimente les métriques HTTP.</summary>
    public static void UseRequestObservability(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Encoding.Api.Requests");
        app.Use(async (context, next) =>
        {
            var incoming = context.Request.Headers[CorrelationHeader].ToString();
            var correlationId = IsValidCorrelationId(incoming) ? incoming : Guid.NewGuid().ToString("N");
            context.Response.Headers[CorrelationHeader] = correlationId;
            var started = Stopwatch.GetTimestamp();
            using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
            try { await next(context); }
            catch (Exception exception) when (!context.Response.HasStarted &&
                (exception is Data.Sqlite.SqliteWriteUnavailableException
                    or Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 }))
            {
                context.Response.Headers.RetryAfter = "1";
                await Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Stockage temporairement occupé.",
                    detail: "Réessayez cette opération après un court délai.",
                    extensions: new Dictionary<string, object?> { ["code"] = "sqlite_write_unavailable" })
                    .ExecuteAsync(context);
            }
            finally
            {
                var elapsed = Stopwatch.GetTimestamp() - started;
                var routed = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
                var route = routed ?? "(non routé)";
                var status = context.Response.StatusCode;
                OperationsMetrics.Request(context.Request.Method, route, status, elapsed);
                // Chemin seul, sans chaîne de requête ni en-têtes : les jetons ne doivent jamais atteindre les journaux.
                if (!route.StartsWith("/health", StringComparison.Ordinal) && route != "/metrics" && (routed is not null || status >= 400))
                    logger.Log(status >= 500 ? LogLevel.Warning : LogLevel.Information, "{Method} {Route} → {Status} en {ElapsedMs} ms", context.Request.Method,
                        route, status, Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
            }
        });
    }

    public static bool IsValidCorrelationId(string value) =>
        value.Length is > 0 and <= 64 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    public static bool Authorized(HttpContext context, string? token)
    {
        if (string.IsNullOrEmpty(token)) return true;
        var header = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var supplied = System.Text.Encoding.UTF8.GetBytes(header[prefix.Length..]);
        var expected = System.Text.Encoding.UTF8.GetBytes(token);
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    private static Task WriteReportAsync(HttpContext context, HealthReport report, bool detailed)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.StatusCode = report.Status == HealthStatus.Unhealthy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK;
        var body = detailed
            ? (object)new
            {
                status = report.Status.ToString().ToLowerInvariant(),
                totalDurationMs = (long)report.TotalDuration.TotalMilliseconds,
                checks = report.Entries.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => new
                {
                    name = entry.Key,
                    status = entry.Value.Status.ToString().ToLowerInvariant(),
                    description = entry.Value.Description,
                    durationMs = (long)entry.Value.Duration.TotalMilliseconds,
                    data = entry.Value.Data,
                }),
            }
            : new { status = report.Status == HealthStatus.Healthy ? "ready" : report.Status.ToString().ToLowerInvariant() };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, Json));
    }
}
