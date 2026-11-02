using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Plugins;

namespace Encoding.Api.Plugins;

public sealed record MissingPluginPrerequisite(string Code, string NodeId, string PluginId, string FieldId);

public static class PluginConfigurationPrerequisites
{
    public static IReadOnlyList<MissingPluginPrerequisite> FindMissing(FlowDefinition flow,
        IReadOnlyList<PluginManifest> manifests,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> configurations)
    {
        var missing = new List<MissingPluginPrerequisite>();
        foreach (var node in flow.Nodes)
        {
            var plugin = manifests.FirstOrDefault(plugin => plugin.Id == node.Component.PluginId);
            if (plugin?.ConfigurationSchema is not { } schema) continue;
            configurations.TryGetValue(plugin.Id, out var values);
            foreach (var field in schema.Fields.Where(field => field.Required))
            {
                if (values is not null && values.TryGetValue(field.Id, out var value)
                    && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                    && (value.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(value.GetString()))) continue;
                if (field.DefaultValue is { } defaultValue && defaultValue.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                    && (defaultValue.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(defaultValue.GetString()))) continue;
                missing.Add(new("plugin_configuration_missing", node.Id, plugin.Id, field.Id));
            }
        }
        return missing;
    }
}
