import type { MediaType } from './media-type';

export interface ConditionGroup {
  readonly kind: 'group';
  readonly match: 'all' | 'any';
  readonly children: readonly ConditionExpression[];
}
export interface ConditionRule {
  readonly kind: 'rule';
  readonly field: string;
  readonly operator: string;
  readonly value?: string | number | boolean | null;
  readonly variableName?: string | null;
  readonly scope?: 'current' | 'source';
}
export type ConditionExpression = ConditionGroup | ConditionRule;
export interface ConditionFieldDefinition {
  readonly id: string;
  readonly label: string;
  readonly type: 'text' | 'number' | 'boolean' | 'text-list';
  readonly operators: readonly { readonly value: string; readonly label: string }[];
  readonly supportedFlowMediaTypes: readonly MediaType[];
  readonly requiresMetadata: boolean;
  readonly supportsScope: boolean;
  readonly requiresVariableName: boolean;
  readonly options?: readonly { readonly value: string; readonly label: string }[];
  readonly unit?: string;
}
export interface ConditionPreset {
  readonly id: string;
  readonly label: string;
  readonly expression: ConditionGroup;
  readonly supportedFlowMediaTypes: readonly MediaType[];
}
export const CONDITION_MAX_DEPTH = 8;
export const CONDITION_MAX_NODES = 100;

export function conditionTreeError(
  value: unknown,
  fields: readonly ConditionFieldDefinition[],
): string | undefined {
  let count = 0;
  function validate(value: unknown, depth: number): string | undefined {
    if (++count > CONDITION_MAX_NODES || depth > CONDITION_MAX_DEPTH)
      return 'Maximum : 100 éléments et 8 niveaux de conditions.';
    if (!value || typeof value !== 'object' || Array.isArray(value)) return 'Condition inconnue.';
    const node = value as Record<string, unknown>;
    if (node['kind'] === 'group') {
      if (Object.keys(node).some((key) => !['kind', 'match', 'children'].includes(key)))
        return 'Paramètre de groupe inconnu.';
      if (!['all', 'any'].includes(node['match'] as string) || !Array.isArray(node['children']))
        return 'Choisissez ET ou OU pour chaque groupe.';
      if (!node['children'].length) return 'Ajoutez au moins une condition dans chaque groupe.';
      for (const child of node['children']) {
        const error = validate(child, depth + 1);
        if (error) return error;
      }
      return undefined;
    }
    if (
      node['kind'] !== 'rule' ||
      Object.keys(node).some(
        (key) => !['kind', 'field', 'operator', 'value', 'variableName', 'scope'].includes(key),
      )
    )
      return 'Condition inconnue.';
    const field = fields.find((field) => field.id === node['field']);
    if (!field) return 'Ce champ de condition n’est pas disponible pour ce type de flow.';
    if (!field.operators.some((operator) => operator.value === node['operator']))
      return 'Cet opérateur n’est pas disponible pour ce champ.';
    if (
      node['scope'] !== undefined &&
      (!['current', 'source'].includes(node['scope'] as string) ||
        (!field.supportsScope && node['scope'] !== 'current'))
    )
      return 'L’instantané demandé n’est pas disponible pour ce champ.';
    if (
      field.requiresVariableName &&
      (typeof node['variableName'] !== 'string' ||
        !node['variableName'].trim() ||
        node['variableName'].length > 128)
    )
      return 'Indiquez le nom de la variable.';
    if (
      !field.requiresVariableName &&
      node['variableName'] !== undefined &&
      node['variableName'] !== null
    )
      return 'Ce champ n’utilise pas de nom de variable.';
    if (node['operator'] === 'exists' || node['operator'] === 'missing') return undefined;
    const scalar = node['value'];
    if (scalar === null || scalar === undefined) return 'Indiquez la valeur à comparer.';
    if (field.type === 'number')
      return typeof scalar === 'number' &&
        Number.isFinite(scalar) &&
        Math.abs(scalar) <= 7.922816251426433e28
        ? undefined
        : 'Un nombre valide est attendu.';
    if (field.type === 'boolean')
      return typeof scalar === 'boolean' ? undefined : 'Une valeur Oui ou Non est attendue.';
    return typeof scalar === 'string' && scalar.length <= 4096
      ? undefined
      : 'Une valeur textuelle est attendue.';
  }
  if (!value || typeof value !== 'object' || (value as ConditionGroup).kind !== 'group')
    return 'Les conditions doivent commencer par un groupe ET ou OU.';
  return validate(value, 1);
}

export function defaultConditionRule(field: ConditionFieldDefinition): ConditionRule {
  return {
    kind: 'rule',
    field: field.id,
    operator: field.operators[0].value,
    value:
      field.type === 'number'
        ? 0
        : field.type === 'boolean'
          ? true
          : (field.options?.[0]?.value ?? ''),
    ...(field.requiresVariableName ? { variableName: '' } : {}),
  };
}
export function updateConditionTree(
  root: ConditionGroup,
  path: readonly number[],
  change: (node: ConditionExpression) => ConditionExpression,
): ConditionGroup {
  function update(node: ConditionExpression, indexes: readonly number[]): ConditionExpression {
    if (!indexes.length) return change(node);
    if (node.kind !== 'group' || !node.children[indexes[0]]) return node;
    return {
      ...node,
      children: node.children.map((child, index) =>
        index === indexes[0] ? update(child, indexes.slice(1)) : child,
      ),
    };
  }
  return update(root, path) as ConditionGroup;
}
export function conditionNodeCount(node: ConditionExpression): number {
  return (
    1 +
    (node.kind === 'group'
      ? node.children.reduce((count, child) => count + conditionNodeCount(child), 0)
      : 0)
  );
}
export function conditionTreeSummary(
  value: unknown,
  fields: readonly ConditionFieldDefinition[],
): string {
  if (!value || typeof value !== 'object' || (value as ConditionGroup).kind !== 'group')
    return 'Conditions à configurer';
  function describe(node: ConditionExpression): string {
    if (node.kind === 'group')
      return '(' + node.children.map(describe).join(node.match === 'all' ? ' ET ' : ' OU ') + ')';
    const field = fields.find((field) => field.id === node.field);
    const operator =
      field?.operators.find((operator) => operator.value === node.operator)?.label ?? node.operator;
    const label =
      (field?.requiresVariableName
        ? 'Variable ' + (node.variableName || 'à renseigner')
        : (field?.label ?? node.field)) +
      (field?.supportsScope ? (node.scope === 'source' ? ' (origine)' : ' (courant)') : '');
    const value =
      node.operator === 'exists' || node.operator === 'missing'
        ? ''
        : ' ' + String(node.value ?? '');
    return label + ' ' + operator + value;
  }
  const root = value as ConditionGroup;
  if (!root.children.length) return 'Conditions à configurer';
  return describe(root).slice(1, -1);
}
