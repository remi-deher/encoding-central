import { expect, Page, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
const pageErrors = new WeakMap<Page, string[]>();
test.beforeEach(async ({ page }) => {
  const errors: string[] = [];
  pageErrors.set(page, errors);
  page.on('pageerror', (error) => errors.push(error.message));
});
test.afterEach(async ({ page }) => {
  expect(pageErrors.get(page)).toEqual([]);
});
const id = '11111111-1111-4111-8111-111111111111';
const reference = (componentId: string) => ({
  pluginId: 'base',
  pluginVersion: '1.0.0',
  componentId,
  componentVersion: '1.0.0',
  configurationSchemaVersion: 1,
});
const node = (id: string, componentId: string) => ({
  id,
  name: id,
  component: reference(componentId),
  configuration: {},
});
const flow = {
  formatVersion: 1,
  id,
  version: 2,
  name: 'Cinéma',
  mediaType: 'video',
  nodes: [
    node('Entrée', 'base-input-file'),
    node('Décision', 'condition'),
    node('Action', 'action'),
    node('Fin', 'finish'),
  ],
  connections: [
    { id: 'a', sourceNodeId: 'Entrée', sourceOutputId: 'success', targetNodeId: 'Décision' },
    { id: 'b', sourceNodeId: 'Décision', sourceOutputId: 'yes', targetNodeId: 'Action' },
    { id: 'c', sourceNodeId: 'Décision', sourceOutputId: 'no', targetNodeId: 'Fin' },
    { id: 'd', sourceNodeId: 'Action', sourceOutputId: 'success', targetNodeId: 'Fin' },
  ],
};
const older = { ...flow, version: 1, name: 'Cinéma ancien' };
async function setup(
  page: Page,
  options: { viewer?: boolean; conflict?: boolean; historyFailure?: boolean } = {},
) {
  const writes: unknown[] = [];
  await page.route('**/api/**', (r) => r.fulfill({ json: [] }));
  await page.route('**/api/v1/auth/status', (r) =>
    r.fulfill({
      json: {
        setupRequired: false,
        user: { id: 'test', username: 'test', role: options.viewer ? 'viewer' : 'admin' },
      },
    }),
  );
  await page.route('**/api/v1/flows', (r) =>
    r.fulfill({
      json: [
        {
          id,
          name: 'Cinéma',
          mediaType: 'video',
          version: 2,
          isDraft: true,
          nodeCount: 4,
          backupStatus: 'ready',
          updatedAt: '2026-10-21T08:00:00Z',
        },
      ],
    }),
  );
  await page.route('**/api/v1/flows/components', (r) =>
    r.fulfill({
      json: [
        {
          id: 'base',
          version: '1.0.0',
          name: 'Base',
          components: [
            ['base-input-file', 'input', ['success']],
            ['condition', 'condition', ['yes', 'no']],
            ['action', 'action', ['success']],
            ['finish', 'terminal', []],
          ].map(([id, kind, outputs]) => ({
            id,
            kind,
            name: id,
            version: '1.0.0',
            description: 'Description',
            mediaType: 'general',
            outputs: (outputs as string[]).map((id) => ({
              id,
              label: id === 'yes' ? 'Oui' : id === 'no' ? 'Non' : 'Suite',
              description: 'Suite',
            })),
            configurationSchema: { version: 1, fields: [] },
          })),
        },
      ],
    }),
  );
  await page.route('**/api/v1/flows/' + id + '/edit', (r) =>
    r.fulfill({
      json: {
        definition: { ...flow, version: 3 },
        publishedVersion: 2,
        draftRevision: 7,
        backupStatus: 'ready',
        createdAt: '2026-10-21T08:00:00Z',
      },
    }),
  );
  await page.route('**/api/v1/flows/' + id + '/versions', (r) =>
    options.historyFailure
      ? r.fulfill({ status: 503, json: { title: 'Unavailable' } })
      : r.fulfill({ json: [older, flow] }),
  );
  await page.route('**/api/v1/flows/' + id + '/versions/1', (r) =>
    r.fulfill({
      json: { definition: older, backupStatus: 'ready', createdAt: '2026-10-19T08:00:00Z' },
    }),
  );
  await page.route('**/api/v1/flows/' + id + '/draft-backups', (r) =>
    r.fulfill({
      json: [{ revision: 6, backupStatus: 'ready', checkedAt: '2026-10-20T08:00:00Z' }],
    }),
  );
  await page.route('**/api/v1/flows/' + id + '/draft-backups/6', (r) =>
    r.fulfill({ json: { ...older, name: 'Sauvegarde historique' } }),
  );
  await page.route('**/api/v1/flows/' + id + '/usage', (r) =>
    r.fulfill({
      json: [{ libraryId: 'library', libraryName: 'Films USB', version: 1, isActive: true }],
    }),
  );
  await page.route('**/api/v1/flows/' + id + '/preflight', (r) =>
    r.fulfill({
      json: {
        draftRevision: 7,
        publishedVersion: 2,
        graph: { valid: true, issues: [] },
        execution: {
          workerCompatible: true,
          compatibleWorkers: [{ id: 'worker', name: 'Encodeur salon' }],
          observedAt: '2026-10-21T08:00:00Z',
        },
      },
    }),
  );
  await page.route('**/api/v1/flows/' + id + '/draft', (r) => {
    writes.push(r.request().postDataJSON());
    return options.conflict
      ? r.fulfill({ status: 409, json: { title: 'Le brouillon a été modifié ailleurs.' } })
      : r.fulfill({
          json: {
            definition: r.request().postDataJSON().definition,
            draftRevision: 8,
            publishedVersion: 2,
            backupStatus: 'pending',
            createdAt: '2026-10-21T08:00:00Z',
          },
        });
  });
  await page.route('**/api/v1/flows/' + id + '/clone', (r) => {
    writes.push({ clone: r.request().postDataJSON() });
    return r.fulfill({
      json: {
        definition: { ...flow, id: 'copy', name: 'Cinéma — Copie' },
        draftRevision: 1,
        publishedVersion: 0,
      },
    });
  });
  await page.goto('/flows');
  await page.getByRole('button', { name: 'Consulter', exact: true }).click();
  const drawer = page.getByRole('dialog', { name: 'Cinéma', exact: true });
  await expect(drawer.locator('.canvas-node')).toHaveCount(4);
  return { drawer, writes };
}
test('drawer previews the real graph on a read-only canvas', async ({ page }, info) => {
  const { drawer, writes } = await setup(page);
  await expect(drawer.getByRole('tab', { name: 'Aperçu', exact: true })).toHaveAttribute(
    'aria-selected',
    'true',
  );
  await expect(
    drawer.getByRole('button', { name: /Configurer |Actions de |Ajouter une étape/ }),
  ).toHaveCount(0);
  await expect(drawer.locator('.canvas-node')).toHaveCount(4);
  await expect(drawer.locator('.wire')).toHaveCount(4);
  await drawer.getByRole('button', { name: 'Tout afficher', exact: true }).click();
  await expect(
    drawer.getByRole('button', { name: /Configurer |Actions de |Disposition automatique/ }),
  ).toHaveCount(0);
  expect(writes).toHaveLength(0);
  await expect(drawer.locator('.mat-ripple-element')).toHaveCount(0);
  const a11y = await new AxeBuilder({ page }).include('app-flow-details').analyze();
  expect(a11y.violations).toEqual([]);
  await page.screenshot({ path: 'test-results/flow-drawer-' + info.project.name + '.png' });
});
for (const conflict of [false, true])
  test(
    'restore historical published version with concurrency protection ' + conflict,
    async ({ page }) => {
      const { drawer, writes } = await setup(page, { conflict });
      await drawer.getByRole('tab', { name: 'Versions', exact: true }).click();
      await expect(drawer.getByLabel('Différences entre les versions')).toContainText(
        'Cinéma ancien',
      );
      await drawer.getByRole('button', { name: 'Prévisualiser la version 1', exact: true }).click();
      await expect(drawer.getByText('Version publiée v1', { exact: true })).toBeVisible();
      await drawer.getByRole('button', { name: 'Créer un brouillon depuis cet aperçu' }).click();
      const confirmation = page.getByRole('dialog', {
        name: 'Créer un brouillon depuis cet aperçu ?',
        exact: true,
      });
      await expect(confirmation).toContainText('Le brouillon actuel sera remplacé');
      await confirmation.getByRole('button', { name: 'Créer le brouillon', exact: true }).click();
      await expect.poll(() => writes.length).toBe(1);
      expect(writes[0]).toMatchObject({
        expectedRevision: 7,
        definition: { id, version: 3, name: 'Cinéma ancien' },
      });
      await expect(
        page.getByText(
          conflict
            ? 'Le brouillon a été modifié ailleurs.'
            : 'Brouillon créé. Examinez-le dans l’éditeur avant publication.',
          { exact: true },
        ),
      ).toBeVisible();
    },
  );
test('drawer shows library versions, observed workers and archived draft preview', async ({
  page,
}) => {
  const { drawer } = await setup(page);
  await drawer.getByRole('tab', { name: 'Utilisation', exact: true }).click();
  await expect(drawer).toContainText('Films USB');
  await expect(drawer).toContainText('Version attribuée v1');
  await drawer.getByRole('tab', { name: 'Validation', exact: true }).click();
  await expect(drawer).toContainText('Encodeur salon');
  await drawer.getByRole('tab', { name: 'Sauvegardes et export', exact: true }).click();
  await drawer.getByRole('button', { name: 'Prévisualiser la sauvegarde 6', exact: true }).click();
  await expect(drawer).toContainText('Sauvegarde de brouillon r6');
});
test('viewer can preview but cannot restore or duplicate, and history failures do not hide preview', async ({
  page,
}) => {
  const { drawer, writes } = await setup(page, { viewer: true, historyFailure: true });
  await expect(drawer.getByRole('button', { name: 'Dupliquer le flow actuel' })).toHaveCount(0);
  await drawer.getByRole('tab', { name: 'Versions', exact: true }).click();
  await expect(drawer).toContainText('Historique indisponible.');
  await drawer.getByRole('tab', { name: 'Sauvegardes et export', exact: true }).click();
  await expect(drawer.getByText('Restaurer un fichier de sauvegarde', { exact: true })).toHaveCount(
    0,
  );
  await drawer.getByRole('button', { name: 'Prévisualiser la sauvegarde 6', exact: true }).click();
  await expect(
    drawer.getByRole('button', { name: 'Créer un brouillon depuis cet aperçu' }),
  ).toHaveCount(0);
  expect(writes).toHaveLength(0);
});

test('duplicate uses a new flow and JSON export contains the selected historical version', async ({
  page,
}) => {
  const { drawer, writes } = await setup(page);
  await drawer.getByRole('button', { name: 'Dupliquer le flow actuel', exact: true }).click();
  await expect(page.getByText('Flow dupliqué : Cinéma — Copie', { exact: true })).toBeVisible();
  expect(writes[0]).toEqual({ clone: { name: 'Cinéma — Copie' } });
  await drawer.getByRole('tab', { name: 'Versions', exact: true }).click();
  await drawer.getByRole('button', { name: 'Prévisualiser la version 1', exact: true }).click();
  await drawer.getByRole('tab', { name: 'Sauvegardes et export', exact: true }).click();
  const downloading = page.waitForEvent('download');
  await drawer.getByRole('button', { name: 'Exporter l’aperçu JSON', exact: true }).click();
  const download = await downloading;
  const stream = await download.createReadStream();
  const chunks: Buffer[] = [];
  for await (const chunk of stream!) chunks.push(Buffer.from(chunk));
  expect(JSON.parse(Buffer.concat(chunks).toString())).toMatchObject({
    id,
    version: 1,
    name: 'Cinéma ancien',
  });
});

test('file restoration requires confirmation and keeps the current flow identity', async ({
  page,
}) => {
  const { drawer, writes } = await setup(page);
  await drawer.getByRole('tab', { name: 'Sauvegardes et export', exact: true }).click();
  await drawer.locator('input[type=file]').setInputFiles({
    name: 'backup.json',
    mimeType: 'application/json',
    buffer: Buffer.from(JSON.stringify({ ...older, id: 'another-flow' })),
  });
  const confirmation = page.getByRole('dialog', {
    name: 'Créer un brouillon depuis cet aperçu ?',
    exact: true,
  });
  await expect(confirmation).toContainText('Cinéma ancien');
  expect(writes).toHaveLength(0);
  await confirmation.getByRole('button', { name: 'Créer le brouillon', exact: true }).click();
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0]).toMatchObject({
    expectedRevision: 7,
    definition: { id, version: 3, name: 'Cinéma ancien' },
  });
});

test('dark theme preview remains readable and navigable', async ({ page }, info) => {
  await page.emulateMedia({ colorScheme: 'dark' });
  const { drawer } = await setup(page);
  await drawer.getByRole('button', { name: 'Tout afficher', exact: true }).click();
  await expect(drawer.locator('.mat-ripple-element')).toHaveCount(0);
  const result = await new AxeBuilder({ page }).include('app-flow-details').analyze();
  expect(result.violations).toEqual([]);
  await expect(drawer.getByRole('link', { name: 'Ouvrir l’éditeur', exact: true })).toBeVisible();
  await page.screenshot({ path: 'test-results/flow-drawer-dark-' + info.project.name + '.png' });
});
