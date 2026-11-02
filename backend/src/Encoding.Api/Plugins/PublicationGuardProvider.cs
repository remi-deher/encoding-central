using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Plugins;

namespace Encoding.Api.Plugins;

/// <summary>
/// Gardes de publication (architecture 0059) : pour chaque plugin qui en déclare une (<see cref="PluginPublicationGuard"/>), active et
/// configurée dans le cluster, décrit l'invocation que le nœud d'accès doit faire avant de remplacer un fichier source. Le serveur ne
/// juge jamais la garde lui-même : il fournit les valeurs effectives (secrets compris, comme pour un worker) et l'arbitrage reste au nœud d'accès.
/// </summary>
public sealed class PublicationGuardProvider(PluginConfigurationRepository configurations, FlowComponentRegistry registry)
{
    public async Task<IReadOnlyList<RuntimePublicationGuard>> ListAsync(CancellationToken ct)
    {
        var result = new List<RuntimePublicationGuard>();
        foreach (var plugin in registry.Plugins.Where(item => item.PublicationGuard is not null))
        {
            var guard = plugin.PublicationGuard!;
            var stored = await configurations.ReadGlobalValuesAsync(plugin, ct);
            // Un plugin jamais configuré n'est pas utilisé dans ce cluster : aucune garde, sans bloquer les remplacements.
            if (stored.Count == 0) continue;
            var values = WithDefaults(plugin, stored);
            if (!Flag(values, guard.EnabledField)) continue;
            var component = plugin.Components.Single(item => item.Id == guard.ComponentId);
            var configuration = new Dictionary<string, JsonElement>(guard.FixedConfiguration, StringComparer.Ordinal);
            foreach (var (componentField, pluginField) in guard.ConfigurationFields)
                if (values.TryGetValue(pluginField, out var value)) configuration[componentField] = value;
            var maxWait = values.TryGetValue(guard.MaxWaitField, out var wait) && wait.ValueKind == JsonValueKind.Number && wait.TryGetInt32(out var minutes) ? minutes : 10;
            result.Add(new(plugin.Id, plugin.Version, component.Id, component.Version, component.ConfigurationSchema.Version, configuration, values,
                Math.Clamp(maxWait, 1, 720), guard.WaitOutputs, guard.AllowOutputs));
        }
        return result;
    }

    private static Dictionary<string, JsonElement> WithDefaults(PluginManifest plugin, Dictionary<string, JsonElement> stored)
    {
        var values = new Dictionary<string, JsonElement>(stored, StringComparer.Ordinal);
        foreach (var field in plugin.ConfigurationSchema?.Fields ?? [])
            if (!values.ContainsKey(field.Id) && field.DefaultValue is { } fallback) values[field.Id] = fallback;
        return values;
    }

    private static bool Flag(Dictionary<string, JsonElement> values, string field) =>
        values.TryGetValue(field, out var value) && value.ValueKind == JsonValueKind.True;
}
