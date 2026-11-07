import { test } from 'node:test';
import assert from 'node:assert/strict';
import { eligible, deletionArgs } from './cleanup-released-branches.mjs';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const repo = 'owner/repo';
const branch = { name: 'feature/example', commit: { sha: 'original' }, protected: false };
const pr = {
  merged_at: '2026-10-21', base: { ref: 'dev' },
  head: { ref: branch.name, sha: 'original', repo: { full_name: repo } },
  merge_commit_sha: 'rewritten',
};
const check = (b = branch, p = pr, open = new Set(), included = () => true) => eligible(b, p, repo, open, included);

test('accepts squash/rebase via the published merge result, not original ancestry (test only)', () => {
  // Depuis la correction : nettoyage base test uniquement
  const prTest = { ...pr, base: { ref: 'test' } };
  assert.equal(check(branch, prTest, new Set(), sha => sha === 'rewritten'), true);
  // PR vers dev ne sont plus acceptées
  assert.equal(check(branch, pr, new Set(), sha => sha === 'rewritten'), false);
});
test('retains main, dev, test, protected branches and open PRs', () => {
  for (const name of ['main', 'dev', 'test']) assert.equal(check({ ...branch, name }), false);
  assert.equal(check({ ...branch, protected: true }), false);
  assert.equal(check(branch, pr, new Set([branch.name])), false);
});
test('retains new commits, unmerged work, foreign branches and unpublished results', () => {
  assert.equal(check({ ...branch, commit: { sha: 'new-work' } }), false);
  assert.equal(check(branch, { ...pr, merged_at: null }), false);
  assert.equal(check(branch, { ...pr, base: { ref: 'main' } }), false);
  assert.equal(check(branch, { ...pr, head: { ...pr.head, repo: null } }), false);
  assert.equal(check(branch, { ...pr, merge_commit_sha: null }), false);
  assert.equal(check(branch, pr, new Set(), () => false), false);
});

test('accepts branches merged into test', () => {
  const prTest = { ...pr, base: { ref: 'test' } };
  assert.equal(check(branch, prTest), true);
});
test('rejects branches merged into dev (test only)', () => {
  const prDev = { ...pr, base: { ref: 'dev' } };
  assert.equal(check(branch, prDev), false);
});
test('deletion uses an exact expected SHA lease and a fully qualified ref', () => {
  assert.deepEqual(deletionArgs(branch.name, 'original'), [
    'push', '--force-with-lease=refs/heads/feature/example:original',
    'origin', ':refs/heads/feature/example',
  ]);
});

test('Git refuses a stale deletion lease and accepts an unchanged branch', () => {
  const dir = mkdtempSync(join(tmpdir(), 'encoding-cleanup-'));
  const remote = join(dir, 'remote.git');
  const work = join(dir, 'work');
  const git = (...args) => execFileSync('git', args, { cwd: dir, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  const local = (...args) => git('-C', work, ...args);
  try {
    git('init', '--bare', remote);
    git('init', work);
    local('config', 'user.name', 'Test');
    local('config', 'user.email', 'test@example.invalid');
    local('commit', '--allow-empty', '-m', 'initial');
    local('remote', 'add', 'origin', remote);
    local('push', 'origin', 'HEAD:refs/heads/feature/example');
    const oldSha = local('rev-parse', 'HEAD');
    local('commit', '--allow-empty', '-m', 'new work');
    local('push', 'origin', 'HEAD:refs/heads/feature/example');
    const newSha = local('rev-parse', 'HEAD');
    assert.throws(() => local(...deletionArgs('feature/example', oldSha)));
    assert.match(local('ls-remote', 'origin', 'refs/heads/feature/example'), new RegExp(newSha));
    local(...deletionArgs('feature/example', newSha));
    assert.equal(local('ls-remote', 'origin', 'refs/heads/feature/example'), '');
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// Fixtures pour exercer toutes les gardes (base=test)
test('fixtures : PR vers test avec merge_commit_sha valide', () => {
  const prTest = {
    merged_at: '2026-10-21',
    base: { ref: 'test' },
    head: { ref: branch.name, sha: 'original', repo: { full_name: repo } },
    merge_commit_sha: 'rewritten',
  };
  const included = () => true;
  assert.equal(check(branch, prTest, new Set(), included), true);
});

test('fixtures : PR vers test sans merge_commit_sha', () => {
  const prTestNoMerge = {
    merged_at: '2026-10-21',
    base: { ref: 'test' },
    head: { ref: branch.name, sha: 'original', repo: { full_name: repo } },
    merge_commit_sha: null,
  };
  assert.equal(check(branch, prTestNoMerge), false);
});

test('fixtures : PR vers test depuis fork (repo différent)', () => {
  const prFork = {
    merged_at: '2026-10-21',
    base: { ref: 'test' },
    head: { ref: branch.name, sha: 'original', repo: { full_name: 'fork/repo' } },
    merge_commit_sha: 'rewritten',
  };
  assert.equal(check(branch, prFork), false);
});

test('fixtures : PR vers test avec SHA de head différent du commit de la branche', () => {
  const prDifferentSha = {
    merged_at: '2026-10-21',
    base: { ref: 'test' },
    head: { ref: branch.name, sha: 'different-sha', repo: { full_name: repo } },
    merge_commit_sha: 'rewritten',
  };
  const branchDifferent = { name: 'feature/example', commit: { sha: 'original' }, protected: false };
  assert.equal(check(branchDifferent, prDifferentSha), false);
});
