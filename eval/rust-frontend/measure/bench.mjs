// Banc de mesure (#30). Pour chaque appli : chargement à froid (N essais), octets transférés,
// temps jusqu'à l'affichage de la liste des workers, puis repos 30 s (CPU et PSS du processus de rendu).
// Usage : node bench.mjs <nom>=<url> [...]   (PLAYWRIGHT_BROWSERS_PATH, RUNS=5, PROFILE=local|slow4g)
import { chromium } from '@playwright/test';
import { readFileSync } from 'node:fs';

const runs = Number(process.env.RUNS ?? 5);
const profile = process.env.PROFILE ?? 'local';
const idleMs = Number(process.env.IDLE_MS ?? 30_000);
const apps = process.argv.slice(2).map((a) => [a.slice(0, a.indexOf('=')), a.slice(a.indexOf('=') + 1)]);
const median = (xs) => { const s = [...xs].sort((a, b) => a - b); return s[Math.floor(s.length / 2)]; };

function pss(pid) {
  const text = readFileSync(`/proc/${pid}/smaps_rollup`, 'utf8');
  return Number(/^Pss:\s+(\d+)/m.exec(text)[1]); // Kio
}

async function renderer(browser) {
  const cdp = await browser.newBrowserCDPSession();
  const { processInfo } = await cdp.send('SystemInfo.getProcessInfo');
  return processInfo.filter((p) => p.type === 'renderer');
}

async function once(url, measureIdle) {
  const browser = await chromium.launch();
  try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
    const page = await context.newPage();
    const cdp = await context.newCDPSession(page);
    await cdp.send('Network.enable');
    await cdp.send('Performance.enable');
    if (profile === 'slow4g') {
      await cdp.send('Network.emulateNetworkConditions', { offline: false, latency: 150,
        downloadThroughput: (1.6 * 1024 * 1024) / 8, uploadThroughput: (750 * 1024) / 8 });
      await cdp.send('Emulation.setCPUThrottlingRate', { rate: 4 });
    }
    const urls = new Map(); let bytes = 0; const errors = [];
    cdp.on('Network.responseReceived', (e) => urls.set(e.requestId, e.response.url));
    cdp.on('Network.loadingFinished', (e) => {
      if (!(urls.get(e.requestId) ?? '').includes('/api/')) bytes += e.encodedDataLength;
    });
    page.on('pageerror', (e) => errors.push(e.message));
    const start = Date.now();
    await page.goto(url);
    await page.getByText('worker-1', { exact: true }).filter({ visible: true }).first().waitFor({ state: 'visible', timeout: 60_000 });
    const readyMs = Date.now() - start;
    const nav = await page.evaluate(() => {
      const n = performance.getEntriesByType('navigation')[0];
      const fcp = performance.getEntriesByName('first-contentful-paint')[0];
      return { domContentLoaded: Math.round(n.domContentLoadedEventEnd), fcp: fcp ? Math.round(fcp.startTime) : null };
    });
    let idle = null;
    if (measureIdle) {
      await page.waitForTimeout(5_000);
      const [r0] = await renderer(browser);
      const t0 = r0.cpuTime;
      await page.waitForTimeout(idleMs);
      const [r1] = await renderer(browser);
      const { metrics } = await cdp.send('Performance.getMetrics');
      const heap = metrics.find((m) => m.name === 'JSHeapUsedSize').value;
      idle = { cpuMsPerMin: Math.round(((r1.cpuTime - t0) * 1000 * 60_000) / idleMs),
        pssKiB: pss(r1.id), jsHeapKiB: Math.round(heap / 1024) };
    }
    return { readyMs, bytes, ...nav, idle, errors };
  } finally {
    await browser.close();
  }
}

const results = {};
for (const [name, url] of apps) {
  const samples = [];
  for (let i = 0; i < runs; i++) samples.push(await once(url, i === runs - 1));
  const last = samples.at(-1);
  results[name] = { profile, runs, readyMsMedian: median(samples.map((s) => s.readyMs)),
    fcpMedian: median(samples.map((s) => s.fcp ?? 0)),
    transferredKiB: Math.round(last.bytes / 1024), idle: last.idle,
    errors: [...new Set(samples.flatMap((s) => s.errors))] };
  console.error(name, JSON.stringify(results[name]));
}
console.log(JSON.stringify(results, null, 2));
