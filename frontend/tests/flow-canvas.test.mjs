import assert from 'node:assert/strict';
import test from 'node:test';
import { loadModel } from './model-loader.mjs';
const { layoutFlowCanvas, estimateFlowCanvasNodeSize } = await loadModel('flow-canvas');
const node = (id) => ({ id, position: { x: 0, y: 0 } }),
  edge = (sourceNodeId, targetNodeId) => ({ sourceNodeId, targetNodeId });
test('five decision levels and reconvergences are laid out without graph mutation or overlap', () => {
  const nodes = [
    'entry',
    ...Array.from({ length: 5 }, (_, i) => ['decision' + i, 'alternate' + i]).flat(),
    'join',
    'detached',
  ].map(node);
  const edges = [
    edge('entry', 'decision0'),
    ...Array.from({ length: 5 }, (_, i) => [
      edge('decision' + i, 'alternate' + i),
      edge('alternate' + i, 'join'),
      edge('decision' + i, i === 4 ? 'join' : 'decision' + (i + 1)),
    ]).flat(),
  ];
  const before = JSON.stringify({ nodes, edges }),
    layout = layoutFlowCanvas(nodes, edges);
  assert.equal(JSON.stringify({ nodes, edges }), before);
  assert.equal(layout.positions.size, nodes.length);
  for (const e of edges)
    assert.ok(layout.positions.get(e.targetNodeId).y > layout.positions.get(e.sourceNodeId).y);
  const ps = [...layout.positions.values()];
  for (let i = 0; i < ps.length; i++)
    for (let j = i + 1; j < ps.length; j++)
      assert.ok(Math.abs(ps[i].x - ps[j].x) >= 200 || Math.abs(ps[i].y - ps[j].y) >= 64);
});

test('automatic placement reserves measured space for tall and multi-output blocks', () => {
  const nodes = ['entry', 'left', 'right', 'join'].map(node);
  const edges = [
    edge('entry', 'left'),
    edge('entry', 'right'),
    edge('left', 'join'),
    edge('right', 'join'),
  ];
  const sizes = new Map([
    ['entry', { width: 700, height: 370 }],
    ['left', { width: 340, height: 610 }],
    ['right', { width: 500, height: 180 }],
  ]);
  const layout = layoutFlowCanvas(nodes, edges, sizes);
  const p = layout.positions;
  assert.ok(p.get('left').y >= p.get('entry').y + 370 + 80);
  assert.ok(p.get('join').y >= p.get('left').y + 610 + 80);
  assert.ok(p.get('right').x >= p.get('left').x + 340 + 60);
  assert.deepEqual([...layout.positions], [...layoutFlowCanvas(nodes, edges, sizes).positions]);
});
test('detached nodes, invalid references and cyclic drafts remain visible for repair', () => {
  const nodes = ['a', 'b', 'alone'].map(node);
  const layout = layoutFlowCanvas(nodes, [edge('a', 'b'), edge('b', 'a'), edge('a', 'missing')]);
  assert.equal(layout.positions.size, 3);
  assert.ok(layout.width >= 1400);
});

test('compact fallback reserves port targets and does not assume a fixed width', () => {
  const nodes = [
    { id: 'short', component: { outputs: [{}] } },
    { id: 'many', component: { outputs: Array.from({ length: 12 }, () => ({})) } },
    { id: 'end', component: { outputs: [] } },
  ];
  const layout = layoutFlowCanvas(nodes, [edge('short', 'end')]);
  assert.ok(layout.positions.get('many').x >= layout.positions.get('short').x + 200 + 64);
  assert.equal(layout.positions.get('end').y - layout.positions.get('short').y, 64 + 88);
});

test('initial estimates account for descriptive titles before DOM measurements arrive', () => {
  const short = {
    id: 'short',
    name: 'Source',
    component: { name: 'Source', pluginName: 'Base', outputs: [{}] },
  };
  const long = {
    ...short,
    id: 'long',
    name: 'Actualiser la bibliothèque multimédia après validation',
  };
  assert.ok(estimateFlowCanvasNodeSize(long).width > estimateFlowCanvasNodeSize(short).width);
  assert.ok(estimateFlowCanvasNodeSize(long).width <= 336);
  const many = {
    ...short,
    component: { ...short.component, outputs: Array.from({ length: 12 }, () => ({})) },
  };
  assert.ok(estimateFlowCanvasNodeSize(many).width >= 12 * 40);
});
