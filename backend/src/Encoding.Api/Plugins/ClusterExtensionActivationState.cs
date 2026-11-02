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

    private sealed record Snapshot(IReadOnlySet<string> EnabledPlugins, IReadOnlySet<string> EnabledTools)
    {
        public static Snapshot Empty { get; } = new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
    }

    private Snapshot snapshot = Snapshot.Empty;

    /// <summary><c>base</c> et <c>nodes</c> restent actifs une fois installés. Tout autre plugin est inactif tant qu’un administrateur ne l’a pas activé.</summary>
    public bool IsPluginEnabled(string pluginId) => !CanDisablePlugin(pluginId)
        || Volatile.Read(ref snapshot).EnabledPlugins.Contains(pluginId);
    /// <summary>Un outil est inactif tant qu’un administrateur ne l’a pas activé.</summary>
    public bool IsToolEnabled(string toolId) => Volatile.Read(ref snapshot).EnabledTools.Contains(toolId);
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

    /// <summary>
    /// Retire, pour les nouvelles attributions, les plugins désactivés et les outils média désactivés.
    /// Les capacités d'exécution (lecture directe, artefacts, scripts) ne sont pas des outils du dépôt : elles restent.
    /// </summary>
    public TCapability[] EnabledCapabilities<TCapability>(IReadOnlyList<TCapability> capabilities, Func<TCapability, string> toolId) =>
        capabilities.Where(capability => KeepsCapability(toolId(capability), IsPluginEnabled, IsToolEnabled)).ToArray();

    internal static bool KeepsCapability(string capabilityId, Func<string, bool> pluginEnabled, Func<string, bool> toolEnabled)
    {
        if (capabilityId.StartsWith("plugin/", StringComparison.Ordinal))
            return pluginEnabled(capabilityId["plugin/".Length..]);
        if (ToolIds.Contains(capabilityId, StringComparer.Ordinal))
            return toolEnabled(capabilityId);
        return true;
    }

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
        var enabledPlugins = new HashSet<string>(StringComparer.Ordinal);
        var enabledTools = new HashSet<string>(StringComparer.Ordinal);
        await using var command = database.CreateCommand("""
            SELECT register_key, value
            FROM cluster_sync_registers
            WHERE register_key LIKE 'extensions/plugins/%' OR register_key LIKE 'extensions/tools/%'
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!bool.TryParse(reader.GetString(1), out var enabled) || !enabled) continue;
            var key = reader.GetString(0);
            if (key.StartsWith("extensions/plugins/", StringComparison.Ordinal))
                enabledPlugins.Add(key["extensions/plugins/".Length..]);
            else if (key.StartsWith("extensions/tools/", StringComparison.Ordinal))
                enabledTools.Add(key["extensions/tools/".Length..]);
        }
        Volatile.Write(ref snapshot, new(enabledPlugins, enabledTools));
    }

    private void SetLocal(string id, bool enabled, bool isPlugin)
    {
        var current = Volatile.Read(ref snapshot);
        var enabledPlugins = new HashSet<string>(current.EnabledPlugins, StringComparer.Ordinal);
        var enabledTools = new HashSet<string>(current.EnabledTools, StringComparer.Ordinal);
        var target = isPlugin ? enabledPlugins : enabledTools;
        if (enabled) target.Add(id);
        else target.Remove(id);
        Volatile.Write(ref snapshot, new(enabledPlugins, enabledTools));
    }

    private static string PluginRegister(string pluginId) => "extensions/plugins/" + pluginId;
    private static string ToolRegister(string toolId) => "extensions/tools/" + toolId;
}
