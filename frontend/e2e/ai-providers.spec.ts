import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';

test.beforeEach(async ({ page }) => {
  // Catch-all routes must be registered first: Playwright evaluates routes in reverse order.
  await page.route('**/api/**', (route) => route.fulfill({ json: [] }));
  await page.route('**/api/v1/tools/ai/models*', (route) =>
    route.fulfill({
      json: {
        nodes: [
          {
            nodeId: 'first',
            name: 'Salon',
            providers: [
              {
                provider: 'ollama',
                state: 'available',
                models: [{ id: 'llama3.2', label: 'Llama 3.2', reasoningEfforts: [] }],
              },
            ],
          },
        ],
        policy: { disabledModels: [] },
      },
    }),
  );
  await page.route('**/api/v1/system/logs/categories*', (route) =>
    route.fulfill({ json: { categories: [] } }),
  );
  await page.route('**/health/details', (route) =>
    route.fulfill({ json: { status: 'healthy', totalDurationMs: 0, checks: [] } }),
  );
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({
      json: {
        setupRequired: false,
        user: { id: '00000000-0000-0000-0000-000000000001', username: 'admin', role: 'admin' },
      },
    }),
  );
  await page.route('**/api/v1/tools/ai', (route) =>
    route.fulfill({
      json: ['codex-cli', 'claude-cli'].map((id) => ({
        id,
        name: id,
        category: 'CLI',
        description: 'CLI',
        isInstalled: true,
        capabilities: [],
        authStatus: { toolId: id, state: 'unconfigured' },
      })),
    }),
  );
  await page.route('**/api/v1/tools/ai/settings', (route) =>
    route.fulfill({
      json: {
        defaultProvider: 'ollama',
        ollamaBaseUrl: 'http://localhost:11434/v1/',
        ollamaModel: 'llama3.2',
        openAiBaseUrl: 'https://api.openai.com/v1/',
        openAiModel: 'gpt-4o-mini',
        hasOpenAiApiKey: true,
        timeoutSeconds: 120,
        subtitleBatchSize: 80,
      },
    }),
  );
  await page.route('**/api/v1/tools/ai/sessions?offset=*', (route) =>
    route.fulfill({ json: { items: [], offset: 0, hasMore: false } }),
  );
  await page.route('**/api/v1/tools/ai/quotas', (route) => route.fulfill({ json: { nodes: [] } }));
  await page.route('**/api/v1/tools/ai/cluster', (route) =>
    route.fulfill({
      json: {
        nodes: [
          { nodeId: 'first', name: 'Salon', providers: ['ollama'] },
          { nodeId: 'second', name: 'Bureau', providers: ['codex-cli'] },
        ],
      },
    }),
  );
});

test('provider settings remain focused, accessible and usable in both themes', async ({
  page,
}, testInfo) => {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.goto('/settings?tab=ai');
  const tab = page.locator('app-ai-tools-tab');
  await expect(tab.getByRole('heading', { name: 'Fournisseurs', exact: true })).toBeVisible();
  await tab.getByRole('button', { name: 'Configurer Ollama', exact: true }).click();
  let editor = page.getByRole('dialog', { name: 'Ollama', exact: true });
  await expect(editor.getByLabel('Adresse du serveur')).toBeVisible();
  await expect(tab.getByLabel('Clé API', { exact: true })).toHaveCount(0);
  await editor.getByRole('button', { name: 'Fermer le panneau' }).click();
  await tab
    .getByRole('button', { name: 'Configurer OpenAI / API compatible', exact: true })
    .click();
  editor = page.getByRole('dialog', { name: 'OpenAI / API compatible', exact: true });
  await expect(editor.getByLabel('Clé API', { exact: true })).toHaveAttribute('type', 'password');
  await expect(editor.getByLabel('Adresse du serveur')).toHaveCount(0);
  await expect(tab.getByRole('combobox', { name: 'Type d’IA', exact: true }).first()).toContainText(
    'Ollama',
  );
  await expect(tab.getByText('Simuler', { exact: false })).toHaveCount(0);
  await editor.getByRole('button', { name: 'Fermer le panneau' }).click();
  for (const theme of ['light', 'dark']) {
    await page.evaluate(
      (value) => document.documentElement.setAttribute('data-theme', value),
      theme,
    );
    const result = await new AxeBuilder({ page })
      .include('app-ai-tools-tab')
      .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
      .analyze();
    expect(result.violations).toEqual([]);
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
    ).toBe(true);
    await page.screenshot({
      path: testInfo.outputPath(`ai-providers-${theme}.png`),
      fullPage: true,
    });
  }
  expect(errors).toEqual([]);
});

test('Codex only shows the supplied code and closing keeps the attempt active', async ({
  page,
}) => {
  let cancelled = false;
  await page.route('**/api/v1/tools/ai/codex-cli/auth/device-code/start', (route) =>
    route.fulfill({
      json: {
        toolId: 'codex-cli',
        prompt: {
          userCode: 'ABCD-12345',
          verificationUri: 'https://auth.openai.com/codex/device',
          expiresInSeconds: 60,
          intervalSeconds: 5,
        },
      },
    }),
  );
  await page.route('**/api/v1/tools/ai/codex-cli/auth/device-code/status', (route) =>
    route.fulfill({ json: { toolId: 'codex-cli', state: 'pending' } }),
  );
  await page.route('**/api/v1/tools/ai/codex-cli/auth/cancel', async (route) => {
    cancelled = true;
    await route.fulfill({ json: {} });
  });
  await page.goto('/settings?tab=ai');
  await page
    .locator('app-ai-tools-tab')
    .getByRole('button', { name: 'Configurer Codex CLI', exact: true })
    .click();
  await page.getByRole('button', { name: 'Se connecter', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: /Associer/ });
  await expect(dialog.getByText('ABCD-12345')).toBeVisible();
  await expect(dialog.getByRole('link')).toHaveAttribute(
    'href',
    'https://auth.openai.com/codex/device',
  );
  await expect(dialog.getByRole('button', { name: 'Copier le lien' })).toBeVisible();
  await expect(dialog.getByText('https://', { exact: false })).toHaveCount(0);
  expect(await dialog.evaluate((element) => element.scrollWidth <= element.clientWidth + 1)).toBe(
    true,
  );
  await expect(dialog.getByText(/Simuler/)).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Fermer', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  expect(cancelled).toBe(false);
});

test('settings navigation and system health use shared components in both themes', async ({
  page,
}, testInfo) => {
  await page.route('**/api/v1/cluster', (route) =>
    route.fulfill({
      json: {
        id: '00000000-0000-0000-0000-000000000001',
        name: 'Cluster de test',
        standalone: false,
      },
    }),
  );
  await page.route('**/health/details', (route) =>
    route.fulfill({
      json: {
        status: 'degraded',
        totalDurationMs: 2004,
        checks: [
          { name: 'database', status: 'healthy', durationMs: 4 },
          {
            name: 'cluster',
            status: 'degraded',
            durationMs: 2000,
            description: 'Le nœud truenas-51 ne répond plus.',
          },
        ],
      },
    }),
  );
  await page.goto('/settings?tab=system');
  const settings = page.locator('app-settings-page');
  await expect(settings.getByText('Base de données')).toBeVisible();
  await expect(settings.getByText('Le nœud truenas-51 ne répond plus.')).toBeVisible();
  await expect(settings.getByText('Opérationnel').first()).toBeVisible();
  await expect(settings.getByText('Cluster de test')).toBeVisible();
  await expect(
    settings.getByRole('region', { name: 'Cluster' }).getByRole('button', { name: 'Copier' }),
  ).toBeVisible();
  // Row backgrounds animate on theme change: axe must not sample a colour halfway between themes.
  await page.addStyleTag({ content: '*, *::before, *::after { transition: none !important; }' });
  for (const theme of ['light', 'dark']) {
    await page.evaluate(
      (value) => document.documentElement.setAttribute('data-theme', value),
      theme,
    );
    const result = await new AxeBuilder({ page })
      .include('app-settings-page')
      .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
      .analyze();
    expect(result.violations).toEqual([]);
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
    ).toBe(true);
    await page.screenshot({ path: testInfo.outputPath(`settings-${theme}.png`), fullPage: true });
  }
  await settings.getByRole('tab', { name: 'Interface', exact: true }).focus();
  await page.keyboard.press('ArrowRight');
  await expect(settings.getByRole('tab', { name: 'Accès', exact: true })).toHaveAttribute(
    'aria-selected',
    'true',
  );
});

test('system logs display the actual array API response and named SSE events without console errors', async ({
  page,
}) => {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  const entry = {
    id: 'historical',
    timestamp: '2026-10-19T00:00:00Z',
    level: 'Warning',
    category: 'Encoding.Api.Tools.DeviceCodeAuthService',
    message: 'Codex timeout: outputReceived=false',
  };
  await page.route('**/api/v1/system/logs?*', (route) => route.fulfill({ json: [entry] }));
  await page.route('**/api/v1/system/logs/categories', (route) =>
    route.fulfill({ json: { categories: [entry.category] } }),
  );
  await page.route('**/api/v1/system/logs/stream', (route) =>
    route.fulfill({
      contentType: 'text/event-stream',
      body: `event: log\ndata: ${JSON.stringify({ ...entry, id: 'live', message: 'CLI process exited' })}\n\n`,
    }),
  );
  await page.goto('/settings?tab=logs');
  const viewer = page.locator('app-system-log-viewer');
  await expect(viewer.getByText(entry.message, { exact: true })).toBeVisible();
  await expect(viewer.getByText('CLI process exited', { exact: true })).toBeVisible();
  expect(errors).toEqual([]);
});

test('Codex displays the server diagnostic instead of a generic network error', async ({
  page,
}) => {
  const title =
    'Codex a produit une sortie, mais aucune URL et aucun code compatibles n’ont été reconnus sous 30 secondes.';
  await page.route('**/api/v1/tools/ai/codex-cli/auth/device-code/start', (route) =>
    route.fulfill({ status: 409, json: { title } }),
  );
  await page.goto('/settings?tab=ai');
  await page
    .locator('app-ai-tools-tab')
    .getByRole('button', { name: 'Configurer Codex CLI', exact: true })
    .click();
  await page.getByRole('button', { name: 'Se connecter', exact: true }).click();
  await expect(page.getByText(title, { exact: true })).toBeVisible();
  await expect(page.getByRole('dialog', { name: /Associer/ })).toHaveCount(0);
});

test('Claude browser authentication accepts the return code from the redesigned drawer', async ({
  page,
}) => {
  let submitted: unknown;
  await page.route('**/api/v1/tools/ai/claude-cli/auth/device-code/start', (route) =>
    route.fulfill({
      json: {
        toolId: 'claude-cli',
        prompt: {
          userCode: null,
          verificationUri: 'https://claude.ai/oauth/authorize',
          requiresInput: true,
          expiresInSeconds: 60,
          intervalSeconds: 5,
        },
      },
    }),
  );
  await page.route('**/api/v1/tools/ai/claude-cli/auth/device-code/status', (route) =>
    route.fulfill({ json: { toolId: 'claude-cli', state: 'pending' } }),
  );
  await page.route('**/api/v1/tools/ai/claude-cli/auth/input', async (route) => {
    submitted = route.request().postDataJSON();
    await route.fulfill({ json: { accepted: true } });
  });
  await page.goto('/settings?tab=ai');
  await page.getByRole('button', { name: 'Configurer Claude Code', exact: true }).click();
  await expect(page.getByLabel('Jeton Claude Code')).toHaveCount(0);
  await page.getByRole('button', { name: 'Se connecter', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: /Associer/ });
  await expect(dialog.getByRole('link')).toHaveAttribute(
    'href',
    'https://claude.ai/oauth/authorize',
  );
  await expect(dialog.getByRole('button', { name: 'Copier le lien' })).toBeVisible();
  await expect(dialog.getByText('https://', { exact: false })).toHaveCount(0);
  expect(await dialog.evaluate((element) => element.scrollWidth <= element.clientWidth + 1)).toBe(
    true,
  );
  await dialog.getByLabel('Code de retour Claude').fill('return-code');
  await dialog.getByRole('button', { name: 'Transmettre le code à Claude Code' }).click();
  await expect.poll(() => submitted).toEqual({ input: 'return-code' });
  await expect(dialog.getByText('Code transmis. Claude Code vérifie la connexion…')).toBeVisible();
});

test('provider changes survive a background connection refresh before saving', async ({ page }) => {
  let saved: Record<string, unknown> | undefined;
  let connectionRefreshes = 0;
  await page.route('**/api/v1/tools/ai', (route) => {
    connectionRefreshes++;
    return route.fulfill({ json: [] });
  });
  await page.route('**/api/v1/tools/ai/settings', async (route) => {
    if (route.request().method() !== 'GET') {
      saved = route.request().postDataJSON();
    }
    await route.fulfill({
      json: {
        defaultProvider: 'ollama',
        ollamaBaseUrl: 'http://localhost:11434/v1/',
        ollamaModel: saved?.['ollamaModel'] ?? 'llama3.2',
        openAiBaseUrl: 'https://api.openai.com/v1/',
        openAiModel: 'gpt-4o-mini',
        hasOpenAiApiKey: true,
        timeoutSeconds: 120,
        subtitleBatchSize: 80,
      },
    });
  });
  await page.clock.install();
  await page.goto('/settings?tab=ai');
  const tab = page.locator('app-ai-tools-tab');
  await tab.getByRole('button', { name: 'Configurer Ollama', exact: true }).click();
  const editor = page.getByRole('dialog', { name: 'Ollama', exact: true });
  await editor.getByLabel('Modèle', { exact: true }).fill('my-local-model');
  const refreshesBeforeEdit = connectionRefreshes;
  await page.clock.fastForward(15_000);
  await expect.poll(() => connectionRefreshes).toBeGreaterThan(refreshesBeforeEdit);
  await expect(editor.getByLabel('Modèle', { exact: true })).toHaveValue('my-local-model');
  await editor.getByRole('button', { name: 'Enregistrer', exact: true }).click();
  await expect.poll(() => saved?.['ollamaModel']).toBe('my-local-model');
  expect(saved?.['defaultProvider']).toBe('ollama');
  await expect(editor).toHaveCount(0);
  await tab.getByRole('button', { name: 'Configurer Ollama', exact: true }).click();
  await expect(
    page.getByRole('dialog', { name: 'Ollama', exact: true }).getByLabel('Modèle', { exact: true }),
  ).toHaveValue('my-local-model');
});

test('application sessions show observed usage and exchanges on desktop and mobile', async ({
  page,
}, testInfo) => {
  const id = 'application-session';
  let sessionRequests = 0;
  await page.route('**/api/v1/tools/ai/sessions?offset=0', (route) => {
    sessionRequests++;
    return route.fulfill({
      json: {
        items: [
          {
            id,
            provider: 'claude-cli',
            status: 'completed',
            turnCount: 1,
            attempt: 1,
            updatedAt: '2026-10-19T08:00:00Z',
            scope: { operation: 'flow-create' },
            usage: { inputTokens: 150, outputTokens: 40, estimatedCostUsd: 0.012 },
          },
        ],
        offset: 0,
        hasMore: false,
      },
    });
  });
  await page.route(`**/api/v1/tools/ai/sessions/${id}`, (route) =>
    route.fulfill({
      json: {
        id,
        systemPrompt: 'Conserver les minutages.',
        turns: [
          {
            requestId: 'request',
            userPrompt: '<script>texte source</script>',
            content: 'Traduction validée',
          },
        ],
      },
    }),
  );
  await page.clock.install();
  await page.goto('/settings?tab=ai');
  await page.getByRole('button', { name: /Suivi de l’IA/ }).click();
  const history = page.locator('app-ai-session-history');
  await expect(history.getByText('Création de flow', { exact: true })).toBeVisible();
  const requestsAfterInitialLoad = sessionRequests;
  await page.clock.fastForward(30_000);
  await expect.poll(() => sessionRequests).toBeGreaterThan(requestsAfterInitialLoad);
  await expect(history.getByText('Jetons d’entrée : 150', { exact: false })).toBeVisible();
  await expect(history.getByText('Traduction validée')).toHaveCount(0);
  await history.getByRole('button', { name: 'Consulter les échanges' }).click();
  await expect(history.getByText('Traduction validée')).toBeVisible();
  await expect(history.locator('script')).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
  const accessibility = await new AxeBuilder({ page })
    .include('app-ai-session-history')
    .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
    .analyze();
  expect(accessibility.violations).toEqual([]);
  await history.screenshot({ path: testInfo.outputPath('ai-session-history.png') });
});

test('subscription quotas show windows and mark failed refreshes as old', async ({
  page,
}, testInfo) => {
  let failed = false;
  await page.route('**/api/v1/tools/ai/quotas', (route) =>
    failed
      ? route.fulfill({ status: 503, json: { title: 'Unavailable' } })
      : route.fulfill({
          json: {
            nodes: [
              {
                nodeId: 'first',
                name: 'Salon',
                providers: [
                  {
                    provider: 'codex-cli',
                    state: 'available',
                    experimental: false,
                    observedAt: new Date().toISOString(),
                    checkedAt: new Date().toISOString(),
                    windows: [
                      {
                        id: 'codex/primary',
                        name: 'Codex · 300 min',
                        usedPercent: 85,
                        remainingPercent: 15,
                        resetsAt: new Date(Date.now() + 3600_000).toISOString(),
                      },
                    ],
                  },
                  {
                    provider: 'claude-cli',
                    state: 'unavailable',
                    experimental: true,
                    observedAt: null,
                    checkedAt: new Date().toISOString(),
                    windows: [],
                    message: 'Les autorisations du profil ne fournissent pas le quota.',
                  },
                ],
              },
            ],
          },
        }),
  );
  await page.clock.install();
  await page.goto('/settings?tab=ai');
  await page.getByRole('button', { name: /Suivi de l’IA/ }).click();
  const quotas = page.locator('app-ai-quota-panel');
  await expect(quotas.getByText('15 % restant', { exact: true })).toBeVisible();
  await expect(quotas.getByText('Quota bientôt atteint')).toBeVisible();
  await expect(quotas.getByText('Indisponible', { exact: true })).toBeVisible();
  await expect(quotas.getByText('Lecture expérimentale', { exact: false })).toBeVisible();
  for (const theme of ['light', 'dark']) {
    await page.evaluate(
      (value) => document.documentElement.setAttribute('data-theme', value),
      theme,
    );
    expect(
      (
        await new AxeBuilder({ page })
          .include('app-ai-quota-panel')
          .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
          .analyze()
      ).violations,
    ).toEqual([]);
    await quotas.screenshot({ path: testInfo.outputPath(`ai-quotas-${theme}.png`) });
  }
  failed = true;
  await page.clock.fastForward(60_000);
  await expect(
    quotas.getByText('15 % restant lors de la dernière observation', { exact: true }),
  ).toBeVisible();
  await expect(quotas.locator('mat-progress-bar')).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
});
