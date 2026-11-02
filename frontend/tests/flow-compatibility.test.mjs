import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import ts from 'typescript';

async function loadModel(name) {
  const source = await readFile(
    new URL(`../src/app/shared/models/${name}.ts`, import.meta.url),
    'utf8',
  );
  const { outputText } = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.ES2022, target: ts.ScriptTarget.ES2022 },
  });
  return import(`data:text/javascript;base64,${Buffer.from(outputText).toString('base64')}`);
}
const { isFlowComponentCompatible, getFlowConfigurationSchema } = await loadModel('flow-component');
const {
  GENERIC_FILE_INPUT,
  MEDIA_INPUT_COMPONENTS,
  getFlowInputComponents,
  FLOW_CONTROL_COMPONENTS,
} = await loadModel('flow-control-components');
const { MEDIA_TOOL_ACTIONS } = await loadModel('media-tool-actions');
const action = (id) => MEDIA_TOOL_ACTIONS.find((component) => component.id === id);

test('les anciennes contraintes Nœuds restent éditables sans rendre le nouveau relais compatible avec un ancien plugin', async () => {
  const { resolveFlowComponent } = await loadModel('flow-validation');
  const plugin = JSON.parse(
    await readFile(
      new URL('../../backend/tests/Fixtures/StoreCatalog/nodes.json', import.meta.url),
      'utf8',
    ),
  );
  const reference = {
    pluginId: 'nodes',
    pluginVersion: '1.0.0',
    componentId: 'nodes-run-on-node',
    componentVersion: '1.0.0',
    configurationSchemaVersion: 1,
  };
  assert.equal(resolveFlowComponent([plugin], reference)?.component.id, 'nodes-run-on-node');
  assert.equal(
    resolveFlowComponent([plugin], { ...reference, componentId: 'nodes-handoff' }),
    undefined,
  );
  assert.equal(
    resolveFlowComponent([plugin], { ...reference, configurationSchemaVersion: 2 }),
    undefined,
  );
  assert.equal(
    resolveFlowComponent([plugin], {
      ...reference,
      pluginVersion: '1.1.0',
      componentId: 'nodes-handoff',
    })?.component.id,
    'nodes-handoff',
  );
});

test('une entrée générique est disponible pour chaque type avec une seule entrée spécialisée compatible', () => {
  for (const mediaType of ['video', 'music', 'image', 'subtitles']) {
    const inputs = getFlowInputComponents(mediaType);
    assert.deepEqual(
      inputs.map((input) => input.id),
      [GENERIC_FILE_INPUT.id, MEDIA_INPUT_COMPONENTS[mediaType].id],
    );
    assert.ok(inputs.every((input) => isFlowComponentCompatible(input, mediaType)));
  }
  assert.equal(isFlowComponentCompatible(MEDIA_INPUT_COMPONENTS.video, 'music'), false);
  assert.equal(isFlowComponentCompatible(MEDIA_INPUT_COMPONENTS.music, 'video'), false);
});
test('un flow vidéo peut traiter ses pistes audio et sous-titres sans proposer des transformations d’image', () => {
  assert.equal(isFlowComponentCompatible(action('base-transcode-audio'), 'video'), true);
  assert.equal(isFlowComponentCompatible(action('base-extract-subtitles'), 'video'), true);
  assert.equal(isFlowComponentCompatible(action('base-convert-image'), 'video'), false);
});
test('un flow musique propose les actions audio et exclut les opérations vidéo et Matroska', () => {
  assert.equal(isFlowComponentCompatible(action('base-normalize-audio'), 'music'), true);
  assert.equal(isFlowComponentCompatible(action('base-transcode-video'), 'music'), false);
  assert.equal(isFlowComponentCompatible(action('base-extract-track'), 'music'), false);
});
test('les flows image et sous-titres excluent les composants génériques liés à un outil incompatible', () => {
  const prepare = FLOW_CONTROL_COMPONENTS.find(
    (component) => component.id === 'base-prepare-processing',
  );
  assert.equal(isFlowComponentCompatible(prepare, 'image'), false);
  assert.equal(isFlowComponentCompatible(action('base-inspect-hdr'), 'image'), false);
  assert.equal(isFlowComponentCompatible(action('base-extract-subtitles'), 'subtitles'), false);
  assert.equal(isFlowComponentCompatible(action('base-probe-media'), 'subtitles'), true);
  const script = FLOW_CONTROL_COMPONENTS.find((component) => component.id === 'base-script');
  assert.equal(isFlowComponentCompatible(script, 'image'), true);
  assert.equal(isFlowComponentCompatible(script, 'subtitles'), true);
});
test('les choix d’outil du préparateur sont adaptés au média du flow', () => {
  const prepare = FLOW_CONTROL_COMPONENTS.find(
    (component) => component.id === 'base-prepare-processing',
  );
  const options = (mediaType) =>
    getFlowConfigurationSchema(prepare.configurationSchema, mediaType)
      .fields.find((field) => field.id === 'tool')
      .options.map((option) => option.value);
  assert.deepEqual(options('video'), ['ffmpeg', 'mkvtoolnix']);
  assert.deepEqual(options('music'), ['ffmpeg']);
});
