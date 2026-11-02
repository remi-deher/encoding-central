import type { FlowDocument } from './flow-validation';

export interface FlowDifference {
  readonly label: string;
  readonly detail: string;
}
export function compareFlowVersions(
  before: FlowDocument,
  after: FlowDocument,
): readonly FlowDifference[] {
  const normalize = (flow: FlowDocument) => ({
    name: flow.name,
    mediaType: flow.mediaType,
    nodes: Object.fromEntries(flow.nodes.map((node) => [node.id, node])),
    connections: Object.fromEntries(flow.connections.map((edge) => [edge.id, edge])),
  });
  const differences: FlowDifference[] = [];
  const visit = (a: unknown, b: unknown, path: string): void => {
    if (JSON.stringify(a) === JSON.stringify(b)) return;
    if (a !== null && b !== null && typeof a === 'object' && typeof b === 'object') {
      const left = a as Record<string, unknown>,
        right = b as Record<string, unknown>;
      for (const key of new Set([...Object.keys(left), ...Object.keys(right)]))
        visit(left[key], right[key], path + '/' + key.replaceAll('~', '~0').replaceAll('/', '~1'));
    } else
      differences.push({
        label: path,
        detail: (JSON.stringify(a) ?? 'Absent') + ' vers ' + (JSON.stringify(b) ?? 'Absent'),
      });
  };
  visit(normalize(before), normalize(after), '');
  return differences;
}
