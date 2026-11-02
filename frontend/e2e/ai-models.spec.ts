import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Locator } from '@playwright/test';

const nodeId = '00000000-0000-0000-0000-000000000002';
const conversationId = '00000000000000000000000000000002.00000000000000000000000000000003';
const source = { kind: 'node', resourceId: nodeId, label: 'Nœud Salon', href: '/nodes' };
const conversation = {
  id: conversationId,
  provider: 'codex-cli',
  model: 'server-model-a',
  reasoningEffort: 'high',
  nodeId,
  title: 'Inspecter le nœud',
  source,
  createdAt: '2030-01-01T00:00:00Z',
  updatedAt: '2030-01-01T00:00:00Z',
};
const models = [
  {
    provider: 'codex-cli',
    model: 'server-model-a',
    label: 'Modèle fourni A',
    reasoningEfforts: ['low', 'high'],
    defaultReasoningEffort: 'high',
    reasoningKnown: true,
    nodeId,
    nodeName: 'Salon',
  },
  {
    provider: 'claude-cli',
    model: 'server-model-b',
    label: 'Modèle fourni B',
    reasoningEfforts: ['medium', 'max'],
    defaultReasoningEffort: 'medium',
    reasoningKnown: true,
    nodeId,
    nodeName: 'Salon',
  },
];

async function openModelSettings(container: Locator): Promise<void> {
  await container.getByRole('button', { name: /^Modèle/ }).click();
}

test.beforeEach(async ({ page }) => {
  // Catch-all routes must be registered first: Playwright evaluates routes in reverse order.
  await page.route('**/api/**', (route) => route.fulfill({ json: [] }));
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({
      json: { setupRequired: false, user: { id: nodeId, username: 'admin', role: 'admin' } },
    }),
  );
  await page.route('**/health/details', (route) =>
    route.fulfill({ json: { status: 'healthy', checks: [] } }),
  );
  await page.route('**/api/v1/executions*', (route) => route.fulfill({ json: { executions: [] } }));
  await page.route('**/api/v1/tools/ai/assistant/options*', (route) =>
    route.fulfill({
      json: { defaultProvider: 'codex-cli', defaultModel: 'server-model-a', models },
    }),
  );
  await page.route('**/api/v1/tools/ai/assistant/conversations', (route) =>
    route.fulfill({ json: [conversation] }),
  );
  await page.route(`**/api/v1/tools/ai/assistant/conversations/${conversationId}`, (route) =>
    route.fulfill({
      json: {
        conversation,
        turns: [
          {
            requestId: 'old',
            userPrompt: 'Question initiale',
            content: 'Réponse initiale',
            selection: {
              provider: 'codex-cli',
              model: 'server-model-a',
              reasoningEffort: 'high',
              nodeId,
            },
            actualModel: 'confirmed-model-a',
          },
        ],
      },
    }),
  );
});

test('the global search starts conversations and the history drawer works without horizontal overflow', async ({
  page,
}) => {
  await page.goto(`/ai/assistant?conversation=${conversationId}`);
  const assistant = page.locator('app-ai-assistant-page').first();
  const history = assistant.locator('#assistant-conversation-history');
  const newConversation = page
    .locator('app-global-search-bar')
    .getByRole('button', { name: 'Nouvelle conversation' });
  const isCompact = await page.evaluate(() => matchMedia('(max-width: 760px)').matches);

  await expect(newConversation).toBeVisible();
  await expect(history.getByRole('button', { name: 'Nouvelle conversation' })).toHaveCount(0);
  await expect(assistant.getByRole('heading', { name: conversation.title })).toBeVisible();

  if (isCompact) {
    await expect(history).toBeHidden();
    await assistant.getByRole('button', { name: 'Afficher les conversations' }).click();
    await expect(history).toBeVisible();
    await expect(history.getByRole('textbox')).toHaveCount(0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
    await newConversation.click();
    await expect(assistant.getByRole('heading', { name: 'Nouvelle conversation' })).toBeVisible();
    return;
  }

  await expect(history).toBeVisible();
  await expect(history.getByRole('textbox')).toHaveCount(0);
  await assistant.getByRole('button', { name: 'Masquer les conversations' }).click();
  await expect(history).toBeHidden();
  await assistant.getByRole('button', { name: 'Afficher les conversations' }).click();
  await expect(history).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();

  await page.setViewportSize({ width: 390, height: 844 });
  await expect(history).toBeHidden();
  await assistant.getByRole('button', { name: 'Afficher les conversations' }).click();
  await expect(history).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await newConversation.click();
  await expect(assistant.getByRole('heading', { name: 'Nouvelle conversation' })).toBeVisible();
});

test('backend models and reasoning drive each turn while old turns retain observed models', async ({
  page,
}, testInfo) => {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  let release: (() => void) | undefined;
  let received: Record<string, unknown> | undefined;
  await page.route('**/api/v1/tools/ai/assistant/messages', async (route) => {
    const request = route.request().postDataJSON();
    received = request;
    await new Promise<void>((resolve) => {
      release = resolve;
    });
    await route.fulfill({
      json: {
        success: true,
        conversation: {
          ...conversation,
          provider: request.provider,
          model: request.model,
          reasoningEffort: request.reasoningEffort,
        },
        requestId: request.requestId,
        content: 'Nouvelle réponse',
        sources: [source],
        selection: {
          provider: request.provider,
          model: request.model,
          reasoningEffort: request.reasoningEffort,
          nodeId,
        },
        actualModel: 'confirmed-model-b',
      },
    });
  });
  await page.goto(`/ai/assistant?conversation=${conversationId}`);
  const assistant = page.locator('app-ai-assistant-page').first();
  await openModelSettings(assistant);
  const modelButton = assistant.getByRole('button', { name: /^Modèle/ });
  const model = assistant.getByRole('combobox', { name: 'Modèle', exact: true });
  await expect(model).toContainText('Modèle fourni A');
  await assistant.getByRole('combobox', { name: 'Fournisseur IA', exact: true }).click();
  await expect(page.getByRole('option', { name: 'Ollama', exact: true })).toHaveCount(0);
  await page.getByRole('option', { name: 'Claude', exact: true }).click();
  await model.click();
  await expect(page.getByRole('option', { name: /Modèle fourni A/ })).toHaveCount(0);
  await page.getByRole('option', { name: /Modèle fourni B/ }).click();
  const reasoning = assistant.getByRole('slider', { name: 'Effort de raisonnement' });
  await expect(assistant.locator('.effort-value')).toHaveText('Moyen');
  await expect(reasoning).toHaveAttribute('aria-valuetext', 'Moyen');
  await reasoning.press('End');
  await expect(assistant.locator('.effort-value')).toHaveText('Maximum');
  await expect(reasoning).toHaveAttribute('aria-valuetext', 'Maximum');
  await assistant.getByRole('textbox', { name: 'Votre question' }).fill('Analyser ce nœud');
  await assistant.getByRole('button', { name: 'Envoyer', exact: true }).click();
  await expect.poll(() => received).toBeTruthy();
  expect(received).toMatchObject({
    provider: 'claude-cli',
    model: 'server-model-b',
    reasoningEffort: 'max',
    nodeId,
  });
  await expect(modelButton).toBeDisabled();
  release!();
  await expect(assistant.getByText('Nouvelle réponse', { exact: true })).toBeVisible();
  for (const details of await assistant.locator('details.response-details').all())
    await details.locator('summary').click();
  await expect(
    assistant.getByText('Modèle utilisé : confirmed-model-a', { exact: true }),
  ).toBeVisible();
  await expect(
    assistant.getByText('Modèle utilisé : confirmed-model-b', { exact: true }),
  ).toBeVisible();
  await expect(modelButton).toBeEnabled();
  for (const details of await assistant.locator('details.response-details').all())
    await details.locator('summary').click();
  const result = await new AxeBuilder({ page })
    .include('app-ai-assistant-page')
    .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
    .analyze();
  expect(
    result.violations.map((violation) => ({
      id: violation.id,
      nodes: violation.nodes.map((node) => ({ target: node.target, summary: node.failureSummary })),
    })),
  ).toEqual([]);
  await modelButton.scrollIntoViewIfNeeded();
  await page.screenshot({ path: testInfo.outputPath('assistant-models.png'), fullPage: true });
  expect(
    await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
  ).toBeTruthy();
  expect(errors).toEqual([]);
});

test('unavailable catalog blocks a new turn while preserving the selected conversation', async ({
  page,
}) => {
  await page.route('**/api/v1/tools/ai/assistant/options*', (route) =>
    route.fulfill({
      json: {
        defaultProvider: 'codex-cli',
        models: [],
        catalogs: [
          {
            nodeId,
            name: 'Salon',
            providers: [
              {
                provider: 'codex-cli',
                state: 'unavailable',
                models: [],
                observedAt: null,
                checkedAt: '2030-01-01T00:00:00Z',
                message: 'Catalogue indisponible',
              },
            ],
          },
        ],
      },
    }),
  );
  await page.goto(`/ai/assistant?conversation=${conversationId}`);
  const assistant = page.locator('app-ai-assistant-page').first();
  await assistant.getByRole('textbox', { name: 'Votre question' }).fill('Question');
  await expect(assistant.getByRole('button', { name: 'Envoyer', exact: true })).toBeDisabled();
  await expect(assistant.getByText(/Salon · codex-cli : Catalogue indisponible/)).toBeVisible();
  await openModelSettings(assistant);
  await expect(assistant.getByRole('combobox', { name: 'Modèle', exact: true })).toBeDisabled();
  await expect(assistant.getByRole('button', { name: 'Envoyer', exact: true })).toBeDisabled();
  await expect(page).toHaveURL(new RegExp(`/ai/assistant\\?conversation=${conversationId}`));
});

test('administrators disable discovered models through the backend policy', async ({ page }) => {
  let disabled = false;
  let saved: unknown;
  await page.route('**/api/v1/tools/ai/settings', (route) =>
    route.fulfill({
      json: {
        defaultProvider: 'codex-cli',
        ollamaBaseUrl: '',
        ollamaModel: '',
        openAiBaseUrl: '',
        openAiModel: '',
        timeoutSeconds: 120,
        subtitleBatchSize: 80,
        assistantConversationRetentionDays: 90,
        assistantActivityRetentionDays: 30,
      },
    }),
  );
  await page.route('**/api/v1/tools/ai/cluster', (route) => route.fulfill({ json: { nodes: [] } }));
  await page.route('**/api/v1/tools/ai/quotas', (route) => route.fulfill({ json: { nodes: [] } }));
  await page.route('**/api/v1/tools/ai/sessions?*', (route) =>
    route.fulfill({ json: { items: [], offset: 0, hasMore: false } }),
  );
  await page.route('**/api/v1/tools/ai/models*', (route) =>
    route.fulfill({
      json: {
        nodes: [
          {
            nodeId,
            name: 'Salon',
            providers: [
              {
                provider: 'codex-cli',
                state: 'available',
                models: [
                  {
                    id: 'server-model-a',
                    label: 'Modèle fourni A',
                    reasoningEfforts: ['low', 'high'],
                    reasoningKnown: true,
                  },
                ],
                observedAt: '2030-01-01T00:00:00Z',
                checkedAt: '2030-01-01T00:00:00Z',
              },
            ],
          },
        ],
        policy: {
          disabledModels: disabled ? [{ provider: 'codex-cli', model: 'server-model-a' }] : [],
        },
      },
    }),
  );
  await page.route('**/api/v1/tools/ai/models/policy', async (route) => {
    saved = route.request().postDataJSON();
    disabled = !(saved as { enabled: boolean }).enabled;
    await route.fulfill({
      json: {
        disabledModels: disabled ? [{ provider: 'codex-cli', model: 'server-model-a' }] : [],
      },
    });
  });
  await page.goto('/settings?tab=ai');
  await page.getByRole('button', { name: /Modèles disponibles/ }).click();
  const panel = page.locator('app-ai-model-catalog');
  const toggle = panel.getByRole('switch', { name: 'Modèle fourni A' });
  await expect(toggle).toBeChecked();
  await toggle.click();
  await expect(toggle).not.toBeChecked();
  expect(saved).toEqual({ provider: 'codex-cli', model: 'server-model-a', enabled: false });
  await page.reload();
  await page.getByRole('button', { name: /Modèles disponibles/ }).click();
  const reloadedPanel = page.locator('app-ai-model-catalog');
  const reloadedToggle = reloadedPanel.getByRole('switch', { name: 'Modèle fourni A' });
  await expect(reloadedToggle).not.toBeChecked();
  await reloadedToggle.click();
  await expect(reloadedToggle).toBeChecked();
  const result = await new AxeBuilder({ page })
    .include('app-ai-model-catalog')
    .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
    .analyze();
  expect(
    result.violations.map((violation) => ({
      id: violation.id,
      nodes: violation.nodes.map((node) => ({ target: node.target, summary: node.failureSummary })),
    })),
  ).toEqual([]);
});

test('network retries keep the selected model and request id while validation errors unlock selection', async ({
  page,
}) => {
  const requests: Record<string, unknown>[] = [];
  await page.route('**/api/v1/tools/ai/assistant/messages', async (route) => {
    requests.push(route.request().postDataJSON());
    await route.fulfill({
      status: requests.length === 1 ? 503 : 400,
      json: { title: requests.length === 1 ? 'Résultat incertain' : 'Ce modèle est désactivé' },
    });
  });
  await page.goto(`/ai/assistant?conversation=${conversationId}`);
  const assistant = page.locator('app-ai-assistant-page').first();
  await openModelSettings(assistant);
  const model = assistant.getByRole('combobox', { name: 'Modèle', exact: true });
  await assistant.getByRole('combobox', { name: 'Fournisseur IA', exact: true }).click();
  await page.getByRole('option', { name: 'Claude', exact: true }).click();
  await model.click();
  await page.getByRole('option', { name: /Modèle fourni B/ }).click();
  await assistant.getByRole('textbox', { name: 'Votre question' }).fill('Question à reprendre');
  await assistant.getByRole('button', { name: 'Envoyer', exact: true }).click();
  await expect(
    assistant.getByRole('button', { name: 'Reprendre la demande', exact: true }),
  ).toBeVisible();
  await expect(assistant.getByRole('button', { name: /^Modèle/ })).toBeDisabled();
  await assistant.getByRole('button', { name: 'Reprendre la demande', exact: true }).click();
  await expect(assistant.getByRole('button', { name: /^Modèle/ })).toBeEnabled();
  expect(requests).toHaveLength(2);
  expect(requests[1]).toEqual(requests[0]);
  expect(requests[1]).toMatchObject({
    provider: 'claude-cli',
    model: 'server-model-b',
    reasoningEffort: 'medium',
    nodeId,
  });
  await expect(assistant.getByRole('textbox', { name: 'Votre question' })).toBeEnabled();
});

test('floating launcher follows scrolling and compact chat preserves conversation without changing the page', async ({
  page,
}, testInfo) => {
  await page.route('**/api/v1/tools/ai/assistant/activity', (route) =>
    route.fulfill({
      json: Array.from({ length: 60 }, (_, index) => ({
        requestId: `request-${index}`,
        provider: 'codex-cli',
        status: 'succeeded',
        startedAt: '2030-01-01T00:00:00Z',
        updatedAt: '2030-01-01T00:00:00Z',
        durationMilliseconds: 300,
        errorCategory: null,
        conversationId: null,
      })),
    }),
  );
  await page.route('**/api/v1/nodes', (route) =>
    route.fulfill({
      json: [
        {
          id: nodeId,
          name: 'Salon',
          roles: ['worker'],
          maxConcurrentTasks: 1,
          capabilities: [],
          status: 'online',
          lastSeenAt: '2030-01-01T00:00:00Z',
        },
      ],
    }),
  );
  await page.route('**/api/v1/tools/ai/assistant/messages', (route) => {
    const request = route.request().postDataJSON();
    expect(request).toMatchObject({
      resourceType: 'node',
      resourceId: nodeId,
      provider: 'claude-cli',
      model: 'server-model-b',
      reasoningEffort: 'medium',
    });
    return route.fulfill({
      json: {
        success: true,
        conversation: {
          ...conversation,
          provider: request.provider,
          model: request.model,
          reasoningEffort: request.reasoningEffort,
        },
        requestId: request.requestId,
        content: 'Réponse du widget',
        sources: [source],
        selection: {
          provider: request.provider,
          model: request.model,
          reasoningEffort: request.reasoningEffort,
          nodeId,
        },
      },
    });
  });
  await page.goto('/activity?type=ai');
  const launcher = page.getByRole('button', { name: 'Ouvrir l’assistant IA', exact: true });
  const mobile = testInfo.project.name === 'mobile';
  let after: { x: number; y: number; width: number; height: number } | null = null;
  if (mobile) {
    await page.getByRole('button', { name: 'Plus', exact: true }).click();
    await page.getByRole('button', { name: /Assistant rapide/ }).click();
  } else {
    await expect(launcher).toBeVisible();
    const before = (await launcher.boundingBox())!;
    expect(before.width).toBeLessThanOrEqual(60);
    const scroller = page.locator('.page-transition-region');
    await scroller.evaluate((element) => {
      const spacer = document.createElement('div');
      spacer.dataset.testid = 'assistant-scroll-spacer';
      spacer.style.flex = '0 0 1600px';
      spacer.style.pointerEvents = 'none';
      element.append(spacer);
      element.scrollTop = element.scrollHeight;
    });
    await expect.poll(() => scroller.evaluate((element) => element.scrollTop)).toBeGreaterThan(600);
    after = (await launcher.boundingBox())!;
    expect(Math.abs(before.y - after.y)).toBeLessThan(1);
    expect(after.y + after.height).toBeLessThanOrEqual(page.viewportSize()!.height);
    await launcher.click();
  }
  const widget = page.locator('#assistant-widget-panel');
  await expect(widget).toBeVisible();
  await expect
    .poll(() => widget.evaluate((panel) => panel.getBoundingClientRect().width))
    .toBeGreaterThan(300);
  await expect
    .poll(
      () =>
        widget.evaluate(
          (panel) => getComputedStyle(panel).transform === 'matrix(1, 0, 0, 1, 0, 0)',
        ),
      { timeout: 2_000 },
    )
    .toBe(true);
  const panelBounds = (await widget.boundingBox())!;
  expect(panelBounds.height).toBeLessThanOrEqual(520);
  if (after) {
    expect(Math.abs(panelBounds.x + panelBounds.width - (after.x + after.width))).toBeLessThan(1);
    expect(Math.abs(panelBounds.y + panelBounds.height + 16 - after.y)).toBeLessThan(1);
  }
  await openModelSettings(widget);
  await widget.getByRole('combobox', { name: 'Fournisseur IA', exact: true }).click();
  await page.getByRole('option', { name: 'Claude', exact: true }).click();
  await widget.getByRole('button', { name: 'Joindre un fichier ou une ressource' }).click();
  await page.getByRole('menuitem', { name: 'Un nœud' }).click();
  await widget.getByRole('combobox', { name: 'Ressource', exact: true }).click();
  await page.getByRole('option', { name: 'Salon', exact: true }).click();
  await widget.getByRole('button', { name: 'Joindre', exact: true }).click();
  await widget.getByRole('button', { name: 'Fermer l’espace de travail' }).click();
  await widget.getByRole('textbox', { name: 'Votre question' }).fill('Analyser ce nœud');
  await widget.getByRole('textbox', { name: 'Votre question' }).press('Enter');
  await expect(widget.getByText('Réponse du widget', { exact: true })).toBeVisible();
  await expect(page).toHaveURL(/\/activity\?type=ai$/);
  await widget.getByRole('textbox', { name: 'Votre question' }).fill('Brouillon conservé');
  await page.getByRole('button', { name: 'Fermer le panneau de l’assistant' }).click();
  if (mobile) {
    await page.getByRole('button', { name: 'Plus', exact: true }).click();
    await page.getByRole('button', { name: /Assistant rapide/ }).click();
  } else {
    await expect(launcher).toBeFocused();
    await launcher.click();
  }
  await expect(widget.getByRole('textbox', { name: 'Votre question' })).toHaveValue(
    'Brouillon conservé',
  );
  await expect(widget.getByText('Réponse du widget', { exact: true })).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath('floating-chat.png'), fullPage: true });
  for (const theme of ['light', 'dark']) {
    await page.evaluate(
      (value) => document.documentElement.setAttribute('data-theme', value),
      theme,
    );
    const result = await new AxeBuilder({ page })
      .include('#assistant-widget-panel')
      .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
      .analyze();
    expect(
      result.violations.map((violation) => ({
        id: violation.id,
        nodes: violation.nodes.map((node) => ({
          target: node.target,
          summary: node.failureSummary,
        })),
      })),
    ).toEqual([]);
  }
  await page.getByRole('button', { name: 'Fermer le panneau de l’assistant' }).click();
  if (mobile) return;
  await launcher.focus();
  await page.keyboard.press('Alt+ArrowUp');
  expect((await launcher.boundingBox())!.y).toBeLessThan(after!.y);
  const moved = (await launcher.boundingBox())!;
  await page.reload();
  await expect(launcher).toBeVisible();
  await expect
    .poll(async () => Math.abs((await launcher.boundingBox())!.y - moved.y))
    .toBeLessThan(1);
});

test('compact resource selector keeps its empty label clear of the pointer target at 320px', async ({
  page,
}) => {
  await page.setViewportSize({ width: 320, height: 800 });
  await page.route('**/api/v1/nodes', (route) =>
    route.fulfill({
      json: [
        {
          id: nodeId,
          name: 'Salon',
          roles: ['worker'],
          maxConcurrentTasks: 1,
          capabilities: [],
          status: 'online',
          lastSeenAt: '2030-01-01T00:00:00Z',
        },
      ],
    }),
  );
  await page.goto('/activity?type=ai');
  await page.getByRole('button', { name: 'Plus', exact: true }).click();
  await page.getByRole('button', { name: /Assistant rapide/ }).click();
  const widget = page.locator('#assistant-widget-panel');
  await widget.getByRole('button', { name: 'Joindre un fichier ou une ressource' }).click();
  await page.getByRole('menuitem', { name: 'Un nœud' }).click();
  const selector = widget.getByRole('combobox', { name: 'Ressource', exact: true });
  await expect
    .poll(() =>
      selector.evaluate((element) => {
        const bounds = element.getBoundingClientRect();
        const target = document.elementFromPoint(
          bounds.x + bounds.width / 2,
          bounds.y + bounds.height / 2,
        );
        return target !== null && element.contains(target);
      }),
    )
    .toBe(true);
  await selector.click();
  await page.getByRole('option', { name: 'Salon', exact: true }).click();
  await widget.getByRole('button', { name: 'Joindre', exact: true }).click();
  await expect(widget.locator('.selected-resource')).toContainText('Salon');
});
