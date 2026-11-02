import { test } from 'node:test';
import assert from 'node:assert/strict';
import { evaluateChecks } from './wait-ci.mjs';

const run = (id, sha, conclusion, status = 'completed') => ({ id, sha, name: 'frontend-e2e', conclusion, status });
const evaluate = runs => evaluateChecks({ 'frontend-e2e': 'old' }, 'current', runs)[0].state;
test('historical failure blocks promotion even if frontend was not rerun', () => {
  assert.equal(evaluate([run(1, 'old', 'failure')]), 'failed');
});
test('current commit takes precedence and unrelated results are ignored', () => {
  assert.equal(evaluate([run(1, 'old', 'success'), run(2, 'current', 'failure')]), 'failed');
  assert.equal(evaluate([run(1, 'old', 'failure'), run(2, 'current', 'success')]), 'success');
  assert.equal(evaluate([run(1, 'unrelated', 'success')]), 'missing');
});
test('latest retry supersedes failed attempt but pending is never green', () => {
  assert.equal(evaluate([run(1, 'old', 'failure'), run(2, 'old', 'success')]), 'success');
  assert.equal(evaluate([run(1, 'old', 'success'), run(2, 'old', null, 'in_progress')]), 'pending');
});
test('cancelled, skipped, neutral, timed out and missing checks do not authorize publication', () => {
  for (const conclusion of ['cancelled', 'skipped', 'neutral', 'timed_out', 'failure']) {
    assert.equal(evaluate([run(1, 'old', conclusion)]), 'failed');
  }
  assert.equal(evaluate([]), 'missing');
});

test('a cancelled equivalent check waits while the current commit has CI checks running', () => {
  const results = evaluateChecks(
    { 'backend-runtime-tests': 'old', 'frontend-fast': 'old' },
    'current',
    [
      { id: 1, sha: 'old', name: 'backend-runtime-tests', conclusion: 'cancelled', status: 'completed' },
      { id: 2, sha: 'current', name: 'frontend-fast', conclusion: null, status: 'in_progress' },
    ],
    {
      'backend-runtime-tests': ['current', 'old'],
      'frontend-fast': ['current', 'old'],
    },
  );
  assert.equal(results.find(result => result.name === 'backend-runtime-tests').state, 'pending');
});

test('an explicitly equivalent ancestor can carry CI beyond the last file-changing commit', () => {
  const sources = { 'frontend-e2e': 'last-change' };
  const equivalents = { 'frontend-e2e': ['current', 'validated', 'last-change'] };
  assert.equal(evaluateChecks(sources, 'current', [run(2, 'validated', 'success')], equivalents)[0].state, 'success');
  assert.equal(evaluateChecks(sources, 'current', [run(2, 'unrelated', 'success')], equivalents)[0].state, 'missing');
  assert.equal(evaluateChecks(sources, 'current', [run(2, 'validated', 'success'), run(3, 'current', null, 'in_progress')], equivalents)[0].state, 'pending');
  assert.equal(evaluateChecks(sources, 'current', [run(2, 'validated', 'success'), run(3, 'current', 'failure')], equivalents)[0].state, 'failed');
  assert.equal(evaluateChecks(sources, 'current', [run(1, 'last-change', 'success'), run(2, 'validated', 'failure')], equivalents)[0].state, 'failed');
});
