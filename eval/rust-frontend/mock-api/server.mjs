// Serveur de mesure (#30) : sert un dossier statique (gzip -9, repli SPA sur index.html) et simule
// le contrat /api/v1/workers* du central. Le même serveur sert Angular, Leptos et Dioxus.
// Usage : node server.mjs <dossier> <port>   (WORKERS_COUNT=3 par défaut)
import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { extname, join, normalize } from 'node:path';
import { gzipSync, constants } from 'node:zlib';

const [root, port = '8080'] = process.argv.slice(2);
const count = Number(process.env.WORKERS_COUNT ?? 3);
const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.mjs': 'text/javascript',
  '.css': 'text/css', '.wasm': 'application/wasm', '.json': 'application/json', '.svg': 'image/svg+xml',
  '.woff2': 'font/woff2', '.ico': 'image/x-icon', '.png': 'image/png' };
const cache = new Map();

const worker = (i) => ({
  id: `00000000-0000-4000-8000-${String(i + 1).padStart(12, '0')}`,
  name: i === 0 ? 'central' : `worker-${i}`,
  displayName: i === 0 ? 'Central (worker intégré)' : null,
  roles: ['processing'], maxConcurrentTasks: 2, capabilities: [],
  status: i % 5 === 4 ? 'offline' : 'online', lifecycle: null, lifecycleReason: null,
  lastSeenAt: '2026-11-07T22:00:00Z', labels: {},
  inventory: { operatingSystem: 'Linux', architecture: 'x64', processorCount: 8 + (i % 3) * 4,
    memoryMb: 16384, processorModel: 'AMD Ryzen 7 5700G', gpus: [], runtimeEnvironment: 'docker' },
});
const workers = Array.from({ length: count }, (_, i) => worker(i));
const pending = [{ id: 'req-1', name: 'nas-encoder', roles: ['processing'], maxConcurrentTasks: 1,
  capabilities: [], createdAt: '2026-11-07T22:00:00Z', expiresAt: '2026-11-07T23:00:00Z',
  verificationCode: '482913', displayName: null }];

function json(res, status, body) {
  res.writeHead(status, { 'content-type': 'application/json', 'cache-control': 'no-store' });
  res.end(JSON.stringify(body));
}

async function api(req, res, path) {
  if (path === '/api/v1/live' || path.endsWith('/stream')) {
    res.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-store', connection: 'keep-alive' });
    res.write('event: ready\ndata: {}\n\n');
    const ping = setInterval(() => res.write('event: ping\ndata: {}\n\n'), 15_000);
    req.on('close', () => clearInterval(ping));
    return;
  }
  if (path === '/api/v1/auth/status') return json(res, 200, { setupRequired: false,
    user: { id: '00000000-0000-0000-0000-000000000001', username: 'admin', role: 'admin' } });
  if (path === '/api/v1/workers') return json(res, 200, workers);
  if (path === '/api/v1/workers/enrollment-requests') return json(res, 200, pending);
  if (path === '/api/v1/workers/enrollment-requests/approve' && req.method === 'POST') {
    let body = ''; for await (const chunk of req) body += chunk;
    const key = JSON.parse(body || '{}').pairingKey;
    if (key !== 'GOOD-KEY') return json(res, 404, { title: 'Not found' });
    const joined = { ...worker(workers.length), name: 'nas-encoder' };
    workers.push(joined); pending.length = 0;
    return json(res, 200, joined);
  }
  return json(res, 200, []);
}

async function file(req, res, path) {
  let target = normalize(join(root, decodeURIComponent(path)));
  if (!target.startsWith(normalize(root))) { res.writeHead(403); return res.end(); }
  try { if (!(await stat(target)).isFile()) throw new Error('dir'); }
  catch { target = join(root, 'index.html'); }
  let entry = cache.get(target);
  if (!entry) {
    const raw = await readFile(target);
    entry = { raw, gz: gzipSync(raw, { level: constants.Z_BEST_COMPRESSION }) };
    cache.set(target, entry);
  }
  const gzip = /\bgzip\b/.test(req.headers['accept-encoding'] ?? '');
  res.writeHead(200, { 'content-type': types[extname(target)] ?? 'application/octet-stream',
    'cache-control': 'no-store', ...(gzip ? { 'content-encoding': 'gzip' } : {}) });
  res.end(gzip ? entry.gz : entry.raw);
}

createServer((req, res) => {
  const path = new URL(req.url, 'http://x').pathname;
  (path.startsWith('/api/') ? api(req, res, path) : file(req, res, path)).catch((e) => {
    res.writeHead(500); res.end(String(e));
  });
}).listen(Number(port), '127.0.0.1', () => console.log(`ready ${root} :${port}`));
