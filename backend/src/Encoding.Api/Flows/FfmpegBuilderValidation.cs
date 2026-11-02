using Encoding.Contracts.Flows;

namespace Encoding.Api.Flows;

/// <summary>Checks every reachable scope state, including error edges and alternative branches.</summary>
internal static class FfmpegBuilderValidation
{
    public static IEnumerable<FlowValidationIssue> Validate(FlowDefinition flow, string entry)
    {
        var nodes = flow.Nodes.Where(node => node?.Component is not null && !string.IsNullOrWhiteSpace(node.Id)).GroupBy(node=>node.Id).ToDictionary(group=>group.Key,group=>group.First());
        var edges = flow.Connections.Where(edge => edge is not null && !string.IsNullOrWhiteSpace(edge.SourceNodeId)
            && !string.IsNullOrWhiteSpace(edge.TargetNodeId)).GroupBy(edge => edge.SourceNodeId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var pending = new Queue<(string Node, bool Open)>();
        var seen = new HashSet<(string, bool)>();
        pending.Enqueue((entry, false));
        while (pending.TryDequeue(out var state))
        {
            if (!seen.Add(state) || !nodes.TryGetValue(state.Node, out var node)) continue;
            var id = node.Component.ComponentId;
            var open = state.Open;
            string? error = null;
            if (id == FfmpegBuilderPolicy.Start)
            {
                if (open) error = "Un builder FFmpeg est déjà ouvert sur ce chemin.";
                open = true;
            }
            else if (id == FfmpegBuilderPolicy.Execute)
            {
                if (!open) error = "Exécuter FFmpeg nécessite un début de préparation sur chaque chemin entrant.";
                open = false;
            }
            else if (open && !FfmpegBuilderPolicy.CanPass(node.Component.PluginId, id))
                error = "Fermez le builder avec Exécuter FFmpeg avant une opération autonome, un script ou une fin de flow.";
            if (error is not null) yield return new("builder.scope.invalid", "/nodes/" + node.Id, error);
            var outgoing = edges.GetValueOrDefault(state.Node) ?? [];
            if (open && outgoing.Length == 0)
                yield return new("builder.scope.unclosed", "/nodes/" + node.Id, "Ce chemin termine une préparation FFmpeg sans exécuteur.");
            foreach (var edge in outgoing)
                pending.Enqueue((edge.TargetNodeId, id == FfmpegBuilderPolicy.Start && edge.SourceOutputId == "error" ? state.Open : open));
        }
    }
}
