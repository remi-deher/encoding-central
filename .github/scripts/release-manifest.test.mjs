import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { generateManifest } from './release-manifest.mjs';

test('release manifest separates version notes and preserves special characters', () => {
  const directory = mkdtempSync(join(tmpdir(), 'encoding-manifest-'));
  const original = process.cwd();
  const git = (...args) => execFileSync('git', args, { cwd: directory, stdio: 'pipe' });
  try {
    git('init');
    git('config', 'user.email', 'test@example.com');
    git('config', 'user.name', 'Test');
    writeFileSync(join(directory, 'file'), 'one');
    git('add', '.');
    git('commit', '-m', 'feat: première version');
    git('tag', 'v0.1.0');
    writeFileSync(join(directory, 'file'), 'two');
    git('commit', '-am', 'fix: texte "cité" <script> et `code`');
    process.chdir(directory);
    const manifest = generateManifest('0.1.1', 'owner/repo');
    assert.equal(manifest.version, '0.1.1');
    assert.match(manifest.commit, /^[a-f0-9]{40}$/);
    assert.equal(manifest.releases.length, 2);
    assert.equal(manifest.releases[0].version, '0.1.1');
    assert.equal(manifest.releases[0].notes, 'fix: texte "cité" <script> et `code`');
    assert.equal(manifest.releases[1].notes, 'feat: première version');
    assert.deepEqual(JSON.parse(JSON.stringify(manifest)), manifest);
    assert.throws(() => generateManifest('invalid', 'owner/repo'));
    assert.throws(() => generateManifest('1.0.0', '../invalid'));
  } finally {
    process.chdir(original);
    rmSync(directory, { recursive: true, force: true });
  }
});
