import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';

export const scopes = [
  {
    workflow: 'backend',
    checks: ['backend-compile', 'backend-runtime-tests', 'backend-launcher-tests', 'backend-plugin-tests', 'backend-prototype-tests', 'backend-static-checks', 'backend-docker-cluster-tests', 'ai-cli-native', 'cluster-two-server-measurements'],
    paths: ['backend/**', '.github/workflows/backend.yml', '.github/actions/apt-install/**', '.github/scripts/apt-install.sh'],
  },
  {
    workflow: 'frontend', checks: ['frontend-fast', 'frontend-e2e'],
    paths: ['frontend/**', '.github/workflows/frontend.yml'],
  },
  {
    workflow: 'image', checks: ['image'],
    paths: ['backend/**', 'frontend/**', '.dockerignore', '.github/workflows/image.yml'],
  },
  {
    workflow: 'release-scripts', checks: ['release-scripts'],
    paths: ['.github/scripts/**', '.github/workflows/release*.yml', '.github/workflows/promotion.yml', '.github/workflows/pr-policy.yml'],
  },
];

export function checkCommits(release, latest) {
  return Object.fromEntries(scopes.flatMap(scope => {
    const sha = latest(release, scope.paths.map(path => path.startsWith('!') ? `:(exclude)${path.slice(1)}` : path));
    if (!/^[a-f0-9]{40}$/.test(sha)) throw new Error(`Missing CI source for ${scope.workflow}`);
    return scope.checks.map(check => [check, sha]);
  }));
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const release = process.env.GITHUB_SHA;
  if (!/^[a-f0-9]{40}$/.test(release ?? '')) throw new Error('Release SHA required');
  console.log(JSON.stringify(checkCommits(release, (sha, paths) => execFileSync(
    'git', ['log', '-1', '--format=%H', sha, '--', ...paths], { encoding: 'utf8' },
  ).trim())));
}
