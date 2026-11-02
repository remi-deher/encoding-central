import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';

const now = '2026-10-19T12:00:00Z';
const running = {
  id: 'run-1',
  media: 'Dune : Deuxième partie.mkv',
  library: 'Films',
  flow: 'HEVC',
  flowVersion: 2,
  state: 'running',
  isActivelyProcessing: true,
  nodeId: 'n1',
  node: 'Studio',
  progress: 100,
  detail: 'Validation de la sortie',
  attempt: 1,
  revision: 1,
  queuedAt: now,
  startedAt: now,
  finishedAt: null,
  updatedAt: now,
};
const succeeded = {
  ...running,
  id: 'done-1',
  media: 'Severance S02E03.mkv',
  state: 'succeeded',
  finishedAt: now,
};
const counts = {
  activeCount: 4,
  queuedCount: 18,
  succeededCount: 126,
  failedCount: 2,
  cancelledCount: 0,
  deferredCount: 1,
  activity: [],
  observedAt: now,
};

test.beforeEach(async ({ page }) => {
  await page.route('**/api/**', (route) => route.fulfill({ json: [] }));
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({
      json: { setupRequired: false, user: { id: 'admin', username: 'admin', role: 'admin' } },
    }),
  );
  await page.route('**/api/v1/cluster', (route) =>
    route.fulfill({ json: { id: 'cluster', name: 'Maison', standalone: false } }),
  );
  await page.route('**/api/v1/nodes', (route) =>
    route.fulfill({
      json: [
        {
          id: 'n1',
          name: 'studio',
          displayName: 'Studio',
          status: 'online',
          lifecycle: 'online',
          roles: ['worker', 'server'],
          maxConcurrentTasks: 3,
          lastSeenAt: now,
          capabilities: [],
          inventory: {
            processorModel: 'Intel Core',
            processorCount: 8,
            memoryMb: 16384,
            architecture: 'x64',
            operatingSystem: 'Linux',
            gpus: [{ name: 'RTX 4070' }],
          },
        },
      ],
    }),
  );
  await page.route('**/api/v1/libraries', (route) => route.fulfill({ json: [] }));
  await page.route('**/api/v1/executions*', (route) => {
    if (route.request().url().includes('/stream')) return route.abort();
    const state = new URL(route.request().url()).searchParams.get('state');
    // Global journal deliberately contains no active or completed records.
    return route.fulfill({
      json: {
        ...counts,
        executions: state === 'running' ? [running] : state === 'succeeded' ? [succeeded] : [],
      },
    });
  });
});

test('overview uses backend counts and filtered records with accessible responsive presentation', async ({
  page,
}, info) => {
  await page.goto('/');
  const home = page.locator('app-home-page');
  const metrics = home.locator('app-summary-tiles app-metric-card');
  await expect(metrics.nth(0)).toContainText('4');
  await expect(metrics.nth(1)).toContainText('18');
  await expect(metrics.nth(2)).toContainText('126');
  await expect(metrics.nth(3)).toContainText('0 / 0');
  await expect(home.locator('app-overview-execution')).toHaveCount(1);
  await expect(home.locator('app-overview-execution')).toContainText('En cours');
  await expect(home.locator('app-overview-execution')).toContainText('100 %');
  await expect(home.locator('.remaining-link')).toContainText('3 autres traitements');
  await expect(home.locator('.publication')).toContainText('Severance');
  await expect(home.locator('.node-row')).toContainText('RTX 4070');
  await expect(home.locator('.attention')).toContainText('2 tâches');
  await expect(home).not.toContainText('fonctionne normalement');
  for (const theme of ['light', 'dark']) {
    await page.evaluate((value) => (document.documentElement.dataset['theme'] = value), theme);
    // Await the global theme color transitions before measuring contrast.
    await page.evaluate(async () => {
      await Promise.all(
        document.getAnimations().map((animation) => animation.finished.catch(() => undefined)),
      );
    });
    const axe = await new AxeBuilder({ page })
      .include('app-home-page')
      .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
      .analyze();
    expect(axe.violations.map((v) => ({ id: v.id, nodes: v.nodes.map((n) => n.target) }))).toEqual(
      [],
    );
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
      true,
    );
    await page.screenshot({ path: info.outputPath(`overview-${theme}.png`), fullPage: true });
  }
  await home.getByRole('link', { name: 'Examiner les échecs' }).click();
  await expect(page).toHaveURL(/activity\?view=errors/);
});

test('background refresh preserves known records and reports stale values', async ({ page }) => {
  let unavailable = false;
  await page.route('**/api/v1/executions*', (route) => {
    if (route.request().url().includes('/stream')) return route.fallback();
    return unavailable ? route.fulfill({ status: 503, json: {} }) : route.fallback();
  });
  await page.clock.install();
  await page.goto('/');
  const home = page.locator('app-home-page');
  await expect(home.locator('app-overview-execution')).toContainText('Dune');
  unavailable = true;
  await page.clock.fastForward(5_000);
  await expect(home.getByRole('alert').first()).toContainText('obsolètes');
  await expect(home.locator('app-overview-execution')).toContainText('Dune');
  const firstMetric = home.locator('app-summary-tiles app-metric-card').first();
  await expect(firstMetric.locator('.metric-card')).toHaveClass(/metric-card_stale/);
  await expect(firstMetric).toContainText('4');
});

test('initial outage displays unavailable metrics without claiming an empty history', async ({
  page,
}) => {
  await page.route('**/api/v1/executions*', (route) => route.fulfill({ status: 503, json: {} }));
  await page.goto('/');
  const home = page.locator('app-home-page');
  await expect(home.getByRole('alert').first()).toBeVisible();
  await expect(
    home.locator('app-summary-tiles app-metric-card').first().locator('.metric-value'),
  ).toHaveText('—');
  await expect(home).not.toContainText('Aucune exécution en cours.');
  await expect(home).not.toContainText('Aucun traitement terminé.');
});

test('AI overview is integrated into home and the former route redirects there', async ({
  page,
}) => {
  let settingsUnavailable = false;
  await page.route('**/api/v1/tools/ai/settings', (route) =>
    settingsUnavailable
      ? route.abort()
      : route.fulfill({
          json: {
            defaultProvider: 'ollama',
            ollamaBaseUrl: 'http://localhost:11434',
            ollamaModel: 'local',
            openAiBaseUrl: '',
            openAiModel: '',
          },
        }),
  );
  await page.route('**/api/v1/tools/ai/cluster', (r) =>
    r.fulfill({ json: { nodes: [{ nodeId: 'n1', name: 'Studio', providers: ['ollama'] }] } }),
  );
  await page.clock.install();
  await page.goto('/ai');
  await expect(page).toHaveURL(/\/\?view=ai$/);
  await expect(page.locator('app-home-page')).toBeVisible();
  await expect(
    page.getByRole('link', { name: 'Vue d’ensemble', exact: true, includeHidden: true }),
  ).toHaveCount(1);
  await expect(page.getByRole('heading', { name: 'Fournisseurs', exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Nœuds déclarants', exact: true })).toBeVisible();
  await expect(page.locator('app-ai-provider-overview')).toContainText('Studio');
  settingsUnavailable = true;
  await page.clock.fastForward(30_000);
  await expect(
    page.getByRole('heading', { name: 'État IA partiellement indisponible' }),
  ).toBeVisible();
});
