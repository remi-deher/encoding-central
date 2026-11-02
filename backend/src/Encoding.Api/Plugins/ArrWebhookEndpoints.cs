using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;
using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Contracts.Plugins;

namespace Encoding.Api.Plugins;

/// <summary>
/// Déclencheur d’import Sonarr/Radarr. L’appelant s’authentifie avec le secret du webhook (et non par une session
/// utilisateur) : la requête ne fait qu’enregistrer une demande, que le chemin commun des bibliothèques exécute.
/// </summary>
public static class ArrWebhookEndpoints
{
    private const int MaxBodyBytes = 1024 * 1024;
    private static readonly string[] Supported = ["sonarr", "radarr"];

    public static void MapArrWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/integrations/{pluginId}/webhook", async (string pluginId, HttpRequest request, FlowComponentRegistry catalog,
            PluginConfigurationRepository configurations, IntegrationTriggerRepository triggers, WebhookAuthThrottle throttle, CancellationToken ct) =>
        {
            var plugin = Supported.Contains(pluginId) ? catalog.Plugins.SingleOrDefault(item => item.Id == pluginId) : null;
            if (plugin is null) return Results.NotFound();
            // Route anonyme : le corps est borné avant toute lecture, quel que soit le plafond global du serveur.
            if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxBodyBytes;
            if (request.ContentLength > MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var client = pluginId + "|" + (request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            if (throttle.IsBlocked(client))
            {
                request.HttpContext.Response.Headers.RetryAfter = ((int)WebhookAuthThrottle.Window.TotalSeconds).ToString();
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }
            var values = await configurations.ReadGlobalValuesAsync(plugin, ct);
            var secret = values.TryGetValue("webhookSecret", out var configured) && configured.ValueKind == JsonValueKind.String ? configured.GetString() : null;
            if (string.IsNullOrEmpty(secret))
                return Results.Json(new { title = "Le secret du webhook n’est pas configuré dans le plugin." }, statusCode: StatusCodes.Status409Conflict);
            if (!IsAuthorized(request, secret))
            {
                throttle.RecordFailure(client);
                request.HttpContext.Response.Headers.WWWAuthenticate = "Basic realm=\"encoding-webhook\"";
                return Results.Unauthorized();
            }

            JsonDocument document;
            try
            {
                using var body = new MemoryStream();
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
                {
                    if (body.Length + read > MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                    body.Write(buffer, 0, read);
                }
                body.Position = 0;
                document = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { title = "Corps JSON invalide." });
            }
            using (document)
            {
                ArrWebhookEvent webhook;
                try { webhook = ArrWebhookEvent.Parse(pluginId, document.RootElement); }
                catch (FormatException exception) { return Results.BadRequest(new { title = exception.Message }); }
                if (webhook.Kind == ArrWebhookKind.Test) return Results.Ok(new { status = "ok" });
                if (webhook.Kind == ArrWebhookKind.Ignored) return Results.Accepted(value: new { status = "ignored" });

                IReadOnlyList<LibraryPathMapping> mappings;
                try { mappings = LibraryPathMapping.Parse(values.TryGetValue("pathMappings", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null); }
                catch (FormatException) { return Results.Json(new { title = "La correspondance des chemins du plugin est invalide." }, statusCode: StatusCodes.Status409Conflict); }

                int accepted = 0, duplicate = 0, unmapped = 0;
                foreach (var remote in webhook.RemotePaths)
                {
                    if (LibraryPathMapping.ToLibrary(mappings, remote) is not { } target) { unmapped++; continue; }
                    switch (await triggers.RecordAsync(pluginId, target.LibraryId, target.RelativePath, ct))
                    {
                        case TriggerRecordResult.Accepted: accepted++; break;
                        case TriggerRecordResult.Duplicate: duplicate++; break;
                        default: unmapped++; break;
                    }
                }
                return Results.Accepted(value: new { status = "accepted", accepted, duplicate, unmapped });
            }
        }).AllowAnonymous().WithTags("Plugins");
    }

    internal static bool IsAuthorized(HttpRequest request, string secret)
    {
        var candidates = new List<string>();
        if (request.Headers.TryGetValue("X-Encoding-Webhook-Secret", out var header)) candidates.AddRange(header.Where(value => value is not null).Select(value => value!));
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..].Trim()));
                var separator = decoded.IndexOf(':');
                if (separator >= 0) candidates.Add(decoded[(separator + 1)..]);
            }
            catch (FormatException) { }
        }
        var expected = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));
        return candidates.Aggregate(false, (matched, candidate) =>
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(candidate)), expected) | matched);
    }
}

/// <summary>
/// Limite les tentatives d'authentification ratées sur le webhook (route anonyme) : au-delà de <see cref="MaxFailures"/>
/// échecs par fenêtre et par appelant, les requêtes sont refusées (429) jusqu'à la fin de la fenêtre. L'état est local au
/// serveur et volontairement non répliqué : il ne protège pas un secret faible, il freine une recherche exhaustive.
/// </summary>
public sealed class WebhookAuthThrottle(TimeProvider? clock = null)
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private const int MaxTrackedClients = 4096;
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Start, int Count)> failures = new(StringComparer.Ordinal);

    public bool IsBlocked(string client) =>
        failures.TryGetValue(client, out var entry) && time.GetUtcNow() - entry.Start < Window && entry.Count >= MaxFailures;

    public void RecordFailure(string client)
    {
        var now = time.GetUtcNow();
        if (failures.Count >= MaxTrackedClients)
            foreach (var stale in failures.Where(pair => now - pair.Value.Start >= Window).Select(pair => pair.Key).ToList()) failures.TryRemove(stale, out _);
        if (failures.Count >= MaxTrackedClients) return;
        failures.AddOrUpdate(client, _ => (now, 1),
            (_, entry) => now - entry.Start >= Window ? (now, 1) : (entry.Start, entry.Count + 1));
    }
}
