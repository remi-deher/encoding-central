import assert from 'node:assert/strict';
import test from 'node:test';
import { loadModel } from './model-loader.mjs';

const { mediaBadges, joinBadges, splitBadges } = await loadModel('media-badges');

test('résume codec vidéo, codec audio, canaux et résolution', () => {
  const badges = mediaBadges({
    streams: [
      { codec_type: 'video', codec_name: 'hevc', width: 1920, height: 1080 },
      { codec_type: 'audio', codec_name: 'eac3', channels: 6 },
      { codec_type: 'audio', codec_name: 'aac', channels: 2 },
    ],
  });
  assert.deepEqual(badges, ['HEVC', 'EAC3', '5.1', '1080p']);
});

test('arrondit les hauteurs non standard à la résolution inférieure', () => {
  assert.deepEqual(mediaBadges({ streams: [{ codec_type: 'video', height: 800 }] }), ['720p']);
  assert.deepEqual(mediaBadges({ streams: [{ codec_type: 'video', height: 360 }] }), ['360p']);
});

test('ignore les métadonnées absentes ou incomplètes', () => {
  assert.deepEqual(mediaBadges(null), []);
  assert.deepEqual(mediaBadges({ streams: [] }), []);
  assert.deepEqual(mediaBadges({ streams: [{ codec_type: 'audio', channels: 'x' }] }), []);
});

test('sérialise et relit les pastilles sans valeur vide', () => {
  assert.equal(joinBadges(['HEVC', 'AAC']), 'HEVC|AAC');
  assert.deepEqual(splitBadges('HEVC|AAC'), ['HEVC', 'AAC']);
  assert.deepEqual(splitBadges(''), []);
  assert.deepEqual(splitBadges(undefined), []);
});
