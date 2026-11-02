import { readFileSync } from 'node:fs';
import { expect, test, type Locator, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
const output = (id: string) => ({
  id,
  label: id === 'success' ? 'Suite' : id === 'yes' ? 'Oui' : 'Non',
  description: 'Chemin ' + id,
});
const component = (id: string, kind: string, outputs: string[]) => ({
  id,
  version: '1.0.0',
  name: id,
  description: 'Description ' + id,
  kind,
  mediaType: 'general',
  outputs: outputs.map(output),
  configurationSchema: { version: 1, fields: [] },
});
const components = [
  component('base-input-file', 'input', ['success']),
  component('decision', 'condition', ['yes', 'no']),
  component('action', 'action', ['success']),
  component('finish', 'terminal', []),
];
const reference = (id: string) => ({
  pluginId: 'base',
  pluginVersion: '1.0.0',
  componentId: id,
  componentVersion: '1.0.0',
  configurationSchemaVersion: 1,
});
const node = (id: string, componentId: string) => ({
  id,
  name: id,
  component: reference(componentId),
  configuration: {},
});
const edge = (id: string, s: string, o: string, t: string) => ({
  id,
  sourceNodeId: s,
  sourceOutputId: o,
  targetNodeId: t,
});
const flow = {
  formatVersion: 1,
  id: '11111111-1111-4111-8111-111111111111',
  version: 1,
  name: 'Flow de test',
  mediaType: 'video',
  nodes: [
    node('Entrée', 'base-input-file'),
    node('Décision', 'decision'),
    node('Action A', 'action'),
    node('Action B', 'action'),
    node('Fin', 'finish'),
  ],
  connections: [
    edge('e1', 'Entrée', 'success', 'Décision'),
    edge('e2', 'Décision', 'yes', 'Action A'),
    edge('e3', 'Décision', 'no', 'Action B'),
    edge('e4', 'Action A', 'success', 'Fin'),
    edge('e5', 'Action B', 'success', 'Fin'),
  ],
};
async function setup(page: Page) {
  await page.route('**/api/**', (r) => r.fulfill({ json: [] }));
  await page.route('**/api/v1/tools/ai/assistant/options*', (r) =>
    r.fulfill({ json: { defaultProvider: 'none', models: [] } }),
  );
  await page.route('**/api/v1/auth/status', (r) =>
    r.fulfill({
      json: { setupRequired: false, user: { id: 'admin', username: 'admin', role: 'admin' } },
    }),
  );
  await page.route('**/api/v1/flows/components', (r) =>
    r.fulfill({ json: [{ id: 'base', version: '1.0.0', name: 'Base', components }] }),
  );
  await page.route('**/api/v1/flows/11111111-1111-4111-8111-111111111111/edit', (r) =>
    r.fulfill({ json: { definition: flow, publishedVersion: 1, backupStatus: 'ready' } }),
  );
  await page.goto('/flows/11111111-1111-4111-8111-111111111111/edit');
  await expect(page.locator('.canvas-node')).toHaveCount(5);
}
test('canvas supports addition, deletion, undo and selections', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await setup(page);
  await expect(page.locator('.canvas-node')).toHaveCount(5);
  await page.getByRole('button', { name: 'Actions de Action A', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Dupliquer', exact: true }).click();
  await expect(page.locator('.canvas-node')).toHaveCount(6);
  await page.getByRole('button', { name: 'Actions de Action A — Copie', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Supprimer', exact: true }).click();
  await expect(page.locator('.canvas-node')).toHaveCount(5);
  await page.getByRole('button', { name: 'Annuler la dernière modification', exact: true }).click();
  await expect(page.locator('.canvas-node')).toHaveCount(6);
  await page.getByRole('button', { name: 'Rétablir la modification', exact: true }).click();
  await expect(page.locator('.canvas-node')).toHaveCount(5);
  expect(errors).toEqual([]);
});
test('canvas connections can be removed, dragged and observed', async ({ page }) => {
  await setup(page);
  const link = page.getByRole('button', { name: 'Liaison success vers Fin', exact: true }).first();
  await link.focus();
  await link.press('Enter');
  await page.getByRole('menuitem', { name: 'Supprimer la liaison', exact: true }).click();
  const source = page.locator('.canvas-node[data-node-id="Action A"] .output');
  const target = page.locator('.canvas-node[data-node-id="Fin"] .input');
  await source.scrollIntoViewIfNeeded();
  const a = await source.boundingBox();
  await source.dispatchEvent('pointerdown', {
    pointerId: 1,
    pointerType: 'mouse',
    button: 0,
    clientX: a!.x + 14,
    clientY: a!.y + 14,
  });
  await expect(
    page.getByRole('button', { name: 'Naviguer dans la vue d’ensemble' }),
  ).toBeDisabled();
  await target.scrollIntoViewIfNeeded();
  const b = await target.boundingBox();
  await page.mouse.move(b!.x + 14, b!.y + 14);
  await page.mouse.down();
  await page.mouse.up();
  await expect(
    page.getByRole('button', { name: 'Liaison success vers Fin', exact: true }),
  ).toHaveCount(2);
  await expect(page.getByRole('button', { name: 'Naviguer dans la vue d’ensemble' })).toBeEnabled();
  await expect(page.locator('.canvas-node')).toHaveCount(5);
});
test('AI uses the widget and only applies a reviewed proposal', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await setup(page);
  await page.route('**/api/v1/flows/ai/refactor', (r) =>
    r.fulfill({
      json: {
        success: true,
        requestId: 'proposal',
        flow: { ...flow, nodes: [...flow.nodes, node('Proposition', 'action')] },
      },
    }),
  );
  await page.getByRole('button', { name: 'Ouvrir l’assistant IA', exact: true }).click();
  const panel = page.getByLabel('Panneau de l’assistant IA', { exact: true });
  await expect(panel).toBeVisible();
  await expect(panel).toContainText('Flow complet : Flow de test');
  await panel.locator('textarea').fill('Ajouter une action');
  await panel.getByRole('button', { name: 'Préparer les changements', exact: true }).click();
  await expect(panel.getByRole('button', { name: 'Appliquer au flow', exact: true })).toBeVisible();
  await expect(page.locator('.canvas-node')).toHaveCount(5);
  await panel.getByRole('button', { name: 'Appliquer au flow', exact: true }).click();
  await expect(page.locator('.canvas-node')).toHaveCount(6);
  expect(errors).toEqual([]);
});
test('canvas keeps a wide graph inside its horizontally scrollable workspace', async ({ page }) => {
  await setup(page);
  const dimensions = await page.locator('.workbench-canvas').evaluate((el) => ({
    body: document.documentElement.scrollWidth,
    viewport: innerWidth,
    client: el.clientWidth,
    scroll: el.scrollWidth,
  }));
  expect(dimensions.body).toBeLessThanOrEqual(dimensions.viewport);
  expect(dimensions.scroll).toBeGreaterThanOrEqual(dimensions.client);
});

test('five nested decisions render all canvas nodes and connections', async ({
  page,
}, testInfo) => {
  await setup(page);
  const deepNodes = [
    node('Entrée', 'base-input-file'),
    ...Array.from({ length: 5 }, (_, i) => [
      node('Décision ' + (i + 1), 'decision'),
      node('Alternative ' + (i + 1), 'action'),
    ]).flat(),
    node('Principale', 'action'),
    node('Fin', 'finish'),
  ];
  const deepEdges = [
    edge('deep-entry', 'Entrée', 'success', 'Décision 1'),
    ...Array.from({ length: 5 }, (_, i) => [
      edge('yes' + i, 'Décision ' + (i + 1), 'yes', i === 4 ? 'Principale' : 'Décision ' + (i + 2)),
      edge('no' + i, 'Décision ' + (i + 1), 'no', 'Alternative ' + (i + 1)),
      edge('join' + i, 'Alternative ' + (i + 1), 'success', 'Fin'),
    ]).flat(),
    edge('main-join', 'Principale', 'success', 'Fin'),
  ];
  await page.route('**/api/v1/flows/11111111-1111-4111-8111-111111111111/edit', (r) =>
    r.fulfill({
      json: {
        definition: { ...flow, nodes: deepNodes, connections: deepEdges },
        publishedVersion: 1,
        backupStatus: 'ready',
      },
    }),
  );
  await page.reload();
  await expect(page.locator('.canvas-node')).toHaveCount(13);
  await expect(page.locator('.canvas-node[data-node-id="Fin"]')).toHaveCount(1);
  await expect(page.locator('.wire')).toHaveCount(17);
  const sizes = await page.locator('.workbench-canvas').evaluate((el) => ({
    client: el.clientWidth,
    scroll: el.scrollWidth,
    body: document.documentElement.scrollWidth,
    window: innerWidth,
  }));
  expect(sizes.scroll).toBeGreaterThanOrEqual(sizes.client);
  expect(sizes.body).toBeLessThanOrEqual(sizes.window);
  await page.getByRole('button', { name: 'Tout afficher', exact: true }).click();
  await page.screenshot({ path: testInfo.outputPath('canvas-five-levels.png') });
});

test('stale AI proposals cannot overwrite changes made while reviewing', async ({ page }) => {
  await setup(page);
  await page.route('**/api/v1/flows/ai/refactor', (r) =>
    r.fulfill({
      json: {
        success: true,
        requestId: 'stale',
        flow: { ...flow, nodes: [...flow.nodes, node('Proposition obsolète', 'action')] },
      },
    }),
  );
  await page.getByRole('button', { name: 'Ouvrir l’assistant IA', exact: true }).click();
  const panel = page.getByLabel('Panneau de l’assistant IA', { exact: true });
  await panel.locator('textarea').fill('Modifier');
  await panel.getByRole('button', { name: 'Préparer les changements', exact: true }).click();
  await expect(panel.getByRole('button', { name: 'Appliquer au flow', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Fermer le panneau de l’assistant', exact: true }).click();
  await page.getByRole('button', { name: 'Actions de Action A', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Dupliquer', exact: true }).click();
  await page.getByRole('button', { name: 'Ouvrir l’assistant IA', exact: true }).click();
  await panel.getByRole('button', { name: 'Appliquer au flow', exact: true }).click();
  await expect(page.locator('.canvas-node[data-node-id="Proposition obsolète"]')).toHaveCount(0);
  await expect(page.locator('.canvas-title').filter({ hasText: 'Action A — Copie' })).toHaveCount(
    1,
  );
  await expect(page.getByText(/Le flow a changé depuis la proposition/)).toBeVisible();
});

test('right-click and stationary touch hold expose the same step actions', async ({ page }) => {
  await setup(page);
  const card = page.locator('.canvas-node[data-node-id="Action A"]');
  await card.scrollIntoViewIfNeeded();
  await card.click({ button: 'right' });
  await expect(page.getByRole('menuitem', { name: 'Dupliquer', exact: true })).toBeVisible();
  await page.keyboard.press('Escape');
  const rect = await card.boundingBox();
  await card.dispatchEvent('pointerdown', {
    pointerType: 'touch',
    pointerId: 3,
    button: 0,
    clientX: rect!.x + 50,
    clientY: rect!.y + 65,
  });
  await expect(page.getByRole('menuitem', { name: 'Dupliquer', exact: true })).toBeVisible();
  await card.dispatchEvent('pointerup', { pointerType: 'touch', pointerId: 3 });
  await page.keyboard.press('Escape');
});

test('the general assistant retains its draft when the shared widget is closed and reopened', async ({
  page,
}, testInfo) => {
  await setup(page);
  await page.goto('/');
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  const mobile = testInfo.project.name === 'mobile';
  const openAssistant = async () => {
    if (mobile) {
      await page.getByRole('button', { name: 'Plus', exact: true }).click();
      await page.getByRole('button', { name: /Assistant rapide/ }).click();
    } else {
      await page.getByRole('button', { name: 'Ouvrir l’assistant IA', exact: true }).click();
    }
  };
  await openAssistant();
  const panel = page.getByLabel('Panneau de l’assistant IA', { exact: true });
  await expect(panel.locator('app-ai-assistant-page')).toBeVisible();
  const composer = panel.locator('textarea').first();
  await composer.fill('Mon brouillon conservé');
  await page.getByRole('button', { name: 'Fermer le panneau de l’assistant', exact: true }).click();
  await openAssistant();
  await expect(composer).toHaveValue('Mon brouillon conservé');
  expect(errors).toEqual([]);
});

test('canvas placement stays fixed when a connection changes', async ({ page }) => {
  await setup(page);
  const positions = () =>
    page
      .locator('.canvas-node')
      .evaluateAll((es) =>
        Object.fromEntries(
          es.map((e) => [
            (e as HTMLElement).dataset['nodeId'],
            (e as HTMLElement).style.left + ',' + (e as HTMLElement).style.top,
          ]),
        ),
      );
  await expect(page.locator('.canvas-node')).toHaveCount(5);
  const before = await positions();
  await page.getByRole('button', { name: 'Liaison success vers Décision', exact: true }).focus();
  await page.getByRole('button', { name: 'Liaison success vers Décision', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Supprimer la liaison', exact: true }).click();
  await expect.poll(positions).toEqual(before);
  await expect.poll(positions).toEqual(before);
  const source = page.locator('.canvas-node[data-node-id="Entrée"] .output');
  await source.click();
  await page.locator('.canvas-node[data-node-id="Décision"] .input').click();
  await expect(
    page.getByRole('button', { name: 'Liaison success vers Décision', exact: true }),
  ).toHaveCount(1);
  await expect.poll(positions).toEqual(before);
});

test('canvas exposes accessible controls and connection actions', async ({ page }) => {
  await setup(page);
  const violations = (
    await new AxeBuilder({ page })
      .include('app-flow-canvas')
      .withTags(['wcag2a', 'wcag2aa'])
      .analyze()
  ).violations;
  expect(violations).toEqual([]);
});

test('editor opens canvas navigation and tools on demand', async ({ page }) => {
  await setup(page);
  const header = page.getByRole('toolbar', { name: 'Commandes du flow', exact: true });
  await expect(header.getByRole('button', { name: 'Guidé', exact: true })).toHaveCount(0);
  await expect(
    header.getByRole('button', { name: "Ouvrir l'assistant IA", exact: true }),
  ).toHaveCount(0);
  await expect(page.locator('.simulation-tray')).toHaveCount(0);
  await expect(page.getByRole('toolbar', { name: 'Navigation du canvas' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Tout afficher', exact: true })).toBeVisible();
  await header.getByRole('button', { name: 'Autres actions du flow', exact: true }).click();
  await page
    .getByRole('menuitem', { name: 'Basculer le volet de simulation', exact: true })
    .click();
  await expect(
    page.getByRole('button', { name: 'Vérification du flow', exact: true }),
  ).toBeVisible();
  await page.getByRole('button', { name: 'Réduire', exact: true }).click();
  await expect(page.locator('.simulation-tray')).toHaveCount(0);
});

async function dragPaletteAction(
  page: Page,
  target: () => Promise<{ x: number; y: number }>,
  name = 'action',
) {
  if (await page.locator('.palette-dock_collapsed').count()) {
    await page.getByRole('button', { name: 'Basculer la palette de blocs', exact: true }).click();
  }
  const card = page.locator('.component-card').filter({
    has: page.locator('.component-title').filter({ hasText: new RegExp('^' + name + '$') }),
  });
  await openPaletteCategoryFor(page, card);
  await card.scrollIntoViewIfNeeded();
  const rect = (await card.boundingBox())!;
  await page.mouse.move(rect.x + 40, rect.y + 30);
  await page.mouse.down();
  await page.mouse.move(rect.x + 48, rect.y + 30, { steps: 4 });
  const point = await target();
  await page.mouse.move(point.x, point.y, { steps: 20 });
  await expect(page.locator('.palette-drag-preview')).toBeVisible();
  await page.mouse.up();
}

async function openPaletteCategoryFor(page: Page, card: Locator) {
  const category = page.locator('.category-group').first();
  const isOpen = await category.evaluate((element) => (element as HTMLDetailsElement).open);
  if (!isOpen) await category.locator('summary').click();
  await expect(card).toBeVisible();
}
test('palette drag places canvas blocks and cancels outside the workspace', async ({ page }) => {
  await setup(page);
  await dragPaletteAction(page, async () => {
    const rect = (await page.locator('.canvas-surface').boundingBox())!;
    return { x: rect.x + 90, y: rect.y + 280 };
  });
  await expect(page.locator('.canvas-node')).toHaveCount(6);
  const added = page.locator('.canvas-node[data-node-id^="flow-node-"]');
  await expect(added).toHaveCSS('left', '90px');
  await expect(added).toHaveCSS('top', '280px');
  const handle = added.getByRole('button', { name: 'Déplacer l’étape' });
  const handleBounds = (await handle.boundingBox())!;
  const before = await added.evaluate((element) => ({
    left: (element as HTMLElement).style.left,
    top: (element as HTMLElement).style.top,
  }));
  await handle.dispatchEvent('pointerdown', {
    pointerId: 1,
    pointerType: 'mouse',
    button: 0,
    clientX: handleBounds.x + handleBounds.width / 2,
    clientY: handleBounds.y + handleBounds.height / 2,
  });
  await page.evaluate(
    ({ x, y }) =>
      document.dispatchEvent(
        new PointerEvent('pointermove', {
          pointerId: 1,
          pointerType: 'mouse',
          clientX: x,
          clientY: y,
        }),
      ),
    {
      x: handleBounds.x + handleBounds.width / 2 + 70,
      y: handleBounds.y + handleBounds.height / 2 + 50,
    },
  );
  await expect(added).toHaveClass(/dragging/);
  await page.evaluate(() =>
    document.dispatchEvent(new PointerEvent('pointerup', { pointerId: 1 })),
  );
  await expect
    .poll(() =>
      added.evaluate((element) => ({
        left: (element as HTMLElement).style.left,
        top: (element as HTMLElement).style.top,
      })),
    )
    .not.toEqual(before);
  await dragPaletteAction(page, async () => ({ x: 25, y: 25 }));
  await expect(page.locator('.canvas-node')).toHaveCount(6);
});

test('friendly editor opens the inspector beside the flow and closes the palette', async ({
  page,
}, info) => {
  await setup(page);
  await expect(page.locator('.palette-dock_collapsed')).toHaveCount(1);
  await expect(page.locator('app-flow-inline-parameters')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Vérifier', exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Basculer la palette de blocs', exact: true }).click();
  await page.locator('.canvas-node[data-node-id="Entrée"] .canvas-title').click();
  const inspector = page.getByRole('complementary', { name: 'Entrée', exact: true });
  await expect(inspector).toBeVisible();
  await expect(page.locator('.palette-dock_collapsed')).toHaveCount(1);
  await expect(inspector.getByLabel('Nom du bloc', { exact: true })).toBeVisible();
  await page.screenshot({ path: info.outputPath('inspector.png'), fullPage: true });
  await page.evaluate(() => (document.documentElement.dataset['theme'] = 'dark'));
  await page.screenshot({ path: info.outputPath('inspector-dark.png'), fullPage: true });
  await inspector.getByRole('button', { name: 'Fermer le panneau', exact: true }).click();
  await expect(inspector).toBeHidden();
  await expect(page.locator('.canvas-node[data-node-id="Entrée"]')).toBeVisible();
});

test('palette closes after a click insertion and can be pinned for successive additions', async ({
  page,
}) => {
  await setup(page);
  const add = page.getByRole('button', { name: 'Basculer la palette de blocs', exact: true });
  await add.click();
  const action = page
    .locator('.component-card')
    .filter({ has: page.locator('.component-title').filter({ hasText: /^action$/ }) });
  await openPaletteCategoryFor(page, action);
  await action.click();
  await expect(page.locator('.canvas-node')).toHaveCount(6);
  await expect(page.locator('.palette-dock_collapsed')).toHaveCount(1);
  await add.click();
  await page
    .getByRole('button', { name: 'Garder la palette ouverte après un ajout', exact: true })
    .click();
  const pinnedAction = page
    .locator('.component-card')
    .filter({ has: page.locator('.component-title').filter({ hasText: /^action$/ }) });
  await openPaletteCategoryFor(page, pinnedAction);
  await pinnedAction.click();
  await expect(page.locator('.canvas-node')).toHaveCount(7);
  await expect(page.locator('.palette-dock_collapsed')).toHaveCount(0);
});

test('successful test displays the taken path on blocks without keeping a bottom panel', async ({
  page,
}, info) => {
  await setup(page);
  await page.route('**/api/v1/flows/simulate', (r) =>
    r.fulfill({
      json: {
        status: 'completed',
        detail: 'Parcours terminé',
        steps: [
          {
            nodeId: 'Entrée',
            name: 'Entrée',
            outputId: 'success',
            detail: 'Entrée parcourue',
            assumed: false,
            workerNodeId: 'worker',
            context: {
              extension: 'mkv',
              variables: {},
              beforeMetadata: {},
              currentMetadata: {},
              plan: [],
            },
          },
        ],
      },
    }),
  );
  await page.getByRole('button', { name: 'Tester', exact: true }).click();
  await expect(
    page.getByRole('button', { name: 'Étape parcourue · Voir les détails du test', exact: true }),
  ).toBeVisible();
  await expect(page.locator('.simulation-tray')).toHaveCount(0);
  await page.screenshot({ path: info.outputPath('tested-flow.png'), fullPage: true });
  await page
    .getByRole('button', { name: 'Étape parcourue · Voir les détails du test', exact: true })
    .click();
  await expect(page.locator('.simulation-tray')).toBeVisible();
  await page.getByRole('button', { name: 'Réduire', exact: true }).click();
  await page.getByRole('button', { name: 'Actions de Action A', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Dupliquer', exact: true }).click();
  await expect(page.locator('app-flow-simulation-status button')).toHaveCount(0);
});

test('inspector preserves optional advanced settings while editing a common setting', async ({
  page,
}) => {
  await setup(page);
  await page.route('**/api/v1/flows/components', (r) =>
    r.fulfill({
      json: [
        {
          id: 'base',
          version: '1.0.0',
          name: 'Base',
          components: components.map((c) =>
            c.id === 'action'
              ? {
                  ...c,
                  configurationSchema: {
                    version: 1,
                    fields: [
                      {
                        id: 'quality',
                        label: 'Qualité',
                        type: 'number',
                        required: true,
                        inline: true,
                        defaultValue: 20,
                      },
                      {
                        id: 'option',
                        label: 'Option avancée',
                        importance: 'advanced',
                        type: 'text',
                        defaultValue: 'Conserver',
                      },
                    ],
                  },
                }
              : c,
          ),
        },
      ],
    }),
  );
  await page.reload();
  const card = page.locator('.canvas-node[data-node-id="Action A"]');
  await card.locator('.drag').focus();
  await page.keyboard.press('Tab');
  await expect(
    page.locator('.mat-mdc-tooltip-surface').filter({ hasText: 'Action A · Qualité : 20' }),
  ).toBeVisible();
  await card.locator('.canvas-title').click();
  const inspector = page.getByRole('complementary', { name: 'Action A', exact: true });
  await inspector.getByLabel('Qualité', { exact: true }).fill('22');
  await expect(inspector.getByLabel('Option avancée', { exact: true })).toBeHidden();
  await inspector.getByText('Réglages avancés · 1', { exact: true }).click();
  await expect(inspector.getByLabel('Option avancée', { exact: true })).toHaveValue('Conserver');
  await inspector.getByRole('button', { name: 'Appliquer les réglages', exact: true }).click();
  await card.locator('.drag').focus();
  await page.keyboard.press('Tab');
  await expect(
    page.locator('.mat-mdc-tooltip-surface').filter({ hasText: 'Action A · Qualité : 22' }),
  ).toBeVisible();
});

test('canvas wires follow dragging before release and selection highlights connections', async ({
  page,
}) => {
  await setup(page);
  const entry = page.locator('.canvas-node').first();
  await entry.locator('.canvas-plugin').click();
  await expect(page.locator('.wire.highlighted')).not.toHaveCount(0);
  const wire = page.locator('.wire').first();
  const before = await wire.getAttribute('d');
  const handle = entry.getByRole('button', { name: 'Déplacer l’étape' });
  const beforePosition = await entry.evaluate((node) => ({
    left: (node as HTMLElement).style.left,
    top: (node as HTMLElement).style.top,
  }));
  const rect = (await handle.boundingBox())!;
  await page.mouse.move(rect.x + rect.width / 2, rect.y + rect.height / 2);
  await page.mouse.down();
  await page.mouse.move(rect.x + rect.width / 2 + 60, rect.y + rect.height / 2 + 70, { steps: 12 });
  await expect(wire).not.toHaveAttribute('d', before!);
  expect(await page.evaluate(() => window.getSelection()?.toString())).toBe('');
  const during = await wire.getAttribute('d');
  await page.mouse.up();
  await expect(wire).toHaveAttribute('d', during!);
  await expect
    .poll(() =>
      entry.evaluate((node) => ({
        left: (node as HTMLElement).style.left,
        top: (node as HTMLElement).style.top,
      })),
    )
    .not.toEqual(beforePosition);
  const movedPosition = await entry.evaluate((node) => ({
    left: (node as HTMLElement).style.left,
    top: (node as HTMLElement).style.top,
  }));
  await page.reload();
  await expect(page.locator('.canvas-node')).toHaveCount(5);
  await expect
    .poll(() =>
      page
        .locator('.canvas-node')
        .first()
        .evaluate((node) => ({
          left: (node as HTMLElement).style.left,
          top: (node as HTMLElement).style.top,
        })),
    )
    .toEqual(movedPosition);
});

test('adding a block on an output opens the palette for that branch', async ({ page }) => {
  await setup(page);
  const action = page.locator('.canvas-node[data-node-id="Action A"]');
  await action.locator('[data-port="output:success"]').focus();
  await action.getByRole('button', { name: 'Ajouter sur la sortie Suite', exact: true }).click();
  await expect(page.locator('.insertion-banner')).toContainText(
    'Insertion sur la sortie « Suite » de Action A',
  );
  await expect(
    page.getByRole('complementary', { name: 'Palette des composants du flow' }),
  ).toBeVisible();
});

test('canvas workspace fills the available width and separates blocks from its toolbar', async ({
  page,
}) => {
  await setup(page);
  const toolbar = (await page.locator('.command-bar').boundingBox())!;
  const workspace = (await page.locator('.flow-workspace-main').boundingBox())!;
  expect(Math.abs(toolbar.width - workspace.width)).toBeLessThan(2);
  const firstBlock = (await page.locator('.canvas-node').first().boundingBox())!;
  expect(firstBlock.y - toolbar.y - toolbar.height).toBeGreaterThanOrEqual(16);
});

test('inspector keeps deletion in the shared action footer', async ({ page }, info) => {
  await setup(page);
  await page.locator('.canvas-node[data-node-id="Action A"] .canvas-title').click();
  const inspector = page.getByRole('complementary', { name: 'Action A', exact: true });
  const footer = inspector.locator('.form-actions');
  await expect(footer.getByRole('button', { name: 'Supprimer ce bloc' })).toBeVisible();
  await expect(footer.getByRole('button', { name: 'Appliquer les réglages' })).toBeVisible();
  if (info.project.name === 'desktop') {
    const remove = (await footer.getByRole('button', { name: 'Supprimer ce bloc' }).boundingBox())!;
    const apply = (await footer
      .getByRole('button', { name: 'Appliquer les réglages' })
      .boundingBox())!;
    expect(Math.abs(remove.y + remove.height / 2 - apply.y - apply.height / 2)).toBeLessThan(2);
  }
  const applyButton = footer.getByRole('button', { name: 'Appliquer les réglages' });
  expect(
    await applyButton.evaluate((button) =>
      (() => {
        const text = button.querySelector('.mdc-button__label')!.getBoundingClientRect();
        const bounds = button.getBoundingClientRect();
        return (
          text.top >= bounds.top &&
          text.bottom <= bounds.bottom &&
          text.left >= bounds.left &&
          text.right <= bounds.right
        );
      })(),
    ),
  ).toBe(true);
  await page.screenshot({ path: info.outputPath('aligned-inspector.png') });
});

test('canvas nodes contain their actions and expose the shared connections', async ({
  page,
}, info) => {
  await setup(page);
  await expect(page.locator('.wire')).toHaveCount(5);
  const contained = await page.locator('.canvas-node').evaluateAll((cards) =>
    cards.every((card) => {
      const bounds = card.getBoundingClientRect();
      return [...card.querySelectorAll('.more, .canvas-title')].every((button) => {
        const b = button.getBoundingClientRect();
        return (
          b.left >= bounds.left &&
          b.right <= bounds.right &&
          b.top >= bounds.top &&
          b.bottom <= bounds.bottom
        );
      });
    }),
  );
  expect(contained).toBe(true);
  await page.screenshot({ path: info.outputPath('canvas-nodes.png') });
});

test('canvas layout is explicit, reversible and zoom stays inside the workspace', async ({
  page,
}, info) => {
  await setup(page);
  const readPositions = () =>
    page.locator('.canvas-node').evaluateAll((cards) =>
      cards.map((card) => ({
        id: card.getAttribute('data-node-id'),
        left: (card as HTMLElement).style.left,
        top: (card as HTMLElement).style.top,
      })),
    );
  await expect(page.locator('.wire').first()).toHaveAttribute('d', /^M /);
  const before = await readPositions();
  await page.getByRole('button', { name: 'Autres actions du flow', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Disposition automatique', exact: true }).click();
  await expect(
    page.getByRole('button', { name: 'Annuler la disposition', exact: true }),
  ).toBeVisible();
  const placed = await page.locator('.canvas-node').evaluateAll((cards) =>
    cards.map((card) => {
      const e = card as HTMLElement;
      return {
        id: e.dataset['nodeId'],
        x: parseFloat(e.style.left),
        y: parseFloat(e.style.top),
        width: e.offsetWidth,
        height: e.offsetHeight,
      };
    }),
  );
  for (let i = 0; i < placed.length; i++)
    for (let j = i + 1; j < placed.length; j++) {
      const a = placed[i],
        b = placed[j];
      expect(
        a.x + a.width <= b.x ||
          b.x + b.width <= a.x ||
          a.y + a.height <= b.y ||
          b.y + b.height <= a.y,
      ).toBe(true);
    }
  await page.getByRole('button', { name: 'Annuler la disposition', exact: true }).click();
  await expect.poll(readPositions).toEqual(before);
  const canvas = page.locator('.canvas-viewport');
  const oldScale = await page.locator('.canvas-surface').getAttribute('style');
  await canvas.dispatchEvent('wheel', { deltaY: -100, ctrlKey: false });
  await expect(page.locator('.canvas-surface')).toHaveAttribute('style', oldScale!);
  const box = (await canvas.boundingBox())!;
  await canvas.dispatchEvent('wheel', {
    deltaY: -100,
    ctrlKey: true,
    clientX: box.x + 80,
    clientY: box.y + 100,
  });
  await expect(page.locator('.canvas-surface')).not.toHaveAttribute('style', oldScale!);
  const minimap = page.getByRole('button', { name: 'Naviguer dans la vue d’ensemble' });
  await expect(minimap).toBeVisible();
  await minimap.click({ position: { x: 150, y: 55 } });
  await expect
    .poll(() => canvas.evaluate((element) => element.scrollLeft + element.scrollTop))
    .toBeGreaterThan(0);
  await page.getByRole('button', { name: 'Zoomer', exact: true }).click();
  const scaleBeforeZoom = Number(
    (await page.locator('.canvas-surface').getAttribute('style'))!.match(/scale\(([^)]+)\)/)?.[1],
  );
  await page.getByRole('button', { name: 'Zoomer', exact: true }).click();
  await expect
    .poll(async () =>
      Number(
        (await page.locator('.canvas-surface').getAttribute('style'))!.match(
          /scale\(([^)]+)\)/,
        )?.[1],
      ),
    )
    .toBeGreaterThan(scaleBeforeZoom);
  const resetOrFit = page.locator('.minimap-zoom-reset');
  await resetOrFit.click();
  await expect(resetOrFit).toHaveAttribute('aria-label', 'Tout afficher');
  await expect
    .poll(async () =>
      Number(
        (await page.locator('.canvas-surface').getAttribute('style'))!.match(
          /scale\(([^)]+)\)/,
        )?.[1],
      ),
    )
    .toBe(1);
  await resetOrFit.click();
  await expect(resetOrFit).toHaveAttribute('aria-label', 'Revenir à 100 %');
  await page.screenshot({ path: info.outputPath('compact-canvas.png') });
  const violations = (
    await new AxeBuilder({ page })
      .include('app-flow-canvas')
      .withTags(['wcag2a', 'wcag2aa'])
      .analyze()
  ).violations;
  expect(violations).toEqual([]);
});

test('multiple exclusive cases, nested decisions and protected ends remain navigable on canvas', async ({
  page,
}, info) => {
  await setup(page);
  const kinds = {
    ...component('subtitle-kind', 'condition', []),
    outputs: [
      { id: 'text', label: 'Texte', description: 'Sous-titres textuels' },
      { id: 'image', label: 'Image', description: 'Sous-titres image' },
      { id: 'absent', label: 'Absent', description: 'Aucune piste' },
    ],
  };
  const complexNodes = [
    node('Entrée', 'base-input-file'),
    node('Vidéo conforme ?', 'decision'),
    node('GPU utilisable ?', 'decision'),
    node('Encoder GPU', 'action'),
    node('Encoder CPU', 'action'),
    node('Audio conforme ?', 'decision'),
    node('Normaliser audio', 'action'),
    node('Sous-titres', 'subtitle-kind'),
    node('Convertir ASS', 'action'),
    node('Conserver PGS', 'action'),
    node('Sortie valide ?', 'decision'),
    node('Taille réduite ?', 'decision'),
    node('Succès', 'finish'),
    node('Conserver original', 'finish'),
    node('Échec protégé', 'finish'),
  ];
  const connections = [
    edge('c0', 'Entrée', 'success', 'Vidéo conforme ?'),
    edge('c1', 'Vidéo conforme ?', 'yes', 'Audio conforme ?'),
    edge('c2', 'Vidéo conforme ?', 'no', 'GPU utilisable ?'),
    edge('c3', 'GPU utilisable ?', 'yes', 'Encoder GPU'),
    edge('c4', 'GPU utilisable ?', 'no', 'Encoder CPU'),
    edge('c5', 'Encoder GPU', 'success', 'Audio conforme ?'),
    edge('c6', 'Encoder CPU', 'success', 'Audio conforme ?'),
    edge('c7', 'Audio conforme ?', 'yes', 'Sous-titres'),
    edge('c8', 'Audio conforme ?', 'no', 'Normaliser audio'),
    edge('c9', 'Normaliser audio', 'success', 'Sous-titres'),
    edge('c10', 'Sous-titres', 'text', 'Convertir ASS'),
    edge('c11', 'Sous-titres', 'image', 'Conserver PGS'),
    edge('c12', 'Sous-titres', 'absent', 'Sortie valide ?'),
    edge('c13', 'Convertir ASS', 'success', 'Sortie valide ?'),
    edge('c14', 'Conserver PGS', 'success', 'Sortie valide ?'),
    edge('c15', 'Sortie valide ?', 'yes', 'Taille réduite ?'),
    edge('c16', 'Sortie valide ?', 'no', 'Échec protégé'),
    edge('c17', 'Taille réduite ?', 'yes', 'Succès'),
    edge('c18', 'Taille réduite ?', 'no', 'Conserver original'),
  ];
  await page.route('**/api/v1/flows/components', (r) =>
    r.fulfill({
      json: [{ id: 'base', version: '1.0.0', name: 'Base', components: [...components, kinds] }],
    }),
  );
  await page.route('**/api/v1/flows/11111111-1111-4111-8111-111111111111/edit', (r) =>
    r.fulfill({
      json: {
        definition: { ...flow, nodes: complexNodes, connections },
        publishedVersion: 1,
        backupStatus: 'ready',
      },
    }),
  );
  await page.reload();
  await expect(page.locator('.canvas-node')).toHaveCount(15);
  for (const name of ['Audio conforme ?', 'Sous-titres', 'Sortie valide ?'])
    await expect(page.locator(`.canvas-node[data-node-id="${name}"]`)).toHaveCount(1);
  await page.locator('.canvas-node[data-node-id="Sous-titres"]').scrollIntoViewIfNeeded();
  await page.getByRole('button', { name: 'Autres actions du flow', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Disposition automatique', exact: true }).click();
  await expect(page.locator('.canvas-node')).toHaveCount(15);
  await expect(page.locator('.wire')).toHaveCount(19);
  await expect
    .poll(() =>
      page.locator('.wire').evaluateAll((paths) => paths.every((p) => !!p.getAttribute('d'))),
    )
    .toBe(true);
  await page.screenshot({ path: info.outputPath('complex-canvas.png') });
});

test('structured audio rules remain editable and preserved on desktop and mobile', async ({
  page,
}, info) => {
  await setup(page);
  const audio = JSON.parse(
    readFileSync('../backend/tests/Fixtures/StoreCatalog/audio.json', 'utf8'),
  );
  const ruleComponent = audio.components.find(
    (item: { id: string }) => item.id === 'audio-convert-by-rules',
  );
  await page.route('**/api/v1/flows/components', (route) =>
    route.fulfill({
      json: [
        {
          id: 'base',
          version: '1.0.0',
          name: 'Base',
          components: components.map((c) =>
            c.id === 'action'
              ? { ...c, configurationSchema: ruleComponent.configurationSchema }
              : c,
          ),
        },
      ],
    }),
  );
  await page.reload();
  const card = page.locator('.canvas-node[data-node-id="Action A"]');
  await card.locator('.canvas-title').click();
  const form = page.locator('app-component-configuration-form');
  const rows = form.locator('.record');
  await expect(rows).toHaveCount(2);
  await rows.first().getByLabel('Langues', { exact: true }).fill('fra,jpn');
  await form.getByRole('button', { name: 'Ajouter une règle', exact: true }).click();
  await expect(rows).toHaveCount(3);
  await rows.nth(2).getByLabel('Action', { exact: true }).click();
  await page.getByRole('option', { name: 'Conserver sans réencoder', exact: true }).click();
  await expect(rows.nth(2).getByLabel('Codec cible', { exact: true })).toHaveCount(0);
  await form.getByRole('button', { name: 'Appliquer les réglages', exact: true }).click();
  await card.locator('.canvas-title').click();
  await expect(form.locator('.record')).toHaveCount(3);
  await expect(form.locator('.record').first().getByLabel('Langues', { exact: true })).toHaveValue(
    'fra,jpn',
  );
  await expect(form.locator('.record').nth(2).getByLabel('Action', { exact: true })).toContainText(
    'Conserver sans réencoder',
  );
  await expect(page.locator('body')).toHaveJSProperty(
    'scrollWidth',
    await page.locator('body').evaluate((element) => element.clientWidth),
  );
  await page.screenshot({ path: info.outputPath('structured-audio-rules.png') });
});

test('compact adaptive cards expose semantic ports and keep measured wires aligned in both themes', async ({
  page,
}, info) => {
  await setup(page);
  const longTitle = 'Actualiser la bibliothèque multimédia après validation';
  const ids = ['success', 'yes', 'no', 'error', 'next', 'skipped', 'waiting', 'custom'];
  await page.route('**/api/v1/flows/components', (r) =>
    r.fulfill({
      json: [
        {
          id: 'base',
          version: '1.0.0',
          name: 'Base',
          components: components.map((c) =>
            c.id === 'action'
              ? {
                  ...c,
                  outputs: ids.map((id) => ({ id, label: id, description: 'Chemin ' + id })),
                }
              : c,
          ),
        },
      ],
    }),
  );
  await page.route('**/api/v1/flows/11111111-1111-4111-8111-111111111111/edit', (r) =>
    r.fulfill({
      json: {
        definition: {
          ...flow,
          nodes: flow.nodes.map((n) => (n.id === 'Action A' ? { ...n, name: longTitle } : n)),
        },
        publishedVersion: 1,
        backupStatus: 'ready',
      },
    }),
  );
  await page.reload();
  const action = page.locator('.canvas-node[data-node-id="Action A"]');
  await expect(action.locator('.output')).toHaveCount(8);
  await expect.poll(() => action.evaluate((n) => (n as HTMLElement).offsetHeight)).toBe(64);
  const entryWidth = await page
    .locator('.canvas-node[data-node-id="Entrée"]')
    .evaluate((n) => (n as HTMLElement).offsetWidth);
  const actionWidth = await action.evaluate((n) => (n as HTMLElement).offsetWidth);
  expect(actionWidth).toBeGreaterThan(entryWidth);
  expect(actionWidth).toBeLessThanOrEqual(336);
  const icon = (id: string) => action.locator(`[data-port="output:${id}"] app-icon`);
  await expect(icon('yes')).toHaveCount(1);
  await expect(icon('no')).toHaveCount(1);
  await expect(icon('error')).toHaveCount(1);
  expect(await icon('no').locator('path').getAttribute('d')).not.toBe(
    await icon('error').locator('path').getAttribute('d'),
  );
  await expect(action.locator('[data-port="output:custom"]')).toHaveText('8');
  await action.locator('.drag').focus();
  await page.keyboard.press('Tab');
  await expect(page.locator('.mat-mdc-tooltip-surface')).toContainText(longTitle);
  await page.keyboard.press('Escape');
  const yes = action.locator('[data-port="output:yes"]');
  await yes.focus();
  await expect(
    action.getByRole('button', { name: 'Ajouter sur la sortie yes', exact: true }),
  ).toBeVisible();
  const portsDoNotOverlap = await action.locator('.output').evaluateAll((ns) => {
    const rects = ns.map((n) => n.getBoundingClientRect());
    return rects.every((r, i) =>
      rects.every((s, j) => i === j || r.right <= s.left || s.right <= r.left),
    );
  });
  expect(portsDoNotOverlap).toBe(true);
  // Start and end of an actual routed link must remain at the measured port centers.
  for (const theme of ['light', 'dark']) {
    await page.evaluate((mode) => {
      document.documentElement.dataset['theme'] = mode;
    }, theme);
    await page.getByRole('button', { name: 'Autres actions du flow', exact: true }).click();
    await page.getByRole('menuitem', { name: 'Disposition automatique', exact: true }).click();
    await expect
      .poll(async () =>
        page.evaluate(() => {
          const wire = document.querySelector<SVGPathElement>('.wire')!;
          const surface = document.querySelector('.canvas-surface')!.getBoundingClientRect();
          const source = document
            .querySelector('[data-node-id="Entrée"] [data-port="output:success"]')!
            .getBoundingClientRect();
          const target = document
            .querySelector('[data-node-id="Décision"] [data-port="input"]')!
            .getBoundingClientRect();
          const scale =
            document.querySelector<HTMLElement>('.canvas-surface')!.offsetWidth / surface.width;
          const start = wire.getPointAtLength(0),
            end = wire.getPointAtLength(wire.getTotalLength());
          return Math.max(
            Math.abs(start.x - (source.x + source.width / 2 - surface.x) * scale),
            Math.abs(start.y - (source.y + source.height / 2 - surface.y) * scale),
            Math.abs(end.x - (target.x + target.width / 2 - surface.x) * scale),
            Math.abs(end.y - (target.y + target.height / 2 - surface.y) * scale),
          );
        }),
      )
      .toBeLessThan(1);
    const violations = (
      await new AxeBuilder({ page })
        .include('app-flow-canvas')
        .withTags(['wcag2a', 'wcag2aa'])
        .analyze()
    ).violations;
    expect(violations).toEqual([]);
    await page.screenshot({ path: info.outputPath(`adaptive-cards-${theme}.png`) });
  }
  await action.focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('complementary', { name: longTitle, exact: true })).toBeVisible();
});
