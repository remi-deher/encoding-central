using System.Text.Json;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Flows;

public static class FlowDefinitionParser
{
    public static FlowDefinition Parse(JsonElement element, FlowComponentRegistry registry)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return new FlowDefinition(1, Guid.NewGuid(), 1, "Nouveau Flow", "video", [], []);

        if (element.TryGetProperty("definition", out var defProp) && defProp.ValueKind == JsonValueKind.Object)
            element = defProp;

        var id = element.TryGetProperty("id", out var idProp)
            && idProp.ValueKind == JsonValueKind.String
            && idProp.TryGetGuid(out var parsedId)
            && parsedId != Guid.Empty
            ? parsedId
            : Guid.NewGuid();

        var formatVersion = element.TryGetProperty("formatVersion", out var fvProp)
            && fvProp.ValueKind == JsonValueKind.Number
            && fvProp.TryGetInt32(out var fv)
            && fv > 0
            ? fv
            : 1;

        var version = element.TryGetProperty("version", out var vProp)
            && vProp.ValueKind == JsonValueKind.Number
            && vProp.TryGetInt32(out var v)
            && v > 0
            ? v
            : 1;

        var name = element.TryGetProperty("name", out var nProp)
            && nProp.ValueKind == JsonValueKind.String
            && nProp.GetString() is { Length: > 0 } n
            ? n.Trim()
            : "Nouveau Flow";

        var mediaType = (element.TryGetProperty("mediaType", out var mtProp)
            && mtProp.ValueKind == JsonValueKind.String
            && mtProp.GetString() is { Length: > 0 } mt
            ? mt
            : (element.TryGetProperty("type", out var tProp)
                && tProp.ValueKind == JsonValueKind.String
                && tProp.GetString() is { Length: > 0 } t
                ? t
                : "video")).Trim().ToLowerInvariant();

        var nodes = ParseNodes(element, registry);
        var connections = ParseConnections(element);

        return new FlowDefinition(formatVersion, id, version, name, mediaType, nodes, connections);
    }

    private static List<FlowNodeDefinition> ParseNodes(JsonElement element, FlowComponentRegistry registry)
    {
        var nodes = new List<FlowNodeDefinition>();
        if (!element.TryGetProperty("nodes", out var nodesProp) || nodesProp.ValueKind != JsonValueKind.Array)
            return nodes;

        var idx = 0;
        foreach (var node in nodesProp.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object)
                continue;

            idx++;
            var nodeId = node.TryGetProperty("id", out var nid)
                && nid.ValueKind == JsonValueKind.String
                && nid.GetString() is { Length: > 0 } nidStr
                ? nidStr
                : $"node_{idx}";

            var nodeName = node.TryGetProperty("name", out var nn)
                && nn.ValueKind == JsonValueKind.String
                ? nn.GetString()
                : null;

            var componentRef = ResolveComponentReference(node, registry);

            var configuration = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var descriptor = registry.Resolve(componentRef);
            if (descriptor is not null)
            {
                foreach (var field in descriptor.ConfigurationSchema.Fields)
                {
                    if (field.DefaultValue is { } defVal) configuration[field.Id] = defVal.Clone();
                }
            }
            if (node.TryGetProperty("configuration", out var cfg) && cfg.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in cfg.EnumerateObject())
                    configuration[prop.Name] = prop.Value.Clone();
            }

            nodes.Add(new FlowNodeDefinition(nodeId, nodeName, componentRef, configuration));
        }

        return nodes;
    }

    private static FlowComponentReference ResolveComponentReference(JsonElement node, FlowComponentRegistry registry)
    {
        if (!node.TryGetProperty("component", out var comp))
            return new FlowComponentReference("base", "1.0.0", "unknown", "1.0.0", 1);

        if (comp.ValueKind == JsonValueKind.String)
        {
            var compId = comp.GetString()!;
            return registry.FindReference(compId) ?? new FlowComponentReference("base", "1.0.0", compId, "1.0.0", 1);
        }

        if (comp.ValueKind != JsonValueKind.Object)
            return new FlowComponentReference("base", "1.0.0", "unknown", "1.0.0", 1);

        if (comp.TryGetProperty("componentId", out var cid)
            && cid.ValueKind == JsonValueKind.String
            && comp.TryGetProperty("pluginId", out var pid)
            && pid.ValueKind == JsonValueKind.String
            && comp.TryGetProperty("pluginVersion", out var pv)
            && pv.ValueKind == JsonValueKind.String
            && comp.TryGetProperty("componentVersion", out var cv)
            && cv.ValueKind == JsonValueKind.String
            && comp.TryGetProperty("configurationSchemaVersion", out var csv)
            && csv.ValueKind == JsonValueKind.Number
            && csv.TryGetInt32(out var csvVal))
        {
            return new FlowComponentReference(
                pid.GetString()!,
                pv.GetString()!,
                cid.GetString()!,
                cv.GetString()!,
                csvVal);
        }

        var candidateCompId = comp.TryGetProperty("componentId", out var cid2) && cid2.ValueKind == JsonValueKind.String
            ? cid2.GetString()
            : (comp.TryGetProperty("id", out var idComp) && idComp.ValueKind == JsonValueKind.String ? idComp.GetString() : null);

        var candidatePluginId = comp.TryGetProperty("pluginId", out var pEl) && pEl.ValueKind == JsonValueKind.String
            ? pEl.GetString()
            : null;

        if (candidateCompId is not null)
        {
            return registry.FindReference(candidateCompId, candidatePluginId)
                ?? new FlowComponentReference(candidatePluginId ?? "base", "1.0.0", candidateCompId, "1.0.0", 1);
        }

        return new FlowComponentReference("base", "1.0.0", "unknown", "1.0.0", 1);
    }

    private static List<FlowConnectionDefinition> ParseConnections(JsonElement element)
    {
        var connections = new List<FlowConnectionDefinition>();
        if (!element.TryGetProperty("connections", out var connsProp) || connsProp.ValueKind != JsonValueKind.Array)
            return connections;

        var idx = 0;
        foreach (var conn in connsProp.EnumerateArray())
        {
            if (conn.ValueKind != JsonValueKind.Object)
                continue;

            idx++;
            var connId = conn.TryGetProperty("id", out var cid)
                && cid.ValueKind == JsonValueKind.String
                && cid.GetString() is { Length: > 0 } cidStr
                ? cidStr
                : $"conn_{idx}";

            var source = conn.TryGetProperty("sourceNodeId", out var sn)
                && sn.ValueKind == JsonValueKind.String
                ? (sn.GetString() ?? "")
                : "";

            var output = conn.TryGetProperty("sourceOutputId", out var so)
                && so.ValueKind == JsonValueKind.String
                ? (so.GetString() ?? "success")
                : "success";

            var target = conn.TryGetProperty("targetNodeId", out var tn)
                && tn.ValueKind == JsonValueKind.String
                ? (tn.GetString() ?? "")
                : "";

            connections.Add(new FlowConnectionDefinition(connId, source, output, target));
        }

        return connections;
    }
}
