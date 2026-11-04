import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const read = path => readFileSync(new URL(`../../${path}`, import.meta.url), 'utf8');

test('documented local runtime commands exclude stress and contention load', () => {
  for (const path of ['TEST_RULES.md', 'backend/tests/README.md', 'docs/reprise.md']) {
    const lines = read(path).split('\n').filter(line =>
      line.includes('dotnet test ') && !line.includes('docker run'));
    assert.ok(lines.length, `No local test commands found in ${path}`);
    for (const line of lines) {
      for (const command of line.split('dotnet test ').slice(1)) {
        const args = command.split('`')[0];
        assert.match(args, /Category!=Stress/, path);
        assert.match(args, /Category!=ContentionLoad/, path);
      }
    }
  }
});

test('the mixed load remains selected in promotion CI and stress stays separate', () => {
  const source = read('backend/tests/Encoding.Runtime.Tests/LoadContentionTests.cs');
  assert.match(source, /\[Trait\("Category", "ContentionLoad"\)\]\s*public Task HeartbeatsOverviewAndRecoveryShareALargeQueueWithoutLocksOrFailures/);
  const workflow = read('.github/workflows/backend.yml');
  const runtime = workflow.split('  backend-runtime-tests:')[1]?.split('  backend-multiprocess-tests:')[0];
  assert.ok(runtime);
  assert.match(runtime, /--filter "Category!=Stress"/);
  assert.doesNotMatch(runtime, /Category!=ContentionLoad/);
  assert.match(read('.github/workflows/stress.yml'), /--filter "Category=Stress"/);
});
