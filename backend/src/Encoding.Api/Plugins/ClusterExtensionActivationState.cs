using System.Data.Common;
using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Api.Infrastructure;

namespace Encoding.Api.Plugins;

/// <summary>
/// Cluster-wide activation preferences for flow plugins and media-tool roles.
/// The existing reconciliation register is the shared source; changes converge eventually on every server.
/// </summary>
public sealed class ClusterExtensionActivationState(
    DbDataSource database,
    ClusterReconciliationRepository journal,
    FlowComponentRegistry registry,
    ILogger<ClusterExtensionActivationState> logger) : BackgroundService
{
    public static readonly IReadOnlyList<string> ToolIds =
        ["ffmpeg", "mkvtoolnix", "ffprobe", "mediainfo", "imagemagick", "python"];

    private sealed record Snapshot(IReadOnlySet<string> DisabledPlugins, IReadOnlySet<string> DisabledTools)
    {
        public static Snapshot Empty { get; } = new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
    }

    private Snapshot snapshot = Snapshot.Empty;

    public bool IsPluginEnabled(string pluginId) => !CanDisablePlugin(pluginId)
        || !Volatile.Read(ref snapshot).DisabledPlugins.Contains(pluginId);
    public bool IsToolEnabled(string toolId) => !Volatile.Read(ref snapshot).DisabledTools.Contains(toolId);
    public bool CanDisablePlugin(string pluginId) => pluginId is not ("base" or "nodes");
    public bool IsKnownPlugin(string pluginId) => registry.Plugins.Any(plugin => plugin.Id == pluginId);
    public bool IsKnownTool(string toolId) => ToolIds.Contains(toolId, StringComparer.Ordinal);

    public Task InitializeAsync(CancellationToken ct) => RefreshAsync(ct);

    public async Task SetPluginEnabledAsync(string pluginId, bool enabled, CancellationToken ct)
    {
        if (!IsKnownPlugin(pluginId)) throw new KeyNotFoundException("Plugin inconnu.");
        if (!enabled && !CanDisablePlugin(pluginId))
            throw new InvalidOperationException("Ce plugin essentiel ne peut pas être désactivé.");

        await journal.SetRegisterAsync(PluginRegister(pluginId), JsonSerializer.Serialize(enabled), ct);
        SetLocal(pluginId, enabled, isPlugin: true);
    }

    public async Task SetToolEnabledAsync(string toolId, bool enabled, CancellationToken ct)
    {
        if (!IsKnownTool(toolId)) throw new KeyNotFoundException("Outil média inconnu.");

        await journal.SetRegisterAsync(ToolRegister(toolId), JsonSerializer.Serialize(enabled), ct);
        SetLocal(toolId, enabled, isPlugin: false);
    }

    /// <summary>Retire les capacités des outils désactivés lors des nouvelles attributions.</summary>
    public TCapability[] EnabledCapabilities<TCapability>(IReadOnlyList<TCapability> capabilities, Func<TCapability, string> toolId) =>
        capabilities.Where(capability => IsToolEnabled(toolId(capability))).ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "L’état partagé des plugins et outils sera relu au prochain cycle.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var disabledPlugins = new HashSet<string>(StringComparer.Ordinal);
        var disabledTools = new HashSet<string>(StringComparer.Ordinal);
        await using var command = database.CreateCommand("""
            SELECT register_key, value
            FROM cluster_sync_registers
            WHERE register_key LIKE 'extensions/plugins/%' OR register_key LIKE 'extensions/tools/%'
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!bool.TryParse(reader.GetString(1), out var enabled) || enabled) continue;
            var key = reader.GetString(0);
            if (key.StartsWith("extensions/plugins/", StringComparison.Ordinal))
                disabledPlugins.Add(key["extensions/plugins/".Length..]);
            else if (key.StartsWith("extensions/tools/", StringComparison.Ordinal))
                disabledTools.Add(key["extensions/tools/".Length..]);
        }
        Volatile.Write(ref snapshot, new(disabledPlugins, disabledTools));
    }

    private void SetLocal(string id, bool enabled, bool isPlugin)
    {
        var current = Volatile.Read(ref snapshot);
        var disabledPlugins = new HashSet<string>(current.DisabledPlugins, StringComparer.Ordinal);
        var disabledTools = new HashSet<string>(current.DisabledTools, StringComparer.Ordinal);
        var target = isPlugin ? disabledPlugins : disabledTools;
        if (enabled) target.Remove(id);
        else target.Add(id);
        Volatile.Write(ref snapshot, new(disabledPlugins, disabledTools));
    }

    private static string PluginRegister(string pluginId) => "extensions/plugins/" + pluginId;
    private static string ToolRegister(string toolId) => "extensions/tools/" + toolId;
}
