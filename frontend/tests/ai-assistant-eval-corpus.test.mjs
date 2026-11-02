import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const corpusUrl = new URL('../../docs/testing/ai-assistant-eval-cases.json', import.meta.url);
const corpus = JSON.parse(await readFile(corpusUrl, 'utf8'));

test('assistant evaluation corpus has unique, actionable cases', () => {
  assert.equal(corpus.version, 1);
  assert.ok(Array.isArray(corpus.cases) && corpus.cases.length >= 8);
  const ids = corpus.cases.map((item) => item.id);
  assert.equal(new Set(ids).size, ids.length);
  for (const item of corpus.cases) {
    assert.ok(item.id && item.category && item.userPrompt);
    assert.ok(Array.isArray(item.sources));
    assert.ok(Array.isArray(item.expected) && item.expected.length > 0);
    for (const source of item.sources) {
      assert.ok(source.kind && source.label);
      assert.ok(Object.hasOwn(source, 'data'));
    }
  }
});

test('assistant evaluation corpus covers the required safety and quality dimensions', () => {
  const categories = new Set(corpus.cases.map((item) => item.category));
  for (const category of [
    'grounding',
    'prompt-injection',
    'tool-use',
    'tool-validation',
    'concurrency',
    'cancellation',
    'uncertainty',
  ]) {
    assert.ok(categories.has(category), `missing evaluation category: ${category}`);
  }
});
