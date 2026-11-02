import assert from 'node:assert/strict';
import test from 'node:test';
import { loadModel } from './model-loader.mjs';

const { workerShellAllows } = await loadModel('worker-shell');

test('the worker shell keeps the six agreed pages and the sign-in flow', () => {
  for (const url of [
    '/',
    '/activity',
    '/activity?type=ai',
    '/libraries',
    '/nodes?tab=cluster',
    '/plugins',
    '/settings',
    '/login',
    '/setup',
  ]) {
    assert.equal(workerShellAllows(url), true, url);
  }
});

test('the worker shell leaves authoring pages to the central', () => {
  for (const url of [
    '/flows',
    '/flows/new',
    '/libraries/scripts',
    '/libraries/packs',
    '/ai/assistant',
    '/updates',
  ]) {
    assert.equal(workerShellAllows(url), false, url);
  }
});
