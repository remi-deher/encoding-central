import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const read = path => readFileSync(new URL(`../../${path}`, import.meta.url), 'utf8');

test('local documentation reserves .NET tests for CI', () => {
  for (const path of ['AGENTS.md', 'TEST_RULES.md', 'backend/tests/README.md', 'docs/reprise.md']) {
    const content = read(path);
    assert.match(content, /(?:Aucun|aucun|Tous les) tests? .NET/ , path);
    assert.doesNotMatch(content, /(?:dotnet test\s+|dotnet run --project backend\/tests\/)/, path);
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
