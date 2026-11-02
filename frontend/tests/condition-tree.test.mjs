import assert from 'node:assert/strict';
import test from 'node:test';
import { loadModel } from './model-loader.mjs';

const {
  conditionTreeError,
  defaultConditionRule,
  updateConditionTree,
  conditionTreeSummary,
  conditionNodeCount,
} = await loadModel('condition-tree');
const { getFlowConfigurationSchema } = await loadModel('flow-component');
const { flowConfigurationFieldError } = await loadModel('flow-graph');
const ops = (values) => values.map((value) => ({ value, label: value }));
const fields = [
  {
    id: 'file.extension',
    label: 'Extension',
    type: 'text',
    operators: ops(['eq', 'contains', 'exists', 'missing']),
    supportedFlowMediaTypes: ['video', 'image', 'music', 'subtitles'],
    requiresMetadata: false,
    supportsScope: false,
    requiresVariableName: false,
  },
  {
    id: 'video.height',
    label: 'Hauteur',
    type: 'number',
    operators: ops(['gt', 'eq']),
    supportedFlowMediaTypes: ['video', 'image'],
    requiresMetadata: true,
    supportsScope: true,
    requiresVariableName: false,
  },
  {
    id: 'variable.boolean',
    label: 'Variable',
    type: 'boolean',
    operators: ops(['eq', 'exists']),
    supportedFlowMediaTypes: ['video', 'image', 'music', 'subtitles'],
    requiresMetadata: false,
    supportsScope: false,
    requiresVariableName: true,
  },
];
const rule = (field = 'file.extension', operator = 'eq', value = 'mkv') => ({
  kind: 'rule',
  field,
  operator,
  value,
});
const group = (...children) => ({ kind: 'group', match: 'all', children });

test('des conditions ET / OU imbriquées conservent leurs types et leur structure', () => {
  const tree = group(rule(), { ...group(rule('video.height', 'gt', 1080)), match: 'any' });
  assert.equal(conditionTreeError(tree, fields), undefined);
  const changed = updateConditionTree(tree, [1, 0], (node) => ({
    ...node,
    value: 2160,
    scope: 'source',
  }));
  assert.equal(changed.children[1].children[0].value, 2160);
  assert.equal(tree.children[1].children[0].value, 1080);
  assert.equal(conditionNodeCount(changed), 4);
  assert.match(conditionTreeSummary(changed, fields), / ET /);
});
test('les valeurs numériques et booléennes ne deviennent pas des textes', () => {
  assert.equal(
    conditionTreeError(group(rule('video.height', 'gt', '1080')), fields),
    'Un nombre valide est attendu.',
  );
  assert.equal(
    conditionTreeError(
      group({ ...rule('variable.boolean', 'eq', 'true'), variableName: 'enabled' }),
      fields,
    ),
    'Une valeur Oui ou Non est attendue.',
  );
  assert.equal(
    conditionTreeError(
      group({ ...rule('variable.boolean', 'eq', false), variableName: 'enabled' }),
      fields,
    ),
    undefined,
  );
  assert.equal(defaultConditionRule(fields[1]).value, 0);
  assert.equal(defaultConditionRule(fields[2]).value, true);
});
test('les groupes incomplets, champs inconnus et opérateurs incompatibles bloquent la publication', () => {
  assert.ok(conditionTreeError(group(), fields));
  assert.ok(conditionTreeError(group(rule('unknown')), fields));
  assert.ok(conditionTreeError(group(rule('video.height', 'contains', '1080')), fields));
  assert.ok(conditionTreeError(group({ ...rule(), scope: 'source' }), fields));
  assert.ok(conditionTreeError(group({ ...rule(), code: 'script' }), fields));
  assert.ok(
    flowConfigurationFieldError(
      { id: 'rules', type: 'condition-tree', conditionFields: fields },
      group(),
    ),
  );
});
test('la profondeur et le nombre d’éléments sont bornés', () => {
  let tree = rule();
  for (let index = 0; index < 8; index++) tree = group(tree);
  assert.ok(conditionTreeError(tree, fields));
  assert.ok(conditionTreeError(group(...Array.from({ length: 100 }, () => rule())), fields));
});
test('un opérateur de présence n’exige pas de valeur mais exige le nom de variable', () => {
  const node = {
    kind: 'rule',
    field: 'variable.boolean',
    operator: 'exists',
    variableName: 'enabled',
  };
  assert.equal(conditionTreeError(group(node), fields), undefined);
  assert.ok(conditionTreeError(group({ ...node, variableName: '' }), fields));
});
test('le schéma commun filtre les critères et préréglages pour le média du flow', () => {
  const schema = {
    version: 1,
    fields: [
      {
        id: 'rules',
        type: 'condition-tree',
        conditionFields: fields,
        conditionPresets: [
          {
            id: 'video',
            expression: group(rule('video.height', 'gt', 1080)),
            supportedFlowMediaTypes: ['video'],
          },
          {
            id: 'extension',
            expression: group(rule()),
            supportedFlowMediaTypes: ['video', 'music'],
          },
        ],
      },
    ],
  };
  const filtered = getFlowConfigurationSchema(schema, 'music').fields[0];
  assert.deepEqual(
    filtered.conditionFields.map((field) => field.id),
    ['file.extension', 'variable.boolean'],
  );
  assert.deepEqual(
    filtered.conditionPresets.map((preset) => preset.id),
    ['extension'],
  );
});
