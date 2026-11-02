import assert from 'node:assert/strict';
import test from 'node:test';
import { loadModel } from './model-loader.mjs';
const { simulationFile, simulationProfile, simulationObject } = await loadModel('flow-simulation');
const values = {
  name: 'folder/film.mkv',
  size: 0,
  duration: 12.5,
  videoCodec: 'hevc',
  width: 1920,
  height: 1080,
  audioCount: 2,
  audioCodecs: 'aac, ac3',
  audioLanguages: 'fra, eng',
  audioChannels: 6,
  subtitleCount: 1,
  subtitleLanguages: 'fra',
  variables: '{"score":42,"ready":true}',
  sourceNodeId: 'source',
  workerNodeId: 'worker',
  libraryId: '',
};
test('simulation profiles preserve typed metadata, ordered tracks and variables', () => {
  const file = simulationFile(values);
  assert.equal(file.metadata.format.size, 0);
  assert.equal(file.metadata.streams[0].codec_name, 'hevc');
  assert.equal(file.metadata.streams[2].tags.language, 'eng');
  assert.equal(file.metadata.streams[1].channels, 6);
  assert.equal(file.variables.score, 42);
  assert.equal(file.variables.ready, true);
  assert.equal(file.libraryId, undefined);
  const restored = simulationProfile(file);
  assert.equal(restored.size, 0);
  assert.equal(restored.audioLanguages, 'fra, eng');
  assert.deepEqual(simulationFile(restored), file);
});
test('missing metadata stays missing and absent tracks stay absent', () => {
  const file = simulationFile({
    ...values,
    videoCodec: '',
    audioCount: 0,
    subtitleCount: 0,
    size: null,
    duration: '',
  });
  assert.deepEqual(file.metadata, { format: {}, streams: [] });
  assert.equal(simulationProfile(file).duration, null);
});
test('invalid variable JSON and unbounded or fractional track counts fail', () => {
  for (const value of ['[]', 'null', '42', 'invalid']) assert.throws(() => simulationObject(value));
  for (const audioCount of [101, -1, 1.5])
    assert.throws(() => simulationFile({ ...values, audioCount }));
});
