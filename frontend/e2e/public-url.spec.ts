import { expect, test } from '@playwright/test';

test('the public URL replaces the browser address in Connexions and is validated', async ({
  page,
}, testInfo) => {
  let stored: { trustProxy: boolean; proxies: string[]; publicUrl: string | null } = {
    trustProxy: false,
    proxies: [],
    publicUrl: null,
  };
  await page.route('**/api/**', (route) => route.fulfill({ json: [] }));
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({
      json: { setupRequired: false, user: { id: 'admin-id', username: 'admin', role: 'admin' } },
    }),
  );
  await page.route('**/api/v1/network/settings', async (route) => {
    if (route.request().method() === 'PUT') {
      const body = route.request().postDataJSON();
      if (body.publicUrl === 'encoding.local')
        return route.fulfill({
          status: 400,
          json: { errors: { publicUrl: ['Indiquez une adresse complète.'] } },
        });
      stored = body;
    }
    return route.fulfill({ json: stored });
  });

  await page.goto('/settings?tab=network');
  const field = page.getByLabel('URL publique', { exact: true });
  await field.fill('encoding.local');
  await page.getByRole('button', { name: 'Enregistrer' }).click();
  await expect(page.getByText('Indiquez une adresse complète.')).toBeVisible();

  await field.fill('https://encoding.example.com/');
  await page.getByRole('button', { name: 'Enregistrer' }).click();
  await expect(page.getByText('Réglage réseau enregistré.')).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath('network.png'), fullPage: true });

  await page.getByRole('tab', { name: 'Connexions', exact: true }).click();
  await expect(page.getByText('https://encoding.example.com/metrics')).toBeVisible();
  await expect(
    page.getByText('https://encoding.example.com/api/v1/integrations/sonarr/webhook'),
  ).toBeVisible();
});
