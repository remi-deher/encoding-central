import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import { readFile } from 'node:fs/promises';

const warning = {
  id: 'warn-one',
  timestamp: '2026-10-19T08:00:00Z',
  level: 'Warning',
  category: 'Encoding.Worker',
  message: 'Worker heartbeat delayed',
  nodeId: 'worker-one',
  nodeName: 'atelier',
  eventId: 17,
};
const entries = [
  warning,
  { ...warning, id: 'warn-two', timestamp: '2026-10-19T08:00:01Z' },
  {
    ...warning,
    id: 'err-one',
    level: 'Error',
    category: 'Encoding.Storage',
    message: 'Access denied to library path',
    exception: 'System.UnauthorizedAccessException\n   at LibraryScanner.Scan()',
    timestamp: '2026-10-19T08:00:02Z',
  },
];
test.beforeEach(async ({ page }) => {
  await page.route('**/api/**', (route) => route.fulfill({ json: [] }));
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({
      json: { setupRequired: false, user: { id: 'admin-id', username: 'admin', role: 'admin' } },
    }),
  );
  await page.route('**/api/v1/cluster', (route) =>
    route.fulfill({ json: { id: 'cluster-one', name: 'Maison', standalone: false } }),
  );
  await page.route('**/health/details', (route) =>
    route.fulfill({
      json: {
        status: 'healthy',
        totalDurationMs: 4,
        checks: [{ name: 'database', status: 'healthy', durationMs: 4 }],
      },
    }),
  );
  await page.route('**/api/v1/tools/ai/settings', (route) =>
    route.fulfill({
      json: {
        defaultProvider: 'ollama',
        ollamaBaseUrl: 'http://localhost:11434',
        ollamaModel: 'qwen3:8b',
        openAiBaseUrl: 'https://api.openai.com/v1',
        openAiModel: 'gpt-4o-mini',
        hasOpenAiApiKey: true,
        timeoutSeconds: 120,
        subtitleBatchSize: 80,
      },
    }),
  );
  await page.route('**/api/v1/tools/ai/models*', (route) =>
    route.fulfill({
      json: {
        nodes: [
          {
            nodeId: 'node-one',
            name: 'Atelier',
            providers: [
              {
                provider: 'ollama',
                state: 'available',
                models: [{ id: 'qwen3:8b', label: 'Qwen 3 8B', reasoningEfforts: [] }],
              },
              {
                provider: 'openai',
                state: 'available',
                models: [{ id: 'gpt-4o-mini', label: 'GPT-4o mini', reasoningEfforts: [] }],
              },
            ],
          },
        ],
        policy: { disabledModels: [] },
      },
    }),
  );
  await page.route('**/api/v1/tools/ai/quotas', (route) => route.fulfill({ json: { nodes: [] } }));
  await page.route('**/api/v1/tools/ai/sessions?offset=*', (route) =>
    route.fulfill({ json: { items: [], offset: 0, hasMore: false } }),
  );
  await page.route('**/api/v1/tools/ai/cluster', (route) => route.fulfill({ json: { nodes: [] } }));
  await page.route('**/api/v1/network/settings', (route) =>
    route.fulfill({ json: { trustProxy: false, proxies: [] } }),
  );
  await page.route('**/api/v1/system/logs?*', (route) =>
    route.fulfill({ json: { entries, totalBuffered: entries.length } }),
  );
  await page.route('**/api/v1/system/logs/categories*', (route) =>
    route.fulfill({ json: { categories: ['Encoding.Worker', 'Encoding.Storage'] } }),
  );
  await page.route('**/api/v1/system/logs/stream*', (route) =>
    route.fulfill({ contentType: 'text/event-stream', body: ': connected\n\n' }),
  );
});

test('six domains use the application shell and remain accessible on both themes', async ({
  page,
}, testInfo) => {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.goto('/settings');
  const settings = page.locator('app-settings-page');
  await expect(settings.getByRole('tab')).toHaveCount(6);
  for (const domain of ['Interface', 'Accès', 'IA', 'Connexions', 'Réseau', 'Exploitation']) {
    await settings.getByRole('tab', { name: domain, exact: true }).click();
    await expect(settings.getByRole('tab', { name: domain, exact: true })).toHaveAttribute(
      'aria-selected',
      'true',
    );
    for (const theme of ['light', 'dark']) {
      await page.evaluate((value) => (document.documentElement.dataset['theme'] = value), theme);
      // Audit the settled theme, after the tab color transitions complete.
      await settings.evaluate(async (element) => {
        getComputedStyle(element).color;
        await Promise.all(
          element
            .getAnimations({ subtree: true })
            .map((animation) => animation.finished.catch(() => {})),
        );
      });
      const axe = await new AxeBuilder({ page })
        .include('app-settings-page')
        .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
        .analyze();
      expect(axe.violations).toEqual([]);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
        true,
      );
      await page.screenshot({
        path: testInfo.outputPath(`settings-${domain}-${theme}.png`),
        fullPage: true,
      });
    }
  }
  expect(errors).toEqual([]);
});

test('summary groups unchanged messages, opens their raw console, and exports only filtered events', async ({
  page,
}) => {
  await page.addInitScript(() => {
    Object.defineProperty(navigator.clipboard, 'writeText', {
      value: async (text: string) => {
        (window as unknown as { copied: string }).copied = text;
      },
    });
  });
  await page.goto('/settings?tab=logs');
  const viewer = page.locator('app-system-log-viewer');
  await expect(viewer.locator('.summary-item')).toHaveCount(2);
  const warningGroup = viewer
    .locator('.summary-item')
    .filter({ hasText: 'Worker heartbeat delayed' });
  await expect(warningGroup).toContainText('2 occurrence(s)');
  await warningGroup.getByRole('button', { name: 'Voir dans la console' }).click();
  await expect(viewer.locator('.console-entry.is-focused')).toHaveCount(2);
  await expect(viewer.locator('.terminal-console')).toContainText(
    'UnauthorizedAccessException\n   at LibraryScanner.Scan()',
  );
  await viewer
    .getByRole('searchbox', { name: 'Rechercher dans les journaux' })
    .fill('Access denied');
  await expect(viewer.locator('.console-entry')).toHaveCount(1);
  await viewer.getByRole('button', { name: 'Copier', exact: true }).click();
  const copied = await page.evaluate(() => (window as unknown as { copied: string }).copied);
  expect(copied).toBe(await viewer.locator('.terminal-console').textContent());
  const downloadPending = page.waitForEvent('download');
  await viewer.getByRole('button', { name: 'Exporter', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Événements complets (.json)', exact: true }).click();
  const download = await downloadPending;
  const json = JSON.parse(await readFile((await download.path())!, 'utf8'));
  expect(json).toHaveLength(1);
  expect(json[0].exception).toContain('UnauthorizedAccessException');
  await viewer.getByRole('radio', { name: 'Résumé', exact: true }).click();
  await expect(viewer.locator('.summary-item')).toHaveCount(1);
  await viewer.getByRole('searchbox', { name: 'Rechercher dans les journaux' }).fill('not-found');
  await expect(viewer.getByRole('button', { name: 'Exporter', exact: true })).toBeDisabled();
});

test('creates accounts and API tokens through one shared drawer and clears one-time secrets', async ({
  page,
}) => {
  const users: unknown[] = [];
  const tokens: unknown[] = [];
  await page.route('**/api/v1/users', async (route) => {
    if (route.request().method() === 'POST') {
      const value = route.request().postDataJSON();
      users.push({ id: 'new-user', ...value, isActive: true, createdAt: '2026-10-19T08:00:00Z' });
      await route.fulfill({ json: users[0] });
    } else await route.fulfill({ json: users });
  });
  await page.route('**/api/v1/tokens/replication', (route) =>
    route.fulfill({ json: { nodes: [] } }),
  );
  await page.route('**/api/v1/tokens', async (route) => {
    if (route.request().method() === 'POST') {
      const value = route.request().postDataJSON();
      const token = {
        id: 'new-token',
        ...value,
        isActive: true,
        tokenPrefix: 'enc_pat_prefix',
        createdAt: '2026-10-19T08:00:00Z',
      };
      tokens.push(token);
      await route.fulfill({ json: { ...token, token: 'enc_pat_ONE_TIME_SECRET' } });
    } else await route.fulfill({ json: tokens });
  });
  await page.goto('/settings?tab=access');
  await expect(page.locator('app-settings-page app-detail-drawer')).toHaveCount(1);
  await page.getByRole('button', { name: 'Créer un compte', exact: true }).click();
  let drawer = page.getByRole('dialog', { name: 'Créer un compte', exact: true });
  await drawer
    .getByRole('textbox', { name: 'Nom d’utilisateur', exact: true })
    .fill('diagnostic-viewer');
  await drawer.getByLabel('Mot de passe initial', { exact: true }).fill('very-long-password');
  await drawer.getByRole('button', { name: 'Créer l’utilisateur', exact: true }).click();
  await expect(page.locator('app-user-management-tab')).toContainText('diagnostic-viewer');
  await page.getByRole('button', { name: 'Créer un jeton', exact: true }).click();
  drawer = page.getByRole('dialog', { name: 'Créer un jeton', exact: true });
  await drawer.getByRole('textbox', { name: 'Nom du jeton', exact: true }).fill('Diagnostic agent');
  await drawer.getByLabel('Mot de passe du compte', { exact: true }).fill('very-long-password');
  await drawer.getByRole('button', { name: 'Créer le jeton', exact: true }).click();
  await expect(drawer.getByText('enc_pat_ONE_TIME_SECRET', { exact: true })).toBeVisible();
  await drawer.getByRole('button', { name: 'Terminer', exact: true }).click();
  await expect(page.getByText('enc_pat_ONE_TIME_SECRET', { exact: true })).toHaveCount(0);
  await expect(page.locator('app-api-token-tab')).toContainText('Diagnostic agent');
});

test('discard protection applies to tab clicks and browser history, without double confirmation', async ({
  page,
}) => {
  await page.goto('/settings?tab=interface');
  await page.getByRole('tab', { name: 'IA', exact: true }).click();
  await page.getByRole('combobox', { name: 'Type d’IA', exact: true }).first().click();
  await page.getByRole('option', { name: 'OpenAI · API', exact: true }).click();
  await page.goBack();
  let confirmation = page.getByRole('dialog', { name: 'Quitter sans enregistrer ?', exact: true });
  await expect(confirmation).toBeVisible();
  await confirmation.getByRole('button', { name: 'Annuler', exact: true }).click();
  await expect(page.getByRole('tab', { name: 'IA', exact: true })).toHaveAttribute(
    'aria-selected',
    'true',
  );
  await page.getByRole('tab', { name: 'Exploitation', exact: true }).click();
  confirmation = page.getByRole('dialog', { name: 'Quitter sans enregistrer ?', exact: true });
  await confirmation.getByRole('button', { name: 'Quitter sans enregistrer', exact: true }).click();
  await expect(page.getByRole('tab', { name: 'Exploitation', exact: true })).toHaveAttribute(
    'aria-selected',
    'true',
  );
  await expect(confirmation).toHaveCount(0);
});

test('protects an unfinished account form and restores an empty editor after discard', async ({
  page,
}) => {
  await page.goto('/settings?tab=access');
  await page.getByRole('button', { name: 'Créer un compte', exact: true }).click();
  const drawer = page.getByRole('dialog', { name: 'Créer un compte', exact: true });
  await drawer
    .getByRole('textbox', { name: 'Nom d’utilisateur', exact: true })
    .fill('unfinished-name');
  await drawer.getByRole('button', { name: 'Annuler', exact: true }).click();
  let confirmation = page.getByRole('dialog', { name: 'Quitter sans enregistrer ?', exact: true });
  await expect(confirmation).toBeVisible();
  await confirmation.getByRole('button', { name: 'Annuler', exact: true }).click();
  await expect(drawer.getByRole('textbox', { name: 'Nom d’utilisateur', exact: true })).toHaveValue(
    'unfinished-name',
  );
  await drawer.getByRole('button', { name: 'Annuler', exact: true }).click();
  confirmation = page.getByRole('dialog', { name: 'Quitter sans enregistrer ?', exact: true });
  await confirmation.getByRole('button', { name: 'Quitter sans enregistrer', exact: true }).click();
  await expect(drawer).toHaveCount(0);
  await page.getByRole('button', { name: 'Créer un compte', exact: true }).click();
  await expect(drawer.getByRole('textbox', { name: 'Nom d’utilisateur', exact: true })).toHaveValue(
    '',
  );
});

test('settings stay restricted to administrators', async ({ page }) => {
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({
      json: { setupRequired: false, user: { id: 'reader', username: 'reader', role: 'viewer' } },
    }),
  );
  await page.goto('/settings');
  await expect(page).not.toHaveURL(/\/settings/);
  await expect(page.locator('app-settings-page')).toHaveCount(0);
});
