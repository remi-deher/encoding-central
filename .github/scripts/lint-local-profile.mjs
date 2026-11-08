import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const read = path => readFileSync(new URL(`../../${path}`, import.meta.url), 'utf8');

for (const path of ['AGENTS.md', 'TEST_RULES.md', 'backend/tests/README.md', 'docs/reprise.md']) {
  const content = read(path);
  assert.match(content, /(?:Aucun|aucun|Tous les) tests? .NET/, path);
  assert.doesNotMatch(content, /(?:dotnet test\s+|dotnet run --project backend\/tests\/)/, path);
}

assert.match(read('AGENTS.md'), /seuls le lint et le format ciblés/i);
assert.match(read('TEST_RULES.md'), /Seules vérifications autorisées en local.*lint et format ciblés/s);

const runtime = read('.github/workflows/backend.yml').split('  backend-runtime-tests:')[1]?.split('  backend-multiprocess-tests:')[0];
assert.ok(runtime, 'backend runtime test job exists');
assert.match(runtime, /--filter "Category!=Stress"/);
assert.doesNotMatch(runtime, /Category!=ContentionLoad/);
assert.match(read('.github/workflows/stress.yml'), /--filter "Category=Stress"/);

console.log('Lint OK : politique de vérification locale et périmètres de tests CI cohérents.');
