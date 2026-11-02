import { execFileSync } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

const git = (...args) => execFileSync('git', args, { encoding: 'utf8' }).trim();
const validVersion = (value) => /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(value);

export function generateManifest(version, repository, sha = 'HEAD') {
  if (!validVersion(version)) throw new Error('Version X.Y.Z requise.');
  if (!/^[A-Za-z0-9][\w.-]*\/[A-Za-z0-9][\w.-]*$/.test(repository)) throw new Error('Dépôt owner/name requis.');
  const tags = git('tag', '--merged', sha, '--sort=version:refname').split('\n')
    .filter(tag => tag.startsWith('v') && validVersion(tag.slice(1)) && tag !== `v${version}`);
  const refs = [...tags, sha];
  const releases = refs.map((ref, index) => {
    const releaseVersion = index === tags.length ? version : ref.slice(1);
    const range = index ? `${refs[index - 1]}..${ref}` : ref;
    const notes = git('log', '--no-merges', '--format=%s', range).split('\n').filter(Boolean);
    return {
      version: releaseVersion,
      publishedAt: git('show', '-s', '--format=%cI', `${ref}^{commit}`),
      notes: notes.length ? notes.join('\n') : 'Aucun changement supplémentaire.',
    };
  }).reverse();
  return { version, repository, commit: git('rev-parse', sha), releases };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const [, , version, repository, output] = process.argv;
  writeFileSync(output, `${JSON.stringify(generateManifest(version, repository), null, 2)}\n`);
}
