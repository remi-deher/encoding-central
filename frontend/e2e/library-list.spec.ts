import { expect, test } from '@playwright/test';

const libraries = [
  {
    id: 'lib-films',
    name: 'Films',
    createdAt: '2026-10-18T10:00:00Z',
    configuredNodeCount: 2,
    directAccessNodeCount: 1,
    transferNodeCount: 1,
    isActive: true,
    flowName: 'V3 HEVC',
    flowVersion: 3,
  },
  {
    id: 'lib-series',
    name: 'Séries',
    createdAt: '2026-10-18T10:00:00Z',
    configuredNodeCount: 0,
    directAccessNodeCount: 0,
    transferNodeCount: 0,
    isActive: false,
  },
];

test.beforeEach(async ({ page }) => {
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/v1/libraries', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(libraries),
    }),
  );
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        setupRequired: false,
        user: { id: '00000000-0000-0000-0000-000000000001', username: 'admin', role: 'admin' },
      }),
    }),
  );
});

test('the library list shows one row per library with state tags and actions', async ({ page }) => {
  await page.goto('/libraries');
  const rows = page.locator('.resource-item');
  await expect(rows).toHaveCount(2);
  await expect(rows.nth(0)).toContainText('Films');
  await expect(rows.nth(1)).toContainText('Aucun nœud n’a accès');
  await expect(rows.nth(1)).toContainText('Désactivée');
  await expect(page.getByRole('button', { name: 'Plus d’actions pour Films' })).toBeVisible();
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  );
  expect(overflow).toBeLessThanOrEqual(0);
});

test('deactivating a library asks for confirmation first', async ({ page }) => {
  let activationCalls = 0;
  await page.route('**/api/v1/libraries/lib-films/activation', (route) => {
    activationCalls += 1;
    return route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ ...libraries[0], isActive: false }),
    });
  });
  await page.goto('/libraries');
  await page.getByRole('switch', { name: 'Activer la bibliothèque Films' }).click();
  await expect(
    page.getByRole('heading', { name: 'Désactiver cette bibliothèque ?' }),
  ).toBeVisible();
  expect(activationCalls).toBe(0);
  await page.getByRole('button', { name: 'Désactiver' }).click();
  await expect.poll(() => activationCalls).toBe(1);
});
