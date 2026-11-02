import { expect, Locator, Page, Route, test } from '@playwright/test';

const id = '11111111-1111-4111-8111-111111111111';
const summary = {
  id,
  name: 'Encodage cinéma',
  mediaType: 'video',
  version: 1,
  isDraft: false,
  nodeCount: 3,
  backupStatus: 'ready',
  updatedAt: '2026-10-21T00:00:00Z',
};

async function setup(
  page: Page,
  options: {
    role?: string;
    draft?: boolean;
    failure?: string;
    libraries?: readonly {
      libraryId: string;
      libraryName: string;
      version: number;
      isActive: boolean;
    }[];
  } = {},
) {
  let deleted = false,
    requests = 0,
    lastUrl = '';
  await page.route('**/api/**', (route) => route.fulfill({ json: [] }));
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({
      json: {
        setupRequired: false,
        user: { id: 'test-user', username: 'test-user', role: options.role ?? 'admin' },
      },
    }),
  );
  await page.route('**/api/v1/flows', (route) =>
    route.fulfill({
      json: deleted
        ? []
        : [
            {
              ...summary,
              isDraft: options.draft ?? false,
              version: options.draft ? 0 : 1,
            },
          ],
    }),
  );
  await page.route('**/api/v1/flows/' + id + '/usage', (route) =>
    route.fulfill({ json: options.libraries ?? [] }),
  );
  await page.route('**/api/v1/flows/' + id + '?*', (route) => deleteFlow(route));
  await page.route('**/api/v1/flows/' + id, (route) => deleteFlow(route));
  async function deleteFlow(route: Route) {
    if (route.request().method() !== 'DELETE') return route.fulfill({ status: 405 });
    requests++;
    lastUrl = route.request().url();
    if (options.failure) return route.fulfill({ status: 409, json: { title: options.failure } });
    deleted = true;
    return route.fulfill({ status: 204 });
  }
  await page.route('**/api/v1/flows/' + id + '/edit', (r) =>
    r.fulfill({
      json: {
        definition: {
          formatVersion: 1,
          id,
          name: summary.name,
          mediaType: 'video',
          version: 1,
          nodes: [],
          connections: [],
        },
        backupStatus: 'ready',
        createdAt: summary.updatedAt,
      },
    }),
  );
  await page.route('**/api/v1/flows/' + id + '/preflight', (r) =>
    r.fulfill({
      json: {
        graph: { valid: true, issues: [] },
        execution: {
          workerCompatible: false,
          compatibleWorkers: [],
          observedAt: summary.updatedAt,
        },
      },
    }),
  );
  await page.goto('/flows');
  await expect(
    page.getByRole('button', { name: 'Plus d’actions pour Encodage cinéma' }),
  ).toBeVisible();
  return { requests: () => requests, lastUrl: () => lastUrl };
}

async function confirmDeletion(dialog: Locator) {
  const confirm = dialog.getByRole('button', { name: 'Supprimer', exact: true });
  await expect(confirm).toBeDisabled();
  await dialog.getByRole('checkbox').check();
  await confirm.click();
}

async function requestDeletion(page: Page) {
  await page.getByRole('button', { name: 'Plus d’actions pour Encodage cinéma' }).click();
  await page.getByRole('menuitem', { name: 'Supprimer', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('Supprimer le flow « Encodage cinéma » ?');
  return dialog;
}

test('canceling flow deletion sends no request and keeps the flow', async ({ page }) => {
  const state = await setup(page);
  const dialog = await requestDeletion(page);
  await dialog.getByRole('button', { name: 'Annuler', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(state.requests()).toBe(0);
  await expect(
    page.getByRole('button', { name: 'Plus d’actions pour Encodage cinéma' }),
  ).toBeVisible();
});

for (const draft of [false, true]) {
  test(`deleting a ${draft ? 'draft' : 'published'} flow refreshes the catalogue immediately`, async ({
    page,
  }) => {
    const state = await setup(page, { draft });
    const dialog = await requestDeletion(page);
    await confirmDeletion(dialog);
    await expect(
      page.getByRole('button', { name: 'Plus d’actions pour Encodage cinéma' }),
    ).toHaveCount(0);
    await expect(
      page.getByText('Le flow « Encodage cinéma » a été supprimé.', { exact: true }),
    ).toBeVisible();
    expect(state.requests()).toBe(1);
  });
}

for (const failure of ['Des exécutions sont actuellement en cours pour ce flow.']) {
  test(`deletion preserves the flow and explains the server refusal: ${failure}`, async ({
    page,
  }) => {
    const state = await setup(page, { failure });
    const dialog = await requestDeletion(page);
    await confirmDeletion(dialog);
    await expect(page.getByText(failure, { exact: true })).toBeVisible();
    await expect(
      page.getByRole('button', { name: 'Plus d’actions pour Encodage cinéma' }),
    ).toBeVisible();
    expect(state.requests()).toBe(1);
  });
}

test('deleting a flow used by libraries lists them and detaches them', async ({ page }) => {
  const state = await setup(page, {
    libraries: [{ libraryId: 'l1', libraryName: 'Cinéma', version: 1, isActive: true }],
  });
  const dialog = await requestDeletion(page);
  await expect(dialog.getByRole('row', { name: /Cinéma/ })).toBeVisible();
  await confirmDeletion(dialog);
  await expect.poll(state.requests).toBe(1);
  expect(state.lastUrl()).toContain('detachLibraries=true');
});

test('deleting from the detail header closes the obsolete detail', async ({ page }) => {
  const state = await setup(page);
  await page.getByRole('button', { name: 'Consulter', exact: true }).click();
  const detail = page.getByRole('dialog', { name: 'Encodage cinéma', exact: true });
  await detail.getByRole('button', { name: 'Actions du flow', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Supprimer le flow', exact: true }).click();
  await confirmDeletion(
    page.getByRole('dialog', { name: 'Supprimer le flow « Encodage cinéma » ?', exact: true }),
  );
  await expect.poll(state.requests).toBe(1);
  await expect(
    page.getByText('Le flow « Encodage cinéma » a été supprimé.', { exact: true }),
  ).toBeVisible();
  await expect(detail).toBeHidden();
  await expect(
    page.getByRole('button', { name: 'Plus d’actions pour Encodage cinéma' }),
  ).toHaveCount(0);
  expect(state.requests()).toBe(1);
});

test('viewers can inspect flows but have no deletion action', async ({ page }) => {
  const state = await setup(page, { role: 'viewer' });
  await page.getByRole('button', { name: 'Plus d’actions pour Encodage cinéma' }).click();
  await expect(page.getByRole('menuitem', { name: 'Supprimer', exact: true })).toHaveCount(0);
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Consulter', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Actions du flow', exact: true })).toHaveCount(0);
  expect(state.requests()).toBe(0);
});
