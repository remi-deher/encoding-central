import { expect, test } from '@playwright/test';

test('first-run setup no longer asks to choose a role or join a cluster', async ({ page }) => {
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({ json: { setupRequired: true, user: null } }),
  );
  await page.route('**/api/v1/auth/setup/node', (route) =>
    route.fulfill({
      json: {
        name: 'encoding-central',
        displayName: 'Central',
        agentAvailable: true,
        inventory: null,
        tools: [],
        addresses: [],
        urls: ['http://127.0.0.1:5080'],
      },
    }),
  );
  await page.route('**/api/v1/local-node/**', (route) => route.fulfill({ status: 401 }));

  await page.goto('/setup');
  await expect(page.getByRole('heading', { name: 'Bienvenue sur Encoding' })).toBeVisible();
  await expect(page.getByRole('radio')).toHaveCount(0);
  await expect(page.getByText('Rejoindre un cluster')).toHaveCount(0);
  await expect(page.getByText('Créer un cluster')).toHaveCount(0);
  await page.getByLabel('Nom de ce nœud').fill('Central salon');
  await page.getByRole('button', { name: 'Continuer' }).click();
  await expect(page.getByRole('heading', { name: 'Créer l’administrateur' })).toBeVisible();
  await page.getByLabel('Mot de passe', { exact: true }).fill('mot-de-passe-admin');
  await page.getByLabel('Confirmer le mot de passe').fill('mot-de-passe-admin');
  await page.getByRole('button', { name: 'Continuer' }).click();
  await expect(page.getByRole('heading', { name: 'Prêt à ouvrir le central' })).toBeVisible();
});

test('the worker setup links with a central administrator account', async ({ page }) => {
  let setupCalls = 0;
  let linked = false;
  await page.route('**/ui-config.json', (route) => route.fulfill({ json: { shell: 'worker' } }));
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({ json: { setupRequired: !linked, user: null } }),
  );
  await page.route('**/api/v1/auth/setup', (route) => {
    setupCalls++;
    return route.fulfill({ status: 500, json: {} });
  });
  await page.route('**/api/v1/worker/link', async (route) => {
    if (route.request().method() === 'GET') {
      return route.fulfill({
        json: {
          enrolled: false,
          centralName: null,
          centralUrl: null,
          verificationCode: null,
          expiresAt: null,
        },
      });
    }
    const body = route.request().postDataJSON() as {
      apiBaseUrl: string;
      displayName: string;
      username: string;
      password: string;
    };
    linked = true;
    expect(body).toEqual({
      apiBaseUrl: 'http://192.168.1.50:5080',
      displayName: 'Worker salon',
      username: 'admin',
      password: 'mot-de-passe-admin',
    });
    return route.fulfill({
      json: {
        enrolled: true,
        centralName: 'Encoding',
        centralUrl: 'http://192.168.1.50:5080/',
        verificationCode: null,
        expiresAt: null,
      },
    });
  });
  await page.route('**/api/v1/auth/me', (route) => route.fulfill({ status: 401, json: {} }));

  await page.goto('/setup');
  await expect(page.getByRole('heading', { name: 'Nommer ce nœud' })).toBeVisible();
  await expect(page.getByLabel('Mot de passe')).toHaveCount(0);
  await page.getByLabel('Nom de ce nœud').fill('Worker salon');
  await page.getByRole('button', { name: 'Continuer' }).click();
  await expect(page.getByRole('heading', { name: 'Relier ce worker au central' })).toBeVisible();
  await page.getByLabel('Adresse du central').fill('http://192.168.1.50:5080');
  await page.getByLabel('Compte administrateur').fill('admin');
  await page.getByLabel('Mot de passe').fill('mot-de-passe-admin');
  await page.getByRole('button', { name: 'Relier ce worker' }).click();
  await expect(page.getByText('Code à comparer')).toHaveCount(0);
  await expect(page).toHaveURL(/\/login/);
  expect(setupCalls).toBe(0);
});
