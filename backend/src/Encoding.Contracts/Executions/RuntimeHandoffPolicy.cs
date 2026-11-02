using System.Text.Json;
using Encoding.Contracts.Flows;

using Encoding.Contracts.Nodes;

namespace Encoding.Contracts.Executions;

public sealed record RuntimeHandoffSelector(string Mode, Guid? TargetNodeId = null, Guid? TargetGroupId = null, string? RequiredLabels = null, bool Resumed = false)
{
    /// <summary>Ancienne forme d'une reprise (ADR 0004), conservée pour lire les lignes déjà écrites : tout worker éligible.</summary>
    public const string ResumeMode = "resume";

    /// <summary>Vrai pour une reprise après perte du worker : la destination du dernier relais est conservée, mais le worker perdu peut revenir.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsResume => Resumed || Mode == ResumeMode;

    public bool Accepts(Guid candidate, Guid previousWorker, NodeIdentityResponse? identity = null) => (IsResume || candidate != previousWorker)
        && (Mode is "eligible" or ResumeMode || Mode == "node" && TargetNodeId == candidate
            || Mode == "selector" && identity is not null
                && (TargetGroupId is null || identity.GroupIds.Contains(TargetGroupId.Value))
                && NodeLabelSelector.Matches(RequiredLabels, identity.EffectiveLabels));
}

/// <summary>Opt-in, bounded policy for starting a fresh attempt after an assigned worker is lost.</summary>
public sealed record RuntimeRetryPolicy(int MaximumRetries, RuntimeHandoffSelector Selector)
{
    public const string ComponentId = "nodes-retry-on-worker-loss";

    public static RuntimeRetryPolicy? Resolve(FlowDefinition flow)
    {
        var nodes = flow.Nodes.Where(node => node.Component.PluginId == "nodes" && node.Component.ComponentId == ComponentId).ToArray();
        if (nodes.Length == 0) return null;
        if (nodes.Length != 1) throw new ArgumentException("Un flow ne peut contenir qu’une seule règle de relance après perte du worker.");
        var configuration = nodes[0].Configuration;
        var retries = configuration.TryGetValue("maxRetries", out var maximum) && maximum.TryGetInt32(out var parsed) ? parsed : 1;
        if (retries is < 1 or > 10) throw new ArgumentException("Le nombre de relances doit être compris entre 1 et 10.");
        Guid? target = null;
        if (configuration.TryGetValue("targetNodeId", out var node) && node.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(node.GetString()))
        {
            if (!Guid.TryParse(node.GetString(), out var id) || id == Guid.Empty)
                throw new ArgumentException("L’identifiant du worker de relance est invalide.");
            target = id;
        }
        Guid? group = null;
        if (configuration.TryGetValue("targetGroupId", out var groupValue) && groupValue.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(groupValue.GetString()))
        {
            if (!Guid.TryParse(groupValue.GetString(), out var id) || id == Guid.Empty)
                throw new ArgumentException("L’identifiant du groupe de relance est invalide.");
            group = id;
        }
        var labels = configuration.TryGetValue("requiredLabels", out var labelValue) && labelValue.ValueKind == JsonValueKind.String
            ? labelValue.GetString() : null;
        if (!string.IsNullOrWhiteSpace(labels) && !NodeLabelSelector.IsValid(labels))
            throw new ArgumentException("Le filtre d’étiquettes de relance est invalide.");
        if (target is not null && (group is not null || !string.IsNullOrWhiteSpace(labels)))
            throw new ArgumentException("Choisissez un worker précis ou un groupe/filtre d’étiquettes pour la relance.");
        var selector = target is not null ? new RuntimeHandoffSelector("node", target)
            : group is not null || !string.IsNullOrWhiteSpace(labels) ? new RuntimeHandoffSelector("selector", TargetGroupId: group, RequiredLabels: labels)
            : new RuntimeHandoffSelector("eligible");
        return new(retries, selector);
    }
}

/// <summary>Shared interpretation of defaults, flow overrides and the explicit relay block.</summary>
public static class RuntimeHandoffPolicy
{
    public static bool IsHandoff(FlowNodeDefinition node) => node.Component.PluginId == "nodes"
        && node.Component.ComponentId == "nodes-handoff";

    public static RuntimeHandoffSelector Resolve(FlowNodeDefinition node,
        IReadOnlyDictionary<string, JsonElement>? pluginConfiguration)
    {
        string? Read(string key)
        {
            if (node.Configuration.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()) && value.GetString() != "inherit") return value.GetString();
            return pluginConfiguration?.TryGetValue(key, out value) == true && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }
        var mode = Read("mode") ?? "eligible";
        if (mode == "eligible") return new(mode);
        if (mode is "node" or "server")
        {
            if (!Guid.TryParse(Read(mode=="server"?"targetServerPeerId":"targetNodeId"), out var target) || target == Guid.Empty)
                throw new ArgumentException("Le relais vers un nœud précis nécessite un identifiant de worker valide.");
            return new(mode, target);
        }
        if (mode == "selector")
        {
            var groupValue = Read("targetGroupId");
            Guid? group = null;
            if (!string.IsNullOrWhiteSpace(groupValue))
            {
                if (!Guid.TryParse(groupValue, out var groupId) || groupId == Guid.Empty)
                    throw new ArgumentException("L’identifiant du groupe destinataire est invalide.");
                group = groupId;
            }
            var labels = Read("requiredLabels");
            if (!string.IsNullOrWhiteSpace(labels) && !NodeLabelSelector.IsValid(labels))
                throw new ArgumentException("Le filtre d’étiquettes du relais est invalide.");
            if (group is null && string.IsNullOrWhiteSpace(labels))
                throw new ArgumentException("Le filtre de relais doit définir un groupe ou des étiquettes.");
            return new(mode, TargetGroupId: group, RequiredLabels: labels);
        }
        throw new ArgumentException("La destination du relais n’est pas prise en charge.");
    }

    /// <summary>Le bloc « Bibliothèque sur ce nœud » exige-t-il aussi un encodeur utilisable du profil pour répondre « oui » ?</summary>
    public static bool LibraryAccessNeedsEncoder(FlowNodeDefinition node) => node.Configuration.TryGetValue("requireProfileEncoder", out var value)
        && value.ValueKind == JsonValueKind.True;

    /// <summary>All possible sequential paths in this segment; stop at the next relay boundary.</summary>
    public static IReadOnlyList<FlowNodeDefinition> Segment(FlowDefinition flow, string entryNodeId, Func<FlowConnectionDefinition, bool>? follow = null)
    {
        var nodes = flow.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var outgoing = flow.Connections.ToLookup(edge => edge.SourceNodeId);
        var pending = new Stack<string>(); pending.Push(entryNodeId);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<FlowNodeDefinition>();
        while (pending.TryPop(out var id))
        {
            if (!seen.Add(id)) continue;
            var node = nodes[id]; result.Add(node);
            if (IsHandoff(node)) continue;
            foreach (var edge in outgoing[id]) if (follow is null || follow(edge)) pending.Push(edge.TargetNodeId);
        }
        return result;
    }
}
