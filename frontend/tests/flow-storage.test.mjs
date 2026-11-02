import assert from 'node:assert/strict';
import test from 'node:test';
import { loadModel } from './model-loader.mjs';
const { flowPublishedVersion } = await loadModel('flow-validation');
test('a new reloaded draft has no published version', () => {
  assert.equal(
    flowPublishedVersion({ definition: { version: 1 }, draftRevision: 1, publishedVersion: null }),
    0,
  );
  assert.equal(
    flowPublishedVersion({ definition: { version: 4 }, draftRevision: 2, publishedVersion: 3 }),
    3,
  );
});
test('a published definition retains its version when draft metadata is absent or null', () => {
  assert.equal(flowPublishedVersion({ definition: { version: 3 } }), 3);
  assert.equal(
    flowPublishedVersion({
      definition: { version: 3 },
      draftRevision: null,
      publishedVersion: null,
    }),
    3,
  );
});

test('portable flow comparison ignores version and JSON property order', async () => {
  const { flowDocumentKey } = await loadModel('flow-validation');
  const original = {
    id: 'flow',
    version: 1,
    name: 'Test',
    nodes: [{ id: 'step', configuration: { quality: 23, codec: 'hevc' } }],
    connections: [],
  };
  const reordered = {
    connections: [],
    nodes: [{ configuration: { codec: 'hevc', quality: 23 }, id: 'step' }],
    name: 'Test',
    version: 2,
    id: 'flow',
  };
  assert.equal(flowDocumentKey(original), flowDocumentKey(reordered));
  assert.notEqual(flowDocumentKey(original), flowDocumentKey({ ...original, name: 'Changed' }));
  assert.notEqual(
    flowDocumentKey(original),
    flowDocumentKey({
      ...original,
      nodes: [{ id: 'step', configuration: { quality: 24, codec: 'hevc' } }],
    }),
  );
});
