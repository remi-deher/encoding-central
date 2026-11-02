import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';

// The closed-PR listing exceeds Node's default 1 MiB buffer once the history grows.
const run = (command, args) => execFileSync(command, args, { encoding: 'utf8', maxBuffer: 256 * 1024 * 1024 }).trim();
const api = (path) => JSON.parse(run('gh', ['api', path]));
const pages = (path) => JSON.parse(run('gh', ['api', '--paginate', '--slurp', path])).flat();

export function eligible(branch, pr, repo, openBranches, included) {
  return !['main', 'dev'].includes(branch.name)
    && !branch.protected
    && !openBranches.has(branch.name)
    && pr.merged_at != null
    && pr.base.ref === 'dev'
    && pr.head.repo?.full_name === repo
    && pr.head.ref === branch.name
    && pr.head.sha === branch.commit.sha
    && Boolean(pr.merge_commit_sha)
    && included(pr.merge_commit_sha);
}

export function deletionArgs(name, sha) {
  const ref = `refs/heads/${name}`;
  // The server rejects deletion if anyone pushed after our last check.
  return ['push', `--force-with-lease=${ref}:${sha}`, 'origin', `:${ref}`];
}

export function main() {
  const repo = process.env.GITHUB_REPOSITORY;
  const release = process.env.GITHUB_SHA;
  if (!repo || !/^[a-f0-9]{40}$/.test(release ?? '')) throw new Error('Repository and release SHA required');
  const apply = process.argv.includes('--apply');
  run('git', ['fetch', 'origin', 'dev']);
  const dev = run('git', ['rev-parse', 'FETCH_HEAD']);
  function ancestor(sha, target) {
    try { run('git', ['merge-base', '--is-ancestor', sha, target]); return true; }
    catch (error) { if (error.status === 1) return false; throw error; }
  }
  const included = (sha) => ancestor(sha, dev) && ancestor(sha, release);
  const openBranches = () => new Set(pages(`repos/${repo}/pulls?state=open&per_page=100`)
    .filter(pr => pr.head.repo?.full_name === repo).map(pr => pr.head.ref));
  const open = openBranches();
  const prs = pages(`repos/${repo}/pulls?state=closed&base=dev&per_page=100`);
  for (const branch of pages(`repos/${repo}/branches?per_page=100`)) {
    const pr = prs.find(pr => eligible(branch, pr, repo, open, included));
    if (!pr) { console.log(`KEEP ${branch.name}`); continue; }
    // Refresh both PR and branch before mutation; lease covers concurrent pushes.
    const fresh = api(`repos/${repo}/branches/${encodeURIComponent(branch.name)}`);
    const detail = api(`repos/${repo}/pulls/${pr.number}`);
    if (!eligible(fresh, detail, repo, openBranches(), included)) {
      console.log(`KEEP ${branch.name}: changed during inspection`);
      continue;
    }
    console.log(`${apply ? 'DELETE' : 'WOULD DELETE'} ${branch.name} (${fresh.commit.sha}, PR #${pr.number})`);
    if (apply) run('git', deletionArgs(branch.name, fresh.commit.sha));
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) main();
