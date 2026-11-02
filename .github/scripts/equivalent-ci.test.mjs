import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { checkCommits, scopes } from './ci-scopes.mjs';
import { equivalentCheckCommits } from './wait-ci.mjs';

test('historical CI candidates are ancestors with identical scoped trees, including across merges', () => {
  const cwd = mkdtempSync(join(tmpdir(), 'encoding-ci-history-'));
  const git = args => execFileSync('git', args, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  const commit = (path, value) => {
    writeFileSync(join(cwd, path), value);
    git(['add', '.']); git(['commit', '-m', value]);
    return git(['rev-parse', 'HEAD']);
  };
  const resolve = target => {
    const sources = checkCommits(target, (sha, paths) => git(['log', '-1', '--format=%H', sha, '--', ...paths]));
    return equivalentCheckCommits(target, sources, git);
  };
  try {
    git(['init', '-b', 'dev']);
    git(['config', 'user.name', 'CI test']); git(['config', 'user.email', 'ci@example.test']);
    for (const path of ['backend', 'frontend', '.github/scripts']) mkdirSync(join(cwd, path), { recursive: true });
    for (const path of ['backend/code', 'frontend/code', '.github/scripts/policy.mjs']) writeFileSync(join(cwd, path), 'initial');
    git(['add', '.']); git(['commit', '-m', 'initial']);
    const initial = git(['rev-parse', 'HEAD']);
    const changed = commit('backend/code', 'backend change');
    const validated = commit('frontend/code', 'frontend change');
    git(['checkout', '-b', 'sibling', initial]);
    const sibling = commit('backend/code', 'different backend');
    git(['checkout', 'dev']);
    const current = commit('frontend/code', 'new frontend');
    let candidates = resolve(current);
    assert.deepEqual(candidates['backend-compile'], [current, validated, changed]);
    assert.deepEqual(candidates['frontend-e2e'], [current]);
    assert.ok(!candidates['backend-compile'].includes(sibling));
    assert.ok(!candidates['backend-compile'].includes(initial));
    // A merged branch can have different domain content even though it descends
    // from the last change. It must not contribute CI for the resulting tree.
    git(['checkout', '-b', 'branch', changed]);
    const divergent = commit('backend/code', 'temporary backend change');
    commit('backend/code', 'backend change');
    git(['checkout', 'dev']);
    git(['merge', '--no-ff', 'branch', '-m', 'merge restored tree']);
    const merged = git(['rev-parse', 'HEAD']);
    candidates = equivalentCheckCommits(merged,
      Object.fromEntries(scopes.flatMap(scope => scope.checks.map(name => [name, changed]))), git);
    assert.ok(candidates['backend-compile'].includes(validated));
    assert.ok(!candidates['backend-compile'].includes(divergent));
    assert.equal(candidates['backend-compile'][0], merged);
  } finally { rmSync(cwd, { recursive: true, force: true }); }
});
