import assert from 'node:assert/strict';
import test from 'node:test';
import { loadModel } from './model-loader.mjs';
const { flowBlockSummary } = await loadModel('flow-block-summary');

test('compact blocks explain declarative conditions without exposing secrets or scripts', () => {
  const node = {
    component: {
      configurationSchema: {
        version: 1,
        fields: [
          {
            id: 'rules',
            type: 'condition-tree',
            label: 'Règles',
            conditionFields: [],
            defaultValue: {
              kind: 'group',
              match: 'all',
              children: [{ kind: 'rule', field: 'codec', operator: 'equals', value: 'hevc' }],
            },
          },
          { id: 'secret', type: 'secret', inline: true, label: 'Secret' },
          { id: 'code', type: 'text', multiline: true, inline: true, label: 'Script' },
        ],
      },
    },
    configuration: { secret: 'never-display', code: 'private-script' },
  };
  const summary = flowBlockSummary(node, 'video');
  assert.equal(summary, 'codec equals hevc');
  assert.ok(!summary.includes('never-display'));
  assert.ok(!summary.includes('private-script'));
});
