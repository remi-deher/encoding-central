using System.Text.Json;
using Encoding.WorkerAgent.Runtime;

namespace Encoding.WorkerAgent.Services;

internal sealed record WorkerStoreDesiredItem(string Kind, string Id, string Version, string? Platform, string Sha256);
internal sealed record WorkerStoreDesiredState(long Revision, IReadOnlyList<WorkerStoreDesiredItem> Items);
internal sealed record WorkerAppliedItem(string Kind, string Id, string Version, string? Platform, string Sha256, string Status, string? ErrorCode);

internal sealed class StoreEventCursor(string dataDirectory)
{
    private readonly string path = Path.Combine(dataDirectory, "store-events.id");
    public async Task<string?> ReadAsync(CancellationToken ct) => File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
    public async Task SaveAsync(string id, CancellationToken ct)
    {
        Directory.CreateDirectory(dataDirectory);
        var temporary = path + ".partial";
        await File.WriteAllTextAsync(temporary, id, ct);
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>Consomme le flux SSE Store du central et ne persiste Last-Event-ID qu'après application et rapport du snapshot.</summary>
internal sealed class StoreEventListener(WorkerAgentOptions options, WorkerIdentityStore identities, ILogger logger)
{
    private readonly HttpClient http = WorkerCentralHttp.Create(Timeout.InfiniteTimeSpan);

    public async Task RunAsync(Func<string, WorkerStoreDesiredState, CancellationToken, Task> reconcile, CancellationToken ct)
    {
        var persistedCursor = new StoreEventCursor(options.DataDirectory);
        var cursor = await persistedCursor.ReadAsync(ct);
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            var identity = await identities.GetAsync(ct);
            var stableConnection = false;
            foreach (var server in identity.IsEnrolled ? identity.GetCandidateServerUrls(options) : identity.ApiBaseUrl is null ? [] : [identity.ApiBaseUrl])
            {
                DateTimeOffset? connectedAt = null;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(server), "api/v1/store/events"));
                    request.Headers.Add("X-Node-Token", identity.WorkerToken);
                    if (!string.IsNullOrWhiteSpace(cursor)) request.Headers.TryAddWithoutValidation("Last-Event-ID", cursor);
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();
                    connectedAt = DateTimeOffset.UtcNow;
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    using var reader = new StreamReader(stream);
                    string? eventId = null;
                    string? eventData = null;
                    while (!ct.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(ct);
                        if (line is null) break;
                        if (line.StartsWith("id:", StringComparison.Ordinal)) eventId = line[3..].Trim();
                        else if (line.StartsWith("data:", StringComparison.Ordinal)) eventData = line[5..].TrimStart();
                        if (line.Length == 0)
                        {
                            if (eventId is not null && eventData is not null)
                            {
                                var desired = JsonSerializer.Deserialize<WorkerStoreDesiredState>(eventData, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                                    ?? throw new JsonException("État désiré SSE absent.");
                                if (!TryRevision(eventId, out var revision) || desired.Revision != revision)
                                    throw new JsonException("Révision de l'événement SSE incohérente.");
                                await reconcile(server, desired, ct);
                                cursor = eventId;
                                await persistedCursor.SaveAsync(cursor, ct);
                            }
                            eventId = null;
                            eventData = null;
                        }
                    }
                    stableConnection |= connectedAt is { } opened && DateTimeOffset.UtcNow - opened >= TimeSpan.FromSeconds(30);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException or JsonException)
                {
                    stableConnection |= connectedAt is { } opened && DateTimeOffset.UtcNow - opened >= TimeSpan.FromSeconds(30);
                    logger.LogWarning("Flux SSE Store interrompu ({Reason}).", ex.GetType().Name);
                }
            }
            if (stableConnection) delay = TimeSpan.FromSeconds(1);
            await Task.Delay(delay, ct);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }
    }

    private static bool TryRevision(string? id, out long revision)
    {
        revision = 0;
        // v1.{n} : ancien état par worker ; v2.{n} : état global du central (ADR 0046). Le worker ne lit que la révision et la renvoie telle quelle comme curseur.
        return id is not null && (id.StartsWith("v1.", StringComparison.Ordinal) || id.StartsWith("v2.", StringComparison.Ordinal))
            && long.TryParse(id.AsSpan(3), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out revision)
            && revision >= 0;
    }
}
