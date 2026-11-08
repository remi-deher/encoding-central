import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readWorkflow, job, value } from './workflow-test-helpers.mjs';

const workflow = readWorkflow('pr-policy');

test('policy serializes events for the same PR and cancels superseded runs', () => {
  assert.match(workflow, /concurrency:\n  group: pr-policy-\$\{\{ github.event.pull_request.number \}\}\n  cancel-in-progress: true/);
});

test('policy only validates branches and cannot enable automatic merging', () => {
  const policy = job(workflow, 'policy');
  assert.deepEqual([...policy.matchAll(/^      - name: (.+)$/gm)].map(match => match[1]), ['Validate the source and target branches']);
  assert.doesNotMatch(workflow, /gh pr merge|--auto|--disable-auto|PAT_TOKEN/);
  assert.equal(value(workflow, 'contents'), 'read');
  assert.equal(value(workflow, 'pull-requests'), 'write');
});
