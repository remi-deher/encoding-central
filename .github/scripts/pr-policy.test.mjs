import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { readWorkflow, job, step, value } from './workflow-test-helpers.mjs';

const script = value(step(job(readWorkflow('pr-policy'), 'policy'), 'Validate the source and target branches'), 'run');
const repository = 'example/encoding-central';

function execute(base, head, source = repository, closeStatus = 0) {
  const directory = mkdtempSync(join(tmpdir(), 'encoding-pr-policy-'));
  const calls = join(directory, 'calls');
  try {
    writeFileSync(calls, '');
    writeFileSync(join(directory, 'gh'), `#!/usr/bin/env node
const fs = require('node:fs');
const args = process.argv.slice(2);
fs.appendFileSync(process.env.POLICY_TEST_CALLS, JSON.stringify(args) + '\\n');
if (args[0] !== 'pr' || args[1] !== 'close') process.exit(99);
process.exit(Number(process.env.POLICY_TEST_CLOSE_STATUS));
`, { mode: 0o755 });
    const result = spawnSync('bash', ['--noprofile', '--norc', '-e', '-o', 'pipefail', '-c', script], {
      encoding: 'utf8', timeout: 5000,
      env: {
        ...process.env, PATH: `${directory}:${process.env.PATH}`, GH_TOKEN: 'test-only',
        PR_NUMBER: '423', GITHUB_REPOSITORY: repository,
        BASE_REF: base, HEAD_REF: head, HEAD_REPO: source,
        POLICY_TEST_CALLS: calls, POLICY_TEST_CLOSE_STATUS: String(closeStatus),
      },
    });
    assert.ifError(result.error);
    assert.equal(result.signal, null);
    return { ...result, calls: readFileSync(calls, 'utf8').trim().split('\n').filter(Boolean).map(line => JSON.parse(line)) };
  } finally { rmSync(directory, { recursive: true, force: true }); }
}

const cases = [
  ['feature/xyz', 'test', true],
  ['dev', 'test', true],
  ['main', 'test', false],
  ['test', 'dev', true],
  ['feature/xyz', 'dev', false],
  ['dev', 'main', true],
  ['test', 'main', false],
  ['feature/xyz', 'unknown', false],
  ['main', 'dev', false],
  ['feature/xyz', 'main', false],
];
for (const [head, base, allowed] of cases) {
  test(`Validate executes ${head} -> ${base}: ${allowed ? 'allowed' : 'closed'}`, () => {
    const result = execute(base, head);
    assert.equal(result.status, allowed ? 0 : 1, result.stdout + result.stderr);
    if (allowed) {
      assert.deepEqual(result.calls, []);
      assert.match(result.stdout, /Pull request autorisée/);
    } else {
      assert.equal(result.calls.length, 1);
      const [args] = result.calls;
      assert.deepEqual(args.slice(0, 6), ['pr', 'close', '423', '--repo', repository, '--comment']);
      assert.match(args[6], /PR fermée automatiquement.*vers test.*test vers dev.*dev vers main/);
      assert.equal(args.length, 7);
    }
  });
}

for (const [head, base] of [['feature/xyz', 'test'], ['dev', 'test'], ['test', 'dev'], ['dev', 'main']]) {
  test(`Validate closes fork ${head} -> ${base}`, () => {
    const result = execute(base, head, 'fork/encoding-central');
    assert.equal(result.status, 1, result.stdout + result.stderr);
    assert.equal(result.calls.length, 1);
    assert.deepEqual(result.calls[0].slice(0, 5), ['pr', 'close', '423', '--repo', repository]);
  });
}

test('Validate propagates a failure to close a forbidden PR', () => {
  const result = execute('dev', 'feature/xyz', repository, 23);
  assert.equal(result.status, 23);
  assert.equal(result.calls.length, 1);
});
