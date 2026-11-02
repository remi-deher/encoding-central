import type {
  FlowComponentConfigurationField,
  FlowComponentConfigurationValue,
  FlowConnectionRequest,
  FlowGraphConnection,
  FlowGraphNode,
} from './flow-component';
import { conditionTreeError } from './condition-tree';

/** Plusieurs chemins alternatifs peuvent converger ; une sortie ne déclenche qu’un chemin. */
export function canConnectFlowGraph(
  nodes: readonly FlowGraphNode[],
  connections: readonly FlowGraphConnection[],
  request: FlowConnectionRequest,
): boolean {
  const source = nodes.find((node) => node.id === request.sourceNodeId);
  const target = nodes.find((node) => node.id === request.targetNodeId);
  if (!source || !target || source.id === target.id || target.component.kind === 'input')
    return false;
  if (!source.component.outputs.some((output) => output.id === request.sourceOutputId))
    return false;
  if (
    connections.some(
      (edge) => edge.sourceNodeId === source.id && edge.sourceOutputId === request.sourceOutputId,
    )
  )
    return false;
  const visited = new Set<string>();
  const pending = [target.id];
  while (pending.length) {
    const id = pending.pop()!;
    if (id === source.id) return false;
    if (visited.has(id)) continue;
    visited.add(id);
    pending.push(
      ...connections.filter((edge) => edge.sourceNodeId === id).map((edge) => edge.targetNodeId),
    );
  }
  return true;
}

export function validateFlowGraph(
  nodes: readonly FlowGraphNode[],
  connections: readonly FlowGraphConnection[],
): readonly string[] {
  const issues: string[] = [];
  const entries = nodes.filter((node) => node.component.kind === 'input');
  if (entries.length !== 1) issues.push('Le flow doit posséder exactement une entrée média.');
  if (new Set(nodes.map((node) => node.id)).size !== nodes.length)
    issues.push('Les identifiants des blocs doivent être uniques.');
  if (
    connections.some(
      (edge, index) =>
        !canConnectFlowGraph(
          nodes,
          connections.filter((_, other) => index !== other),
          edge,
        ),
    )
  )
    issues.push(
      'Le graphe contient une connexion invalide, une sortie utilisée plusieurs fois ou un cycle.',
    );
  if (entries.length === 1) {
    const visited = new Set<string>();
    const pending = [entries[0].id];
    while (pending.length) {
      const id = pending.pop()!;
      if (visited.has(id)) continue;
      visited.add(id);
      pending.push(
        ...connections.filter((edge) => edge.sourceNodeId === id).map((edge) => edge.targetNodeId),
      );
    }
    for (const node of nodes) {
      if (!visited.has(node.id))
        issues.push(`Bloc non relié à l’entrée : ${node.name || node.component.name || node.id}.`);
    }
  }
  for (const node of nodes) {
    for (const field of node.component.configurationSchema?.fields ?? []) {
      const value = node.configuration[field.id];
      const error = flowConfigurationFieldError(field, value);
      if (error)
        issues.push(
          (node.name || node.component.name) +
            ' : ' +
            field.label +
            ' ' +
            (error === 'Ce paramètre est obligatoire.' ? 'est obligatoire.' : error),
        );
    }
  }
  return issues;
}

export function flowConfigurationFieldError(
  field: FlowComponentConfigurationField,
  value: FlowComponentConfigurationValue | undefined,
): string | undefined {
  const empty =
    value === undefined || value === null || (typeof value === 'string' && !value.trim());
  if (empty) return field.required ? 'Ce paramètre est obligatoire.' : undefined;
  switch (field.type) {
    case 'record-list':
      if (!Array.isArray(value)) return 'Une liste de règles est attendue.';
      if (value.length < (field.minItems ?? 0) || value.length > (field.maxItems ?? 64))
        return 'Nombre de lignes hors limites.';
      for (const row of value) {
        if (typeof row !== 'object' || row === null || Array.isArray(row)) return 'Ligne invalide.';
        for (const child of field.itemFields) {
          const error = flowConfigurationFieldError(child, row[child.id] ?? child.defaultValue);
          if (error) return child.label + ' : ' + error;
        }
      }
      return undefined;
    case 'condition-tree':
      return conditionTreeError(value, field.conditionFields);
    case 'text':
    case 'secret':
      return typeof value === 'string' ? undefined : 'Une valeur textuelle est attendue.';
    case 'script-reference':
      return typeof value === 'string' && /^[0-9a-f-]{36}@[1-9]\d*$/i.test(value)
        ? undefined
        : 'Choisissez une version publiée de la bibliothèque de scripts.';
    case 'encoding-profile':
      return typeof value === 'object' && value !== null && 'targets' in value
        ? undefined
        : 'Choisissez un profil de qualité publié.';
    case 'video-encoder':
      return typeof value === 'string' && value.length > 0
        ? undefined
        : 'Choisissez un encodeur compatible avec le profil.';
    case 'boolean':
      return typeof value === 'boolean' ? undefined : 'Une valeur Oui ou Non est attendue.';
    case 'select':
      return typeof value === 'string' && field.options.some((option) => option.value === value)
        ? undefined
        : 'Choisissez une valeur proposée.';
    case 'number':
      if (typeof value !== 'number' || !Number.isFinite(value))
        return 'Un nombre valide est attendu.';
      if (field.min !== undefined && value < field.min) return 'Minimum : ' + field.min + '.';
      if (field.max !== undefined && value > field.max) return 'Maximum : ' + field.max + '.';
      if (field.step && field.step > 0) {
        const increment = (value - (field.min ?? 0)) / field.step;
        if (Math.abs(increment - Math.round(increment)) > 1e-8)
          return 'Pas attendu : ' + field.step + '.';
      }
      return undefined;
  }
}
