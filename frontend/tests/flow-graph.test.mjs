import assert from 'node:assert/strict';
import test from 'node:test';
import { loadModel } from './model-loader.mjs';

// Le modèle pur est vérifié sans serveur Angular ni services externes.
const { canConnectFlowGraph, validateFlowGraph } = await loadModel('flow-graph');
const node = (id, kind = 'action', outputs = ['success', 'error']) => ({
  id,
  configuration: {},
  component: { kind, outputs: outputs.map((id) => ({ id })) },
});
const edge = (sourceNodeId, sourceOutputId, targetNodeId) => ({
  sourceNodeId,
  sourceOutputId,
  targetNodeId,
});
const nodes = [
  node('input', 'input', ['success']),
  node('choice', 'condition', ['yes', 'no']),
  node('a'),
  node('b'),
  node('join'),
  node('fallback'),
  node('end', 'terminal', []),
];
const branches = [
  edge('input', 'success', 'choice'),
  edge('choice', 'yes', 'a'),
  edge('choice', 'no', 'b'),
  edge('a', 'success', 'join'),
];

test('des chemins alternatifs rejoignent un même bloc', () => {
  assert.equal(canConnectFlowGraph(nodes, branches, edge('b', 'success', 'join')), true);
});
test('la sortie erreur peut rejoindre une action de secours', () => {
  assert.equal(canConnectFlowGraph(nodes, branches, edge('a', 'error', 'fallback')), true);
});
test('une sortie ne lance pas plusieurs chemins ni une connexion en double', () => {
  assert.equal(canConnectFlowGraph(nodes, branches, edge('a', 'success', 'fallback')), false);
  assert.equal(canConnectFlowGraph(nodes, branches, edge('a', 'success', 'join')), false);
});
test('une entrée média ne reçoit aucune connexion', () => {
  assert.equal(canConnectFlowGraph(nodes, [], edge('a', 'success', 'input')), false);
});
test('un cycle traversant une reconvergence et une auto-connexion sont refusés', () => {
  const graph = [...branches, edge('b', 'success', 'join')];
  assert.equal(canConnectFlowGraph(nodes, graph, edge('join', 'success', 'choice')), false);
  assert.equal(canConnectFlowGraph(nodes, [], edge('a', 'error', 'a')), false);
});
test('une sortie absente, un nœud absent ou un départ terminal sont refusés', () => {
  assert.equal(canConnectFlowGraph(nodes, [], edge('a', 'unknown', 'b')), false);
  assert.equal(canConnectFlowGraph(nodes, [], edge('a', 'success', 'unknown')), false);
  assert.equal(canConnectFlowGraph(nodes, [], edge('unknown', 'success', 'a')), false);
  assert.equal(canConnectFlowGraph(nodes, [], edge('end', 'success', 'a')), false);
});

test('un graphe complet avec reconvergence est valide', () => {
  const reachableNodes = nodes.filter((node) => node.id !== 'fallback' && node.id !== 'end');
  assert.deepEqual(
    validateFlowGraph(reachableNodes, [...branches, edge('b', 'success', 'join')]),
    [],
  );
});
test('la validation détecte une entrée absente ou multiple et un bloc inaccessible', () => {
  assert.match(validateFlowGraph([node('a')], [])[0], /exactement une entrée/);
  assert.match(
    validateFlowGraph([node('x', 'input'), node('y', 'input')], [])[0],
    /exactement une entrée/,
  );
  assert.ok(
    validateFlowGraph([node('input', 'input'), node('a')], []).some((issue) =>
      issue.includes('non relié'),
    ),
  );
});
test('un script sans code bloque la validation du brouillon', () => {
  const script = node('script', 'script');
  script.component.configurationSchema = {
    fields: [{ id: 'code', label: 'Code', required: true }],
  };
  script.configuration.code = '   ';
  assert.ok(
    validateFlowGraph([node('input', 'input'), script], [edge('input', 'success', 'script')]).some(
      (issue) => issue.includes('Code est obligatoire'),
    ),
  );
});
