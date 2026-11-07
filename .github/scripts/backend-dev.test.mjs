import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { execFileSync, spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { readWorkflow, job, step, value, expression } from './workflow-test-helpers.mjs';

const workflow = readWorkflow('backend-dev');
const ancestry = job(workflow, 'dev-ancestry');
const changes = job(workflow, 'changes');
const conclusion = step(job(workflow, 'dev-ci'), 'Conclude');
const repository = 'example/encoding-central';

function bash(script, cwd, env = {}) {
  const result = spawnSync('bash', ['--noprofile', '--norc', '-e', '-o', 'pipefail', '-c', script], {
    cwd, env: { ...process.env, ...env }, encoding: 'utf8', timeout: 5000,
  });
  assert.ifError(result.error);
  assert.equal(result.signal, null);
  return result;
}

function fixture(callback) {
  const directory = mkdtempSync(join(tmpdir(), 'encoding-backend-dev-'));
  const cwd = join(directory, 'checkout');
  mkdirSync(cwd);
  const git = args => execFileSync('git', args, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], timeout: 5000 }).trim();
  const commit = (path, content) => {
    writeFileSync(join(cwd, path), content);
    git(['add', path]);
    git(['commit', '-m', content]);
    return git(['rev-parse', 'HEAD']);
  };
  try {
    git(['init', '-b', 'dev']);
    git(['config', 'user.name', 'CI test']);
    git(['config', 'user.email', 'ci@example.test']);
    git(['config', 'commit.gpgsign', 'false']);
    git(['config', 'core.hooksPath', '/dev/null']);
    const initial = commit('initial', 'initial');
    git(['init', '--bare', join(directory, 'origin.git')]);
    git(['remote', 'add', 'origin', join(directory, 'origin.git')]);
    callback({ cwd, git, commit, initial });
  } finally { rmSync(directory, { recursive: true, force: true }); }
}

function context(event, sha, headSha = '') {
  return { github: {
    event_name: event, sha, repository,
    event: { pull_request: { head: { ref: headSha ? 'test' : '', sha: headSha, repo: { full_name: headSha ? repository : '' } } } },
  } };
}

for (const compliant of [true, false]) {
  test(`ancestry uses the PR source, not its synthetic merge: ${compliant ? 'compliant succeeds' : 'divergent fails'}`, () => {
    fixture(({ cwd, git, commit }) => {
      git(['checkout', '-b', 'test']);
      commit('source', 'test change');
      git(['checkout', 'dev']);
      const missing = commit('dev-only', 'dev change');
      git(['checkout', 'test']);
      if (compliant) git(['merge', '--no-ff', 'dev', '-m', 'bring dev into test']);
      const source = git(['rev-parse', 'HEAD']);
      git(['push', 'origin', 'dev', 'test']);
      git(['checkout', '--detach', 'dev']);
      git(['merge', '--no-ff', 'test', '-m', 'synthetic PR merge']);
      const synthetic = git(['rev-parse', 'HEAD']);
      assert.notEqual(synthetic, source);
      const env = context('pull_request', synthetic, source);
      assert.equal(expression(value(ancestry, 'if'), env), true);
      assert.equal(value(ancestry, 'fetch-depth'), '0');
      const checkout = expression(value(ancestry, 'ref'), env);
      assert.equal(checkout, source);
      git(['checkout', '--detach', checkout]);
      const fetch = bash(value(step(ancestry, 'Récupérer dev'), 'run'), cwd);
      assert.equal(fetch.status, 0, fetch.stderr);
      const result = bash(value(step(ancestry, 'Vérifier ascendance'), 'run'), cwd);
      assert.equal(result.status, compliant ? 0 : 1, result.stdout + result.stderr);
      if (compliant) assert.match(result.stdout, /Ascendance vérifiée/);
      else {
        assert.match(result.stdout, /::error::dev porte des commits absents de test/);
        assert.ok(result.stdout.includes(missing));
      }
    });
  });
}

test('ancestry is skipped for a push on dev, another source branch, or a fork', () => {
  const push = context('push', 'a'.repeat(40));
  const feature = context('pull_request', 'b'.repeat(40), 'a'.repeat(40));
  feature.github.event.pull_request.head.ref = 'fix/example';
  const fork = context('pull_request', 'b'.repeat(40), 'a'.repeat(40));
  fork.github.event.pull_request.head.repo.full_name = 'fork/encoding-central';
  for (const env of [push, feature, fork]) assert.equal(expression(value(ancestry, 'if'), env), false);
});

for (const path of ['backend/example.cs', 'documentation.md']) {
  test(`changes handles a push on dev without PR fields: ${path}`, () => {
    fixture(({ cwd, git, commit, initial }) => {
      mkdirSync(join(cwd, 'backend'));
      const sha = commit(path, 'push change');
      const env = context('push', sha);
      // Missing PR properties evaluate to empty strings in GitHub expressions.
      env.github.event.pull_request.head.sha = '';
      env.github.event.pull_request.base = { sha: '' };
      env.github.event.before = initial;
      const checkout = expression(value(changes, 'ref'), env);
      assert.equal(checkout, sha);
      git(['checkout', '--detach', checkout]);
      const output = join(cwd, 'output');
      const result = bash(value(changes, 'run'), cwd, {
        BASE: expression(value(changes, 'BASE'), env), GITHUB_OUTPUT: output,
      });
      assert.equal(result.status, 0, result.stdout + result.stderr);
      assert.equal(readFileSync(output, 'utf8').trim(), `backend=${path.startsWith('backend/')}`);
    });
  });
}

test('dev-ci requires ancestry and propagates errors while allowing skipped jobs', () => {
  const ci = job(workflow, 'dev-ci');
  assert.equal(expression(value(ci, 'if'), { always: () => true }), true);
  assert.deepEqual(value(ci, 'needs').slice(1, -1).split(',').map(name => name.trim()), ['changes', 'backend-dev-fast', 'dev-ancestry']);
  for (const [diff, fast, ancestor, expected] of [
    ['success', 'success', 'success', 0],
    ['success', 'skipped', 'skipped', 0],
    ['success', 'success', 'failure', 1],
    ['success', 'success', 'cancelled', 1],
    ['failure', 'skipped', 'skipped', 1],
    ['success', 'failure', 'success', 1],
  ]) {
    const context = { needs: {
      changes: { result: diff }, 'backend-dev-fast': { result: fast }, 'dev-ancestry': { result: ancestor },
    } };
    const env = Object.fromEntries(['CHANGES', 'FAST', 'ANCESTRY'].map(key => [key, expression(value(conclusion, key), context)]));
    const result = bash(value(conclusion, 'run'), undefined, env);
    assert.equal(result.status, expected, result.stdout + result.stderr);
  }
});
