import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';

const now = '2026-10-23T12:00:00Z';
const running = {
  id: 'run-1',
  media: 'Séries/Épisode en cours.mkv',
  library: 'Séries',
  flow: 'HEVC',
  flowVersion: 3,
  state: 'running',
  isActivelyProcessing: true,
  nodeId: 'node-1',
  node: 'Studio',
  progress: 100,
  detail: 'Validation avant publication',
  attempt: 1,
  revision: 3,
  queuedAt: now,
  startedAt: now,
  finishedAt: null,
  updatedAt: now,
};
const counts = {
  activeCount: 1,
  queuedCount: 6000,
  succeededCount: 60,
  failedCount: 1,
  cancelledCount: 0,
  deferredCount: 0,
  activity: [],
  observedAt: now,
};
const activityCounts = { untreated: 6000, active: 2, done: 60, failed: 1, cancelled: 0, disabled: 0 };

function activityResponse(status: string, offset: number, limit: number) {
  const totalCount = activityCounts[status as keyof typeof activityCounts] ?? 0;
  let files: readonly unknown[] = [];
  if (status === 'done') {
    files = Array.from({ length: Math.min(limit, Math.max(0, totalCount - offset)) }, (_, index) => ({
      id: `file-${offset + index}`,
      libraryId: 'library-1',
      library: 'Films',
      relativePath: `Film terminé ${offset + index}.mkv`,
      bytes: 1024,
      modifiedAt: now,
      libraryActive: true,
      flow: 'HEVC',
      flowVersion: 3,
      status: 'done',
      executionId: `done-${offset + index}`,
      executionState: 'succeeded',
      detail: 'Traitement terminé',
      nodeId: 'node-1',
      node: 'Studio',
      progress: 100,
      updatedAt: now,
    }));
  } else if (status === 'failed') {
    files = Array.from({ length: Math.min(limit, Math.max(0, totalCount - offset)) }, (_, index) => ({
      id: `file-failed-${offset + index}`,
      libraryId: 'library-1',
      library: 'Films',
      relativePath: `Film en erreur ${offset + index}.mkv`,
      bytes: 1024,
      modifiedAt: now,
      libraryActive: true,
      flow: 'HEVC',
      flowVersion: 3,
      status: 'failed',
      executionId: `failed-${offset + index}`,
      executionState: 'failed',
      detail: 'Erreur encodage',
      nodeId: 'node-1',
      node: 'Studio',
      progress: null,
      updatedAt: now,
    }));
  } else if (status === 'cancelled') {
    files = Array.from({ length: Math.min(limit, Math.max(0, totalCount - offset)) }, (_, index) => ({
      id: `file-cancelled-${offset + index}`,
      libraryId: 'library-1',
      library: 'Films',
      relativePath: `Film annulé ${offset + index}.mkv`,
      bytes: 1024,
      modifiedAt: now,
      libraryActive: true,
      flow: 'HEVC',
      flowVersion: 3,
      status: 'cancelled',
      executionId: `cancelled-${offset + index}`,
      executionState: 'cancelled',
      detail: 'Annulé par utilisateur',
      nodeId: 'node-1',
      node: 'Studio',
      progress: null,
      updatedAt: now,
    }));
  } else if (status === 'active') {
    files = Array.from({ length: Math.min(limit, Math.max(0, totalCount - offset)) }, (_, index) => ({
      id: `file-active-${offset + index}`,
      libraryId: 'library-1',
      library: 'Films',
      relativePath: `Film en cours ${offset + index}.mkv`,
      bytes: 1024,
      modifiedAt: now,
      libraryActive: true,
      flow: 'HEVC',
      flowVersion: 3,
      status: 'active',
      executionId: `active-${offset + index}`,
      executionState: 'running',
      detail: 'Encodage en cours',
      nodeId: 'node-1',
      node: 'Studio',
      progress: 50,
      updatedAt: now,
    }));
  }
  return {
    files,
    counts: activityCounts,
    totalCount,
    limit,
    offset,
    observedAt: now,
  };
}

test.beforeEach(async ({ page }) => {
  await page.route('**/api/**', (route) => route.fulfill({ json: [] }));
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({
      json: { setupRequired: false, user: { id: 'admin', username: 'admin', role: 'admin' } },
    }),
  );
  await page.route('**/api/v1/cluster', (route) =>
    route.fulfill({ json: { id: 'cluster', name: 'Maison', standalone: true } }),
  );
  await page.route('**/api/v1/activity/stream**', (route) => route.abort());
  await page.route('**/api/v1/activity/files**', (route) => {
    const url = new URL(route.request().url());
    return route.fulfill({
      json: activityResponse(
        url.searchParams.get('status') ?? 'untreated',
        Number(url.searchParams.get('offset') ?? 0),
        Number(url.searchParams.get('limit') ?? 50),
      ),
    });
  });
  await page.route('**/api/v1/tools/ai/assistant/activity', (route) => route.fulfill({ json: [] }));
  await page.route(/\/api\/v1\/executions(\/|\?|$)/, (route) => {
    const url = new URL(route.request().url());
    if (url.pathname.endsWith('/stream')) return route.abort();
    if (
      url.pathname.endsWith('/pipeline') ||
      url.pathname.endsWith('/events') ||
      url.pathname.endsWith('/artifacts')
    ) {
      return route.fulfill({ json: [] });
    }
    if (url.pathname.endsWith('/flow')) {
      return route.fulfill({ json: { nodes: [], connections: [] } });
    }
    const parts = url.pathname.split('/').filter(Boolean);
    if (parts.length === 4) {
      const id = parts[3];
      return route.fulfill({
        json: {
          ...running,
          id,
          state: id.startsWith('failed')
            ? 'failed'
            : id.startsWith('cancelled')
              ? 'cancelled'
              : 'running',
          steps: [],
        },
      });
    }
    const state = url.searchParams.get('state');
    const offset = Number(url.searchParams.get('offset') ?? 0);
    const limit = Number(url.searchParams.get('limit') ?? 25);
    let executions: (typeof running)[] = [];
    let totalCount = 0;
    if (state === 'succeeded') {
      totalCount = 60;
      executions = Array.from({ length: Math.min(limit, Math.max(0, 60 - offset)) }, (_, i) => ({
        ...running,
        id: 'done-' + (offset + i),
        media: `Film terminé ${offset + i}.mkv`,
        state: 'succeeded',
        finishedAt: now,
      }));
    } else if (state === 'running') {
      executions = [running];
      totalCount = 1;
    } else if (state === 'cancelling') {
      executions = [
        {
          ...running,
          id: 'cancel-1',
          media: 'Annulation.mkv',
          state: 'cancelling',
          progress: null,
        },
      ];
      totalCount = 1;
    } else if (!state || state === 'queued') {
      executions = [
        { ...running, id: 'queue-1', media: 'En attente.mkv', state: 'queued', progress: null },
      ];
      totalCount = 6000;
    }
    return route.fulfill({ json: { ...counts, executions, totalCount, limit, offset } });
  });
});

test('completed jobs stay visible behind a large queue and history is paginated', async ({
  page,
}) => {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.goto('/activity?view=history');
  const activity = page.locator('app-activity-page');
  await expect(activity.getByText('Film terminé 0.mkv', { exact: true })).toBeVisible();
  await expect(activity.getByRole('tab', { name: /Déjà traité/ })).toHaveAttribute(
    'aria-selected',
    'true',
  );
  await expect(activity).toContainText('6063 fichiers suivis');
  await activity.getByRole('button', { name: 'Suivant' }).click();
  await expect(activity.getByText('Film terminé 50.mkv', { exact: true })).toBeVisible();
  await activity.getByRole('button', { name: 'Précédent' }).click();
  await expect(activity.getByText('Film terminé 0.mkv', { exact: true })).toBeVisible();
  expect(errors).toEqual([]);
});

test('jobs pill shows only accessible jobs with confirmed worker processing', async ({
  page,
}) => {
  await page.goto('/activity');
  const trigger = page.getByRole('button', { name: '1 job en cours', exact: true });
  await trigger.click();
  const panel = page.getByRole('dialog', { name: 'Jobs en cours', exact: true });
  await expect(panel).toBeVisible();
  await expect(panel).toContainText('Épisode en cours.mkv');
  await expect(panel).toContainText('Validation avant publication');
  await expect(panel).not.toContainText('Annulation.mkv');
  await expect(panel).not.toContainText('Annulation demandée');
  await expect(panel).toContainText('En cours');
  await expect(panel.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '100');
  const accessibility = await new AxeBuilder({ page })
    .include('[role="dialog"][aria-label="Jobs en cours"]')
    .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
    .analyze();
  expect(accessibility.violations.map((violation) => violation.id)).toEqual([]);
  await page.keyboard.press('Escape');
  await expect(panel).not.toBeVisible();
  await expect(trigger).toBeFocused();
  await trigger.click();
  await panel.getByRole('link', { name: 'Voir l’activité' }).click();
  await expect(panel).not.toBeVisible();
  await expect(page).toHaveURL(/view=active/);
});

test('viewers do not request administrator-only AI history', async ({ page }) => {
  let aiRequests = 0;
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({
      json: { setupRequired: false, user: { id: 'viewer', username: 'viewer', role: 'viewer' } },
    }),
  );
  await page.route('**/api/v1/tools/ai/assistant/activity', (route) => {
    aiRequests++;
    return route.fulfill({ status: 403, json: {} });
  });
  await page.goto('/activity?view=history');
  await expect(page.getByText('Film terminé 0.mkv', { exact: true })).toBeVisible();
  expect(aiRequests).toBe(0);
  await expect(
    page.getByText('Les demandes de l’assistant sont momentanément indisponibles.'),
  ).toHaveCount(0);
});

test('activity keeps the last results visible through a refresh outage and recovers automatically', async ({
  page,
}) => {
  let unavailable = false;
  await page.clock.install();
  await page.route('**/api/v1/activity/files**', (route) =>
    unavailable ? route.fulfill({ status: 503, json: {} }) : route.fallback(),
  );
  await page.goto('/activity?view=history');
  const activity = page.locator('app-activity-page');
  await expect(activity.getByText('Film terminé 0.mkv', { exact: true })).toBeVisible();
  unavailable = true;
  await page.clock.fastForward(10_000);
  await expect(activity.getByRole('status')).toContainText('L’activité n’a pas pu être actualisée');
  await expect(activity.getByText('Film terminé 0.mkv', { exact: true })).toBeVisible();
  unavailable = false;
  await page.clock.fastForward(10_000);
  await expect(
    activity.getByText('L’activité n’a pas pu être actualisée.', { exact: false }),
  ).toHaveCount(0);
});

test('background failures preserve history and recover automatically, with responsive light and dark themes', async ({
  page,
}, info) => {
  let unavailable = false;
  await page.route('**/api/v1/activity/files**', (route) =>
    unavailable ? route.fulfill({ status: 503, json: {} }) : route.fallback(),
  );
  await page.clock.install();
  await page.goto('/activity?view=history');
  const activity = page.locator('app-activity-page');
  await expect(activity.getByText('Film terminé 0.mkv', { exact: true })).toBeVisible();
  unavailable = true;
  await page.clock.fastForward(10_000);
  await expect(activity.getByRole('status')).toContainText('L’activité n’a pas pu être actualisée');
  await expect(activity.getByText('Film terminé 0.mkv', { exact: true })).toBeVisible();
  unavailable = false;
  await page.clock.fastForward(10_000);
  await expect(
    activity.getByText('L’activité n’a pas pu être actualisée.', { exact: false }),
  ).toHaveCount(0);
  for (const theme of ['light', 'dark']) {
    await page.evaluate((value) => (document.documentElement.dataset['theme'] = value), theme);
    await page.evaluate(async () => {
      await Promise.all(
        document.getAnimations().map((animation) => animation.finished.catch(() => undefined)),
      );
    });
    const result = await new AxeBuilder({ page })
      .include('app-activity-page')
      .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
      .analyze();
    expect(
      result.violations.map((v) => ({ id: v.id, targets: v.nodes.map((n) => n.target) })),
    ).toEqual([]);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
      true,
    );
    await page.screenshot({ path: info.outputPath(`activity-${theme}.png`), fullPage: true });
  }
});

test('batch selection provides select all and contextual actions', async ({ page }) => {
  await page.goto('/activity?state=active');
  const activity = page.locator('app-activity-page');
  await expect(activity.getByText('Film en cours 0.mkv')).toBeVisible();

  const selectAll = activity.getByRole('checkbox', {
    name: /Sélectionner ou désélectionner tous les fichiers/,
  });
  await expect(selectAll).toBeVisible();
  await selectAll.click();

  const toolbar = activity.getByRole('toolbar', { name: 'Actions groupées' });
  await expect(toolbar).toBeVisible();
  await expect(toolbar).toContainText('2 fichiers sélectionnés');
  await expect(toolbar.getByRole('button', { name: /Annuler la tâche/ })).toBeVisible();

  await toolbar.getByRole('button', { name: 'Tout désélectionner' }).click();
  await expect(toolbar).not.toBeVisible();
});

test('cancelled tab is present and displays cancelled jobs', async ({ page }) => {
  await page.goto('/activity');
  const activity = page.locator('app-activity-page');
  const cancelledTab = activity.getByRole('tab', { name: /Annulées/ });
  await expect(cancelledTab).toBeVisible();
  await cancelledTab.click();
  await expect(page).toHaveURL(/state=cancelled/);
});

test('drawer header does not contain re-traiter button on failed jobs', async ({ page }) => {
  await page.goto('/activity?state=failed');
  const activity = page.locator('app-activity-page');
  await expect(activity.getByText('Film en erreur 0.mkv')).toBeVisible();
  await activity.getByText('Film en erreur 0.mkv').click();

  const drawer = activity.locator('app-detail-drawer mat-drawer');
  await expect(drawer).toBeVisible();
  const header = drawer.locator('.drawer-header');
  await expect(header.getByRole('button', { name: 'Re-traiter' })).toHaveCount(0);

  const footer = drawer.locator('.drawer-footer');
  await expect(footer.getByRole('button', { name: 'Créer une nouvelle tentative' })).toBeVisible();
});
