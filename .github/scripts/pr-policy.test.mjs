import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, writeFileSync, rmSync, chmodSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const workflow = readFileSync(new URL('../workflows/pr-policy.yml', import.meta.url), 'utf8');
const script = workflow.slice(workflow.lastIndexOf('        run: |\n') + '        run: |\n'.length)
  .split('\n').map(line => line.replace(/^          /, '')).join('\n');
const sha = 'a'.repeat(40);
const snapshot = (state, conclusion = 'SUCCESS', headRefOid = sha) => ({
  state, headRefOid, statusCheckRollup: [{ name: 'promotion-ci', status: 'COMPLETED', conclusion, startedAt: '2026-10-26T00:00:00Z' }],
});
function execute(snapshots) {
  const directory = mkdtempSync(join(tmpdir(), 'encoding-pr-policy-'));
  const calls = join(directory, 'calls');
  const stateFile = join(directory, 'snapshots.json');
  writeFileSync(stateFile, JSON.stringify(snapshots));
  writeFileSync(join(directory, 'gh'), `#!/usr/bin/env node
const fs = require('node:fs');
const args = process.argv.slice(2);
fs.appendFileSync(process.env.POLICY_TEST_CALLS, JSON.stringify(args) + '\\n');
if (args[1] === 'view') {
  const values = JSON.parse(fs.readFileSync(process.env.POLICY_TEST_STATE, 'utf8'));
  const value = values.length > 1 ? values.shift() : values[0];
  fs.writeFileSync(process.env.POLICY_TEST_STATE, JSON.stringify(values));
  console.log(JSON.stringify(value));
}
`);
  chmodSync(join(directory, 'gh'), 0o755);
  let status = 0;
  try {
    execFileSync('bash', ['-e', '-c', script], { timeout: 3000, stdio: 'pipe', env: {
      ...process.env, PATH: `${directory}:${process.env.PATH}`, PR_NUMBER: '423',
      GITHUB_REPOSITORY: 'example/repo', EXPECTED_SHA: sha,
      POLICY_TEST_CALLS: calls, POLICY_TEST_STATE: stateFile,
    } });
  } catch (error) { status = error.status; }
  try { return { status, calls: readFileSync(calls, 'utf8').trim().split('\n').map(line => JSON.parse(line)) }; }
  finally { rmSync(directory, { recursive: true, force: true }); }
}

test('policy serializes events for the same PR and cancels superseded runs', () => {
  assert.match(workflow, /concurrency:\n  group: pr-policy-\$\{\{ github.event.pull_request.number \}\}\n  cancel-in-progress: true/);
});
test('a completed promotion of the expected commit is an idempotent success', () => {
  const result = execute([snapshot('MERGED')]);
  assert.equal(result.status, 0);
  assert.ok(result.calls.every(args => args[1] === 'view'));
});
test('a concurrent merge while awaiting CI succeeds without issuing another merge', () => {
  const result = execute([snapshot('OPEN'), snapshot('MERGED')]);
  assert.equal(result.status, 0);
  assert.equal(result.calls.filter(args => args.includes('--disable-auto')).length, 1);
  assert.ok(!result.calls.some(args => args.includes('--auto')));
});
test('an open promotion enables auto merge for the exact expected commit', () => {
  const result = execute([snapshot('OPEN')]);
  assert.equal(result.status, 0);
  assert.ok(result.calls.some(args => args.includes('--auto') && args.includes('--match-head-commit') && args.includes(sha)));
});
test('a changed commit, unsuccessful gate or closed unmerged PR never authorizes a merge', () => {
  for (const value of [snapshot('MERGED', 'SUCCESS', 'b'.repeat(40)), snapshot('MERGED', 'FAILURE'), snapshot('CLOSED'), snapshot('OPEN', 'FAILURE')]) {
    const result = execute([value]);
    assert.equal(result.status, 1);
    assert.ok(!result.calls.some(args => args.includes('--auto')));
  }
});
