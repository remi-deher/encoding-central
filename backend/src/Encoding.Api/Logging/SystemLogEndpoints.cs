using System.Text.Json;
using System.Text.Json.Serialization;
using Encoding.Api.Auth;
using Encoding.Contracts.Logging;
using Encoding.Core.Logging;
using Microsoft.AspNetCore.Mvc;

namespace Encoding.Api.Logging;

public static class SystemLogEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void MapSystemLogEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/system/logs")
            .WithTags("SystemLogs")
            .RequireAuthorization(AuthExtensions.AdministratorPolicy);

        group.MapGet("", async (
            [FromQuery] SystemLogLevel? minLevel,
            [FromQuery] string? category,
            [FromQuery] string? search,
            [FromQuery] Guid? nodeId,
            [FromQuery] int? limit,
            [FromQuery] DateTimeOffset? since,
            SystemLogRingBuffer ringBuffer,
            SystemLogRepository repository,
            CancellationToken ct) =>
        {
            var filter = new SystemLogFilterQuery(
                MinLevel: minLevel ?? SystemLogLevel.Information,
                Category: category,
                Search: search,
                NodeId: nodeId,
                Limit: limit.GetValueOrDefault(100),
                Since: since);

            var inMemory = ringBuffer.Query(filter);

            // Si le filtre demande des avertissements ou erreurs, ou si on a peu d'entrées en mémoire,
            // on complète avec l'historique persistant SQLite en dédupliquant par Id.
            if (filter.MinLevel >= SystemLogLevel.Warning || inMemory.Count < filter.Limit)
            {
                var dbEntries = await repository.QueryAsync(filter, ct);
                if (dbEntries.Count > 0)
                {
                    var seenIds = new HashSet<string>(inMemory.Select(e => e.Id));
                    var combined = new List<SystemLogEntry>(inMemory);
                    foreach (var dbEntry in dbEntries)
                    {
                        if (seenIds.Add(dbEntry.Id))
                        {
                            combined.Add(dbEntry);
                        }
                    }

                    // Trie antichronologique
                    combined.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
                    var capped = combined.Take(filter.Limit).ToList();
                    return Results.Ok(capped);
                }
            }

            return Results.Ok(inMemory);
        }).WithName("GetSystemLogs");

        group.MapGet("/categories", async (
            SystemLogRingBuffer ringBuffer,
            SystemLogRepository repository,
            CancellationToken ct) =>
        {
            var inMemCategories = ringBuffer.GetCategories();
            var dbCategories = await repository.GetCategoriesAsync(ct);

            var set = new HashSet<string>(inMemCategories, StringComparer.OrdinalIgnoreCase);
            foreach (var cat in dbCategories)
            {
                set.Add(cat);
            }

            var sorted = set.ToList();
            sorted.Sort(StringComparer.OrdinalIgnoreCase);

            return Results.Ok(new SystemLogCategoriesResponse(sorted));
        }).WithName("GetSystemLogCategories");

        group.MapGet("/stream", async (
            HttpContext context,
            [FromQuery] SystemLogLevel? minLevel,
            [FromQuery] string? category,
            [FromQuery] string? search,
            [FromQuery] Guid? nodeId,
            SystemLogRingBuffer ringBuffer,
            SystemLogStreamBroadcaster broadcaster,
            CancellationToken ct) =>
        {
            var filter = new SystemLogFilterQuery(
                MinLevel: minLevel ?? SystemLogLevel.Information,
                Category: category,
                Search: search,
                NodeId: nodeId,
                Limit: 50);

            context.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache, no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            // 1. Émet d'abord les logs existants récents (dans l'ordre chronologique pour le client)
            var initialEntries = ringBuffer.Query(filter).Reverse().ToList();
            foreach (var entry in initialEntries)
            {
                var json = JsonSerializer.Serialize(entry, JsonOptions);
                await context.Response.WriteAsync($"event: log\ndata: {json}\n\n", ct);
            }
            await context.Response.Body.FlushAsync(ct);

            // 2. Boucle d'écoute temps réel avec heartbeat toutes les 15s
            using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var enumerator = broadcaster.SubscribeAsync(filter, streamCancellation.Token).GetAsyncEnumerator(streamCancellation.Token);
            // Keep a single pending read across heartbeats.
            Task<bool>? moveNextTask = null;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    moveNextTask ??= enumerator.MoveNextAsync().AsTask();
                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15), ct);

                    var completed = await Task.WhenAny(moveNextTask, timeoutTask);
                    if (completed == moveNextTask)
                    {
                        var hasMore = await moveNextTask;
                        moveNextTask = null;
                        if (!hasMore)
                            break;

                        var item = enumerator.Current;
                        var json = JsonSerializer.Serialize(item, JsonOptions);
                        await context.Response.WriteAsync($"event: log\ndata: {json}\n\n", ct);
                        await context.Response.Body.FlushAsync(ct);
                    }
                    else
                    {
                        // Heartbeat SSE pour maintenir les proxys (Traefik, Nginx) ouverts
                        await context.Response.WriteAsync(": heartbeat\n\n", ct);
                        await context.Response.Body.FlushAsync(ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Connexion fermée normalement par le client
            }
            finally
            {
                await streamCancellation.CancelAsync();
                if (moveNextTask is not null)
                {
                    try { await moveNextTask; } catch (OperationCanceledException) { }
                }
                await enumerator.DisposeAsync();
            }
        }).WithName("StreamSystemLogs");
    }
}
