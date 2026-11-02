import { expect, test } from '@playwright/test';

test('guided choices create an editable draft and preserve choices after failure', async ({
  page,
}, testInfo) => {
  await page.route('**/api/**', (route) => route.fulfill({ json: [] }));
  await page.route('**/api/v1/flows/components', (route) =>
    route.fulfill({
      json: [
        {
          id: 'video',
          version: '1.3.0',
          components: [
            {
              id: 'video-transcode',
              configurationSchema: {
                version: 1,
                fields: [
                  { id: 'videoCodec', options: [{ value: 'hevc', label: 'HEVC / H.265' }] },
                  { id: 'qualityTier', options: [{ value: 'balanced', label: 'Équilibrée' }] },
                ],
              },
            },
          ],
        },
      ],
    }),
  );
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({
      json: {
        setupRequired: false,
        user: { id: 'admin', username: 'admin', role: 'admin' },
      },
    }),
  );
  let requests = 0;
  let definition: Record<string, unknown>;
  await page.route('**/api/v1/flows/guided', async (route) => {
    const request = route.request().postDataJSON();
    expect(request).toMatchObject({
      name: 'Premier flow',
      codec: 'hevc',
      encoder: 'automatic',
      quality: 'balanced',
      audio: 'keep',
      retryWithCpu: false,
    });
    if (++requests === 1) return route.fulfill({ status: 422, json: { issues: [] } });
    definition = {
      id: request.id,
      formatVersion: 1,
      version: 1,
      name: request.name,
      mediaType: 'video',
      nodes: [],
      connections: [],
    };
    return route.fulfill({ json: definition });
  });
  let drafts = 0;
  await page.route('**/api/v1/flows/*/draft', async (route) => {
    expect(route.request().postDataJSON()).toEqual({ expectedRevision: 0, definition });
    drafts++;
    return route.fulfill({ json: { definition, draftRevision: 1 } });
  });
  await page.goto('/flows/guided?name=Premier%20flow');
  await expect(page.getByRole('textbox', { name: 'Nom du flow' })).toHaveValue('Premier flow');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
  await page.screenshot({ path: testInfo.outputPath('guided-general.png'), fullPage: true });
  await page
    .locator('form')
    .filter({ has: page.getByRole('textbox', { name: 'Nom du flow' }) })
    .getByRole('button', { name: 'Continuer', exact: true })
    .click();
  await expect(page.getByRole('combobox', { name: 'Choix de l’encodeur' })).toBeVisible();
  await page
    .locator('form')
    .filter({ has: page.getByRole('combobox', { name: 'Choix de l’encodeur' }) })
    .getByRole('button', { name: 'Continuer', exact: true })
    .click();
  await expect(
    page.getByText('Les sous-titres et les pistes non modifiées sont conservés.', { exact: false }),
  ).toBeVisible();
  await page
    .locator('form')
    .filter({ has: page.getByRole('combobox', { name: 'Pistes audio' }) })
    .getByRole('button', { name: 'Continuer', exact: true })
    .click();
  await expect(page.getByText('Ce parcours crée un brouillon', { exact: false })).toBeVisible();
  await page.getByRole('button', { name: 'Vérifier', exact: true }).click();
  const create = page.getByRole('button', { name: 'Créer le brouillon et ouvrir l’éditeur' });
  await create.click();
  await expect(
    page.getByText('Impossible de créer le brouillon guidé. Vos choix sont conservés.'),
  ).toBeVisible();
  await expect(create).toBeEnabled();
  expect(drafts).toBe(0);
  await create.click();
  await expect(page).toHaveURL(/\/flows\/[0-9a-f-]+\/edit$/);
  expect(drafts).toBe(1);
});
