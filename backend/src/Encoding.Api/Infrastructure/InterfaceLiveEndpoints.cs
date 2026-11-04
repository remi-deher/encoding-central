using System.Text.Json;

namespace Encoding.Api.Infrastructure;

public static class InterfaceLiveEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapInterfaceLiveEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/live", async (HttpContext http, InterfaceLiveBroadcaster live, CancellationToken ct) =>
        {
            ExecutionStreamPrepare(http);
            var version = live.CurrentVersion;
            await WriteAsync(http, "ready", new { topics = Array.Empty<string>() }, ct);
            while (!ct.IsCancellationRequested)
            {
                var signal = await live.WaitAsync(version, TimeSpan.FromSeconds(15), ct);
                if (ct.IsCancellationRequested) break;
                version = signal.Version;
                if (signal.Topics.Count == 0)
                {
                    await WriteAsync(http, "ping", new { }, ct);
                    continue;
                }
                await WriteAsync(http, "change", new { topics = signal.Topics }, ct);
            }
        }).WithName("StreamInterfaceChanges");
    }

    /// <summary>Une mutation réussie prévient les interfaces. Les lectures et les signaux de présence n'annoncent rien.</summary>
    public static TBuilder NotifyLive<TBuilder>(this TBuilder builder, string topic) where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);
            var method = context.HttpContext.Request.Method;
            var path = context.HttpContext.Request.Path.Value ?? "";
            if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method)
                || path.EndsWith("/heartbeat", StringComparison.OrdinalIgnoreCase))
                return result;
            if (result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 })
                context.HttpContext.RequestServices.GetRequiredService<InterfaceLiveBroadcaster>().Publish(topic);
            return result;
        });
        return builder;
    }

    private static void ExecutionStreamPrepare(HttpContext http)
    {
        http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-cache, no-store";
        http.Response.Headers["X-Accel-Buffering"] = "no";
    }

    private static async Task WriteAsync<T>(HttpContext http, string eventType, T payload, CancellationToken ct)
    {
        await http.Response.WriteAsync($"event: {eventType}\ndata: {JsonSerializer.Serialize(payload, Json)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
