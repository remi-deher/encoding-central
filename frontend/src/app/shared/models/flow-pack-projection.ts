import type { FlowComponentPackInstance } from './flow-validation';
import type { FlowGraphConnection, FlowGraphNode } from './flow-component';

export interface FlowPackProjection {
  readonly nodes: readonly FlowGraphNode[];
  readonly connections: readonly FlowGraphConnection[];
  readonly instancesByVisualId: ReadonlyMap<string, FlowComponentPackInstance>;
  readonly instanceByChildId: ReadonlyMap<string, FlowComponentPackInstance>;
}

export function projectFlowPacks(
  nodes: readonly FlowGraphNode[],
  connections: readonly FlowGraphConnection[],
  instances: readonly FlowComponentPackInstance[],
  expanded: ReadonlySet<string> = new Set(),
): FlowPackProjection {
  const byId = new Map(nodes.map((node) => [node.id, node]));
  const instanceByChildId = new Map<string, FlowComponentPackInstance>();
  const instancesByVisualId = new Map<string, FlowComponentPackInstance>();
  const valid = instances.filter(
    (instance) =>
      instance.nodeIds.length > 0 &&
      instance.nodeIds.every((id) => byId.has(id)) &&
      byId.has(instance.inputNodeId),
  );
  for (const instance of valid) {
    instancesByVisualId.set(visualId(instance), instance);
    for (const id of instance.nodeIds) instanceByChildId.set(id, instance);
  }
  const collapsed = valid.filter((instance) => !expanded.has(instance.id));
  const packByChildId = new Map(
    collapsed.flatMap((instance) => instance.nodeIds.map((id) => [id, instance] as const)),
  );
  const packNodes = collapsed.map((instance): FlowGraphNode => {
    const root = byId.get(instance.inputNodeId)!;
    return {
      id: visualId(instance),
      name: instance.name,
      position: root.position,
      configuration: {},
      component: {
        id: 'component-pack',
        name: instance.name,
        description: `Pack réutilisable composé de ${instance.nodeIds.length} composants.`,
        kind: 'pack',
        mediaType: 'general',
        supportedFlowMediaTypes: ['video', 'image', 'music', 'subtitles'],
        pluginId: 'packs',
        pluginName: 'Packs de composants',
        outputs: instance.outputs.map((port) => ({
          id: port.id,
          label: port.label,
          description: `Sortie du pack « ${instance.name} ».`,
        })),
      },
    };
  });
  const projectedNodes = nodes.filter((node) => !packByChildId.has(node.id)).concat(packNodes);
  const projectedConnections: FlowGraphConnection[] = [];
  const seen = new Set<string>();
  for (const edge of connections) {
    const sourcePack = packByChildId.get(edge.sourceNodeId);
    const targetPack = packByChildId.get(edge.targetNodeId);
    if (sourcePack && sourcePack.id === targetPack?.id) continue;
    let sourceNodeId = edge.sourceNodeId;
    let sourceOutputId = edge.sourceOutputId;
    let targetNodeId = edge.targetNodeId;
    if (sourcePack) {
      const port = sourcePack.outputs.find(
        (candidate) =>
          candidate.sourceNodeId === edge.sourceNodeId &&
          candidate.sourceOutputId === edge.sourceOutputId,
      );
      if (!port) continue;
      sourceNodeId = visualId(sourcePack);
      sourceOutputId = port.id;
    }
    if (targetPack) {
      if (edge.targetNodeId !== targetPack.inputNodeId) continue;
      targetNodeId = visualId(targetPack);
    }
    if (seen.has(edge.id)) continue;
    seen.add(edge.id);
    projectedConnections.push({ ...edge, sourceNodeId, sourceOutputId, targetNodeId });
  }
  return {
    nodes: projectedNodes,
    connections: projectedConnections,
    instancesByVisualId,
    instanceByChildId,
  };
}

export function visualId(instance: FlowComponentPackInstance): string {
  return `flow-pack-${instance.id}`;
}
