import type { FlowGraphConnection, FlowGraphNode, FlowComponentOutput } from './flow-component';

export interface FlowSequence {
  readonly id: string;
  readonly steps: readonly FlowStep[];
  readonly returnsTo?: string;
}
export interface FlowStep {
  readonly node: FlowGraphNode;
  readonly reference: boolean;
  readonly branches: readonly {
    readonly output: FlowComponentOutput;
    readonly sequence: FlowSequence;
  }[];
  readonly joinId?: string;
  readonly continuationOutput?: FlowComponentOutput;
}
export interface StructuredFlow {
  readonly main: FlowSequence;
  readonly detached: readonly FlowSequence[];
  readonly references: boolean;
}

/** A projection only: graph edges and node configuration are never rewritten. */
export function structureFlow(
  nodes: readonly FlowGraphNode[],
  edges: readonly FlowGraphConnection[],
  startNodeId?: string,
): StructuredFlow {
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const seen = new Set<string>();
  let references = false;
  const outgoing = (id: string) =>
    edges.filter((e) => e.sourceNodeId === id && byId.has(e.targetNodeId));
  const postMemo = new Map<string, Set<string>>();
  const post = (id: string, trail = new Set<string>()): Set<string> => {
    if (trail.has(id)) return new Set();
    const cached = postMemo.get(id);
    if (cached) return cached;
    const children = outgoing(id);
    const nextTrail = new Set([...trail, id]);
    const sets = children.map((e) => post(e.targetNodeId, nextTrail));
    const result = new Set([
      id,
      ...(sets.length ? [...sets[0]].filter((x) => sets.every((s) => s.has(x))) : []),
    ]);
    postMemo.set(id, result);
    return result;
  };
  const join = (children: readonly FlowGraphConnection[]): string | undefined => {
    if (children.length < 2) return undefined;
    const common = [...post(children[0].targetNodeId)].filter((id) =>
      children.every((e) => post(e.targetNodeId).has(id)),
    );
    // The earliest common postdominator precedes all other common candidates.
    return common.find((id) => common.every((other) => other === id || post(id).has(other)));
  };
  const build = (
    start: string | undefined,
    stop: string | undefined,
    key: string,
  ): FlowSequence => {
    const steps: FlowStep[] = [];
    let id = start;
    while (id && id !== stop) {
      const node = byId.get(id);
      if (!node) break;
      if (seen.has(id)) {
        references = true;
        steps.push({ node, reference: true, branches: [] });
        break;
      }
      seen.add(id);
      const children = outgoing(id);
      if (children.length > 1 || (node.component.kind === 'condition' && children.length)) {
        const merge = join(children);
        const branches = node.component.outputs
          .filter((output) => children.some((edge) => edge.sourceOutputId === output.id))
          .map((output) => ({
            output,
            sequence: build(
              children.find((edge) => edge.sourceOutputId === output.id)?.targetNodeId,
              merge ?? stop,
              key + '/' + node.id + '/' + output.id,
            ),
          }));
        steps.push({ node, reference: false, branches, joinId: merge });
        id = merge;
      } else {
        steps.push({
          node,
          reference: false,
          branches: [],
          continuationOutput: node.component.outputs.find(
            (output) => output.id === children[0]?.sourceOutputId,
          ),
        });
        id = children[0]?.targetNodeId;
      }
    }
    return { id: key, steps, returnsTo: id === stop ? stop : undefined };
  };
  const entry = startNodeId
    ? nodes.find((node) => node.id === startNodeId)
    : nodes.find((n) => n.component.kind === 'input');
  const main = build(entry?.id, undefined, 'main');
  const detached: FlowSequence[] = [];
  for (const node of nodes)
    if (
      !seen.has(node.id) &&
      !edges.some((e) => e.targetNodeId === node.id && !seen.has(e.sourceNodeId))
    )
      detached.push(build(node.id, undefined, 'detached/' + node.id));
  for (const node of nodes)
    if (!seen.has(node.id)) detached.push(build(node.id, undefined, 'detached/' + node.id));
  return { main, detached, references };
}

/** Reorders a simple chain by swapping adjacent steps. Refuses branching/shared nodes. */
export function reorderFlowSequence(
  nodes: readonly FlowGraphNode[],
  edges: readonly FlowGraphConnection[],
  ids: readonly string[],
  from: number,
  to: number,
): readonly FlowGraphConnection[] | null {
  if (from === to) return edges;
  if (from < 0 || to < 0 || from >= ids.length || to >= ids.length) return null;
  let result = [...edges];
  const order = [...ids];
  const direction = from < to ? 1 : -1;
  for (let at = from; at !== to; at += direction) {
    const index = direction === 1 ? at : at - 1;
    const a = nodes.find((n) => n.id === order[index]),
      b = nodes.find((n) => n.id === order[index + 1]);
    if (
      !a ||
      !b ||
      a.component.kind === 'input' ||
      b.component.kind === 'input' ||
      a.component.kind === 'condition' ||
      b.component.kind === 'condition'
    )
      return null;
    const incomingA = result.filter((e) => e.targetNodeId === a.id),
      incomingB = result.filter((e) => e.targetNodeId === b.id);
    const outgoingA = result.filter((e) => e.sourceNodeId === a.id),
      outgoingB = result.filter((e) => e.sourceNodeId === b.id);
    if (
      incomingA.length !== 1 ||
      incomingB.length !== 1 ||
      outgoingA.length !== 1 ||
      outgoingA[0].targetNodeId !== b.id ||
      outgoingB.length > 1
    )
      return null;
    const primaryA = a.component.outputs.find((o) => o.id === 'success') ?? a.component.outputs[0];
    const primaryB = b.component.outputs.find((o) => o.id === 'success') ?? b.component.outputs[0];
    if (
      outgoingA[0].sourceOutputId !== primaryA?.id ||
      (outgoingB[0] && outgoingB[0].sourceOutputId !== primaryB?.id)
    )
      return null;
    const bOutput =
      outgoingB[0]?.sourceOutputId ??
      b.component.outputs.find((o) => o.id === 'success')?.id ??
      b.component.outputs[0]?.id;
    if (!bOutput) return null;
    const link = outgoingA[0],
      tail = outgoingB[0],
      predecessor = incomingA[0];
    result = result
      .filter((e) => e.id !== link.id && e.id !== tail?.id)
      .map((e) => (e.id === predecessor.id ? { ...e, targetNodeId: b.id } : e));
    result.push({ ...link, sourceNodeId: b.id, sourceOutputId: bOutput, targetNodeId: a.id });
    if (tail)
      result.push({
        ...tail,
        sourceNodeId: a.id,
        sourceOutputId: link.sourceOutputId,
        targetNodeId: tail.targetNodeId,
      });
    [order[index], order[index + 1]] = [order[index + 1], order[index]];
  }
  return result;
}
export interface FlowContinuationSource {
  readonly sourceNodeId: string;
  readonly output: FlowComponentOutput;
}
/** Free ends of live paths. Explicit terminal blocks remain terminal. */
export function flowContinuationSources(
  nodes: readonly FlowGraphNode[],
  edges: readonly FlowGraphConnection[],
  sourceNodeId: string,
): readonly FlowContinuationSource[] {
  const sources = new Map<string, FlowContinuationSource>(),
    visited = new Set<string>();
  const visit = (id: string, root = false): void => {
    if (visited.has(id)) return;
    visited.add(id);
    const node = nodes.find((n) => n.id === id);
    if (!node || !node.component.outputs.length) return;
    const outgoing = edges.filter((e) => e.sourceNodeId === id);
    const outputs =
      node.component.kind === 'condition'
        ? node.component.outputs.filter((output) =>
            outgoing.some((edge) => edge.sourceOutputId === output.id),
          )
        : outgoing.length
          ? node.component.outputs.filter((o) => outgoing.some((e) => e.sourceOutputId === o.id))
          : root
            ? []
            : [node.component.outputs.find((o) => o.id === 'success') ?? node.component.outputs[0]];
    for (const output of outputs) {
      const edge = outgoing.find((e) => e.sourceOutputId === output.id);
      if (edge) visit(edge.targetNodeId);
      else sources.set(id + '/' + output.id, { sourceNodeId: id, output });
    }
  };
  visit(sourceNodeId, true);
  return [...sources.values()];
}
