import assert from 'node:assert/strict';
import test from 'node:test';
import { loadModel } from './model-loader.mjs';
const { flowOutputPresentation, flowComponentColors } = await loadModel('flow-presentation');
const output = (id, label = id) => ({ id, label, description: '' });

test('false conditions and technical errors have distinct icons and tones', () => {
  assert.deepEqual(flowOutputPresentation(output('yes')), { icon: 'check', tone: 'success' });
  assert.deepEqual(flowOutputPresentation(output('false')), { icon: 'close', tone: 'warning' });
  assert.deepEqual(flowOutputPresentation(output('error')), { icon: 'warning', tone: 'danger' });
  assert.deepEqual(flowOutputPresentation(output('plugin-id', 'Non conforme')), {
    icon: 'close',
    tone: 'warning',
  });
  assert.deepEqual(flowOutputPresentation(output('error', 'Non')), {
    icon: 'warning',
    tone: 'danger',
  });
});

test('known continuation, skipped, waiting and media labels have small explicit icons', () => {
  for (const [label, icon] of [
    ['Suite', 'forward'],
    ['Ignoré', 'skip'],
    ['En attente', 'clock'],
    ['Vidéo', 'video'],
    ['Audio', 'music'],
    ['Sous-titres', 'subtitles'],
  ]) {
    assert.equal(flowOutputPresentation(output('plugin-output', label)).icon, icon);
  }
});

test('unknown and misleading labels retain a neutral numbered port', () => {
  for (const label of [
    'Sans erreur',
    'Non disponible sur ce nœud',
    'Audio déjà conforme',
    'constructor',
    '__proto__',
  ]) {
    assert.deepEqual(flowOutputPresentation(output('custom-output', label)), {
      icon: null,
      tone: 'neutral',
    });
  }
});

test('shared media colors use theme tokens and conditions use their own domain', () => {
  for (const mediaType of ['video', 'audio', 'image', 'subtitles', 'general']) {
    const colors = flowComponentColors({ kind: 'action', mediaType, pluginId: 'custom' });
    assert.match(colors.color, /^var\(--/);
    assert.match(colors.background, /^var\(--/);
    assert.equal(flowComponentColors({ kind: 'condition', mediaType }).icon, 'condition');
  }
});
