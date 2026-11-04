using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Encoding.Contracts.Workers;
using Encoding.WorkerAgent.Runtime;

namespace Encoding.WorkerAgent.Services;

/// <summary>Un seul flux SSE orchestre les projections plugins/outils et publie un rapport appliqué agrégé.</summary>
public sealed class StoreSyncCoordinator(WorkerAgentOptions options, WorkerIdentityStore identities,
    PluginSyncService plugins, ToolPackageSyncService tools, ILogger<StoreSyncCoordinator> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Roles.Contains(WorkerRole.Worker)) return Task.CompletedTask;
        return new StoreEventListener(options, identities, logger).RunAsync(ReconcileAsync, stoppingToken);
    }

    internal async Task ReconcileAsync(string server, WorkerStoreDesiredState state, CancellationToken ct)
    {
        var revision = state.Revision;
        var identity = await identities.GetAsync(ct);
        var pluginItems = await plugins.SyncDesiredAsync(server, state, ct);
        var toolItems = await tools.SyncDesiredAsync(server, state, ct);
        var currentItems = pluginItems.Concat(toolItems).ToArray();
        var appliedPath = Path.Combine(options.DataDirectory, "store-applied-state.json");
        IReadOnlyList<WorkerAppliedItem> previous = File.Exists(appliedPath)
            ? JsonSerializer.Deserialize<List<WorkerAppliedItem>>(await File.ReadAllTextAsync(appliedPath, ct), Json) ?? []
            : [];
        var currentKeys = currentItems.Select(Key).ToHashSet(StringComparer.Ordinal);
        var removed = previous.Where(item => !currentKeys.Contains(Key(item)))
            .Select(item => item with { Status = "removed", ErrorCode = null }).ToArray();
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(new Uri(server), "api/v1/store/applied-state"))
        {
            Content = JsonContent.Create(new { revision, items = currentItems.Concat(removed.Take(Math.Max(0, 500 - currentItems.Length))).ToArray() })
        };
        request.Headers.Add("X-Node-Token", identity.WorkerToken);
        using var http = WorkerCentralHttp.Create(TimeSpan.FromSeconds(30));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            logger.LogInformation("Le rapport Store {Revision} est périmé; le flux SSE demandera la révision désirée courante.", revision);
            return;
        }
        response.EnsureSuccessStatusCode();
        Directory.CreateDirectory(options.DataDirectory);
        await File.WriteAllTextAsync(appliedPath, JsonSerializer.Serialize(currentItems, Json), ct);
    }

    private static string Key(WorkerAppliedItem item) => $"{item.Kind}\u001f{item.Id}\u001f{item.Version}\u001f{item.Platform}";
}
