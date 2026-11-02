using System.Text.Json;
using Encoding.Api.Flows;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Flows.Conditions;
using Encoding.Contracts.Nodes;

namespace Encoding.Api.Executions;

public sealed record NodePlacementCandidate(
    Guid NodeId,
    string? Name,
    bool IsWorker,
    bool IsOnline,
    IReadOnlyList<NodeToolCapability> Capabilities,
    NodeInventory? Inventory,
    NodeIdentityResponse? Identity,
    LibraryNodeAccessInfo? Access,
    bool HasAvailableCapacity = true);

public sealed record LibraryNodeAccessInfo(
    Guid LibraryId,
    string Mode,
    bool IsEnabled,
    bool CanRead,
    bool CanWrite,
    string? LocalPath,
    Guid? TransferSourceNodeId);

public sealed record VideoTranscodeRequirement(
    string Codec,
    string Mode,
    string? EncoderId,
    EncodingProfileSnapshot? Profile,
    string? HardwareAcceleration,
    bool RetryWithCpu);

public sealed record NodeRunConstraint(
    Guid? TargetNodeId,
    Guid? TargetGroupId,
    string? RequiredLabels);

public sealed record CapabilityConstraint(
    string ToolId,
    string? MinimumVersion,
    string? RequiredCapability,
    string? RequiredEncoder,
    string? EncoderKind);

public sealed record FlowPathRequirements(
    IReadOnlyList<string> NodeIds,
    bool RequiresDirectAccess,
    bool PrefersDirectAccess,
    bool RequiresWriteAccess,
    IReadOnlySet<string> RequiredTools,
    IReadOnlySet<string> RequiredPlugins,
    IReadOnlyList<VideoTranscodeRequirement> VideoTranscodes,
    IReadOnlySet<string> AudioEncoders,
    IReadOnlySet<string> FontSets,
    IReadOnlyList<NodeRunConstraint> RunConstraints,
    IReadOnlyList<CapabilityConstraint> CapabilityConstraints);

/// <summary>
/// Planificateur de placement de flow de bout en bout (ADR 0024).
/// Analyse les exigences complètes de chaque chemin et sélectionne dès l'entrée le nœud optimal
/// pour minimiser les transferts réseau (priorité accès direct, plan en 3 phases si transcode distant).
/// </summary>
public sealed partial class FlowPlacementPlanner(FlowComponentRegistry registry)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public FlowPlacementPlan Plan(
        FlowDefinition flow,
        JsonElement? sourceMetadata,
        IReadOnlyList<NodePlacementCandidate> candidates,
        DateTimeOffset now)
    {
        var paths = ExtractPathRequirements(flow, sourceMetadata, candidates);
        if (paths.Count == 0)
        {
            return new(
                FlowPlacementPlan.StrategyImpossible,
                null, null, null,
                "Placement impossible : aucun chemin d’exécution valide dans le flow.",
                [], ["aucun_chemin_valide"], now);
        }

        var allRequiredCaps = CollectCapabilitiesNames(paths);
        var hasFixedTarget = flow.Nodes.Any(node => node.Component.PluginId == "nodes"
            && (node.Component.ComponentId == "nodes-run-on-node"
                || node.Component.ComponentId == "nodes-handoff" && node.Configuration.TryGetValue("mode",out var mode)
                    && mode.ValueKind==JsonValueKind.String && mode.GetString() is "node" or "selector"));
        var capacityCandidates = candidates.Any(candidate=>candidate.HasAvailableCapacity)
            ? candidates.Where(candidate=>candidate.HasAvailableCapacity).ToArray() : candidates;
        var placementCandidates = hasFixedTarget ? candidates : capacityCandidates;
        // Un relais explicite est une frontière d'exécution du flow. Les stratégies unifiées
        // ne doivent pas masquer cette frontière ni annoncer un nombre de transferts inférieur.
        var hasExplicitHandoff = paths.SelectMany(path => path.NodeIds)
            .Select(id => flow.Nodes.First(node => node.Id == id)).Any(IsWorkerHandoff);

        // 1. Nœuds d'accès direct en ligne
        var directAccessNodes = candidates
            .Where(c => c.IsOnline && c.IsWorker && c.Access is { IsEnabled: true, Mode: "direct", CanRead: true, LocalPath: not null })
            .ToList();

        // Cas 1 : Priorité 1 — Un nœud d'accès direct peut exécuter tous les chemins applicables
        foreach (var directNode in placementCandidates.Where(c=>directAccessNodes.Any(direct=>direct.NodeId==c.NodeId) && !hasExplicitHandoff))
        {
            if (paths.All(p => NodeSatisfiesPath(directNode, p, out _)))
            {
                var nodeLabel = directNode.Name ?? directNode.NodeId.ToString("D");
                return new(
                    FlowPlacementPlan.StrategyDirectUnified,
                    directNode.NodeId,
                    directNode.NodeId,
                    null,
                    $"Placement direct unifié : le nœud d’accès local {nodeLabel} prend l’intégralité du flow (0 transfert réseau).",
                    allRequiredCaps,
                    [],
                    now);
            }
        }

        // Priorité 2 : essayer d'abord un worker capable de tout le flow, même s'il faut lui transférer la source.
        var transferWorkers = placementCandidates
            .Where(c => c.IsOnline && c.IsWorker && c.Access is { IsEnabled: true, CanRead: true })
            .ToList();

        var canPublishAtSource = directAccessNodes.Any(candidate => candidate.Access is { CanWrite: true });
        foreach (var worker in transferWorkers.Where(_ => !hasExplicitHandoff))
        {
            if ((!paths.Any(path => path.RequiresWriteAccess) || canPublishAtSource)
                && paths.All(path => NodeSatisfiesPath(worker, path with { RequiresWriteAccess = false }, out _)))
            {
                var workerLabel = worker.Name ?? worker.NodeId.ToString("D");
                return new(
                    FlowPlacementPlan.StrategyTransferWorker,
                    worker.NodeId,
                    directAccessNodes.FirstOrDefault()?.NodeId,
                    null,
                    $"Placement avec transfert : le worker {workerLabel} prend la tâche via transfert WebDAV.",
                    allRequiredCaps,
                    [],
                    now,
                    flow.Nodes.ToDictionary(node => node.Id, _ => worker.NodeId, StringComparer.Ordinal));
            }
        }

        // Priorité 3 : minimiser exactement le nombre de transitions sur les arcs du graphe retenu.
        // Les branches non résolues sont planifiées de façon conservatrice :
        // tous leurs arcs sont pris en compte. Les conditions natives déjà évaluables sur les métadonnées
        // sont élaguées par ExtractPathRequirements avant cette étape.
        var segmented = PlanSegments(flow, paths, placementCandidates, now,sourceMetadata);
        if (segmented is not null) return segmented;

        // Cas 4 : Aucun nœud ne convient
        var missingReport = new List<string>();
        foreach (var p in paths)
        {
            var pathMissing = new List<string>();
            foreach (var c in candidates.Where(cand => cand.IsOnline && cand.IsWorker))
            {
                NodeSatisfiesPath(c, p, out var m);
                pathMissing.AddRange(m);
            }
            missingReport.AddRange(pathMissing);
        }
        var distinctMissing = missingReport.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinctMissing.Count == 0) distinctMissing.Add("aucun_worker_disponible");

        return new(
            FlowPlacementPlan.StrategyImpossible,
            null,
            null,
            null,
            $"Placement impossible : aucun worker ne satisfait les capacités requises ({string.Join(", ", distinctMissing)}).",
            allRequiredCaps,
            distinctMissing,
            now);
    }

    private FlowPlacementPlan? PlanSegments(FlowDefinition flow, IReadOnlyList<FlowPathRequirements> paths,
        IReadOnlyList<NodePlacementCandidate> candidates, DateTimeOffset now,JsonElement? sourceMetadata)
    {
        var reachable = paths.SelectMany(path => path.NodeIds).ToHashSet(StringComparer.Ordinal);
        var nodes = flow.Nodes.Where(node => reachable.Contains(node.Id)).ToDictionary(node => node.Id, StringComparer.Ordinal);
        var pathEdges=paths.SelectMany(path=>path.NodeIds.Zip(path.NodeIds.Skip(1),(source,target)=>(source,target))).ToHashSet();
        var incoming = flow.Connections.Where(edge => pathEdges.Contains((edge.SourceNodeId,edge.TargetNodeId)))
            .GroupBy(edge => edge.TargetNodeId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var roots = nodes.Keys.Where(id => !incoming.ContainsKey(id)).ToArray();
        if (roots.Length != 1) return null;

        var ordered = new List<string>();
        var degrees=incoming.ToDictionary(pair=>pair.Key,pair=>pair.Value.Length,StringComparer.Ordinal);
        var ready=new Queue<string>(roots.Order(StringComparer.Ordinal));
        while(ready.TryDequeue(out var current))
        {
            ordered.Add(current);
            foreach(var edge in flow.Connections.Where(edge=>edge.SourceNodeId==current && pathEdges.Contains((edge.SourceNodeId,edge.TargetNodeId))))
            {
                degrees[edge.TargetNodeId]--;
                if(degrees[edge.TargetNodeId]==0) ready.Enqueue(edge.TargetNodeId);
            }
        }
        if (ordered.Count != nodes.Count) return null;

        var requirements = nodes.ToDictionary(pair => pair.Key, pair => BuildRequirements([pair.Value],sourceMetadata), StringComparer.Ordinal);
        var directRequired=new HashSet<string>(StringComparer.Ordinal);
        foreach(var path in paths)
        {
            var direct=false;
            foreach(var id in path.NodeIds)
            {
                var node=nodes[id];
                if(node.Component.PluginId=="nodes" && node.Component.ComponentId=="nodes-require-direct-access"
                    && !(node.Configuration.TryGetValue("mode",out var directMode) && directMode.ValueKind==JsonValueKind.String && directMode.GetString()=="prefer")) direct=true;
                if(direct) directRequired.Add(id);
                if(RuntimeHandoffPolicy.IsHandoff(node)) direct=false;
            }
        }
        foreach(var id in directRequired) requirements[id]=requirements[id] with { RequiresDirectAccess=true };
        var available = requirements.ToDictionary(pair => pair.Key,
            pair => candidates.Where(candidate => candidate.IsOnline && candidate.IsWorker
                && (candidate.Access?.Mode=="direct" ? NodeSatisfiesPath(candidate,pair.Value,out _)
                    : NodeSatisfiesPath(candidate,pair.Value with { RequiresWriteAccess=false },out _))
                && FitsExplicitHandoff(flow,incoming.GetValueOrDefault(pair.Key)??[],candidate))
                .Select(candidate => candidate.NodeId).ToArray(), StringComparer.Ordinal);
        var missingNodes = available.Where(pair => pair.Value.Length == 0).Select(pair => pair.Key).ToArray();
        if (missingNodes.Length > 0) return null;

        const int searchBudget=500_000;
        var explored=0; var bestCost=int.MaxValue;
        Dictionary<string,Guid>? best=null;
        var assignments=new Dictionary<string,Guid>(StringComparer.Ordinal);
        void Search(int index,int cost)
        {
            if(cost>=bestCost || ++explored>searchBudget) return;
            if(index==ordered.Count) { bestCost=cost; best=new(assignments,StringComparer.Ordinal); return; }
            var id=ordered[index];
            var parents=incoming.GetValueOrDefault(id)??[];
            foreach(var worker in available[id]
                .OrderBy(candidate=>candidates.First(item=>item.NodeId==candidate).Access?.Mode=="direct"?0:1)
                .ThenBy(candidate=>candidate))
            {
                if(parents.Any(edge => flow.Nodes.First(node => node.Id == edge.SourceNodeId) is { } source
                    && IsWorkerHandoff(source)
                    && assignments.TryGetValue(edge.SourceNodeId,out var priorWorker) && priorWorker==worker)) continue;
                var added=parents.Count(edge=>assignments.TryGetValue(edge.SourceNodeId,out var prior)&&prior!=worker);
                if(cost+added>=bestCost) continue;
                assignments[id]=worker; Search(index+1,cost+added); assignments.Remove(id);
                if(explored>searchBudget) return;
            }
        }
        Search(0,0);
        if(explored>searchBudget)
            return new(FlowPlacementPlan.StrategyImpossible,null,null,null,
                "Placement impossible à optimiser dans la limite de recherche du graphe ; simplifiez les branches ou réduisez le nombre de workers candidats.",
                CollectCapabilitiesNames(paths),["placement_search_limit"],now);
        if(best is null) return null;
        assignments=best;

        var distinctWorkers = assignments.Values.Distinct().Count();
        if (distinctWorkers == 1)
        {
            var singleWorkerId = assignments[roots[0]];
            var singleWorker = candidates.FirstOrDefault(c => c.NodeId == singleWorkerId);
            var directAccess = candidates.FirstOrDefault(candidate => candidate.IsOnline && candidate.IsWorker
                && candidate.Access is { IsEnabled: true, Mode: "direct", CanRead: true, CanWrite: true, LocalPath: not null });
            if (singleWorker?.Access is { IsEnabled: true, Mode: "direct", CanRead: true, LocalPath: not null })
            {
                var nodeLabel = singleWorker.Name ?? singleWorkerId.ToString("D");
                return new(FlowPlacementPlan.StrategyDirectUnified, singleWorkerId, singleWorkerId, null,
                    $"Placement direct unifié : le nœud d’accès local {nodeLabel} prend l’intégralité du flow (0 transfert réseau).",
                    CollectCapabilitiesNames(paths), [], now, assignments);
            }
            return new(FlowPlacementPlan.StrategyTransferWorker, singleWorkerId, directAccess?.NodeId, null,
                $"Placement avec transfert : le worker {singleWorker?.Name ?? singleWorkerId.ToString("D")} prend la tâche via transfert WebDAV.",
                CollectCapabilitiesNames(paths), [], now, assignments);
        }
        var accessNode = candidates.FirstOrDefault(candidate => candidate.IsOnline && candidate.IsWorker
            && candidate.Access is { IsEnabled: true, Mode: "direct", CanRead: true, CanWrite: true, LocalPath: not null });
        return new(FlowPlacementPlan.StrategySegmented, assignments.GetValueOrDefault(roots[0]), accessNode?.NodeId,
            null,
            "Placement segmenté : les blocs sont attribués à des workers compatibles ; les transitions suivent les checkpoints durables. Les branches non déterminées sont planifiées sur l’union de leurs chemins.",
            CollectCapabilitiesNames(paths), [], now, assignments);
    }

    private static bool FitsExplicitHandoff(FlowDefinition flow,IReadOnlyList<FlowConnectionDefinition> incoming,NodePlacementCandidate candidate)
    {
        foreach(var edge in incoming)
        {
            var source=flow.Nodes.FirstOrDefault(node=>node.Id==edge.SourceNodeId);
            if(source is null || !RuntimeHandoffPolicy.IsHandoff(source)) continue;
            var mode=source.Configuration.TryGetValue("mode",out var configured)?configured.GetString():"eligible";
            if(mode=="node" && source.Configuration.TryGetValue("targetNodeId",out var target)
                && Guid.TryParse(target.GetString(),out var id) && id!=candidate.NodeId) return false;
            if(mode=="selector")
            {
                Guid? group=null;
                if(source.Configuration.TryGetValue("targetGroupId",out var groupValue) && Guid.TryParse(groupValue.GetString(),out var groupId)) group=groupId;
                var labels=source.Configuration.TryGetValue("requiredLabels",out var labelValue)?labelValue.GetString():null;
                if(group is not null && candidate.Identity?.GroupIds.Contains(group.Value)!=true
                    || !NodeLabelSelector.Matches(labels,candidate.Identity?.EffectiveLabels)) return false;
            }
        }
        return true;
    }

    private static bool IsWorkerHandoff(FlowNodeDefinition node) => RuntimeHandoffPolicy.IsHandoff(node)
        && !(node.Configuration.TryGetValue("mode",out var mode) && mode.ValueKind==JsonValueKind.String && mode.GetString()=="server");
}
