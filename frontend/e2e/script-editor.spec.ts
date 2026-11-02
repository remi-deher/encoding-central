import { expect, test } from '@playwright/test';

test.beforeEach(async ({ page }) => {
  await page.route('**/api/**', (route) => route.fulfill({ json: [] }));
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({
      json: {
        setupRequired: false,
        user: { id: 'admin', username: 'admin', role: 'admin' },
      },
    }),
  );
});

test('script code remains in the accessible editor after cancelled close, Escape and backdrop dismissal', async ({
  page,
}) => {
  await page.goto('/libraries/scripts');
  await page.getByRole('button', { name: 'Créer le premier script' }).click();
  await page.getByRole('textbox', { name: 'Nom du script', exact: true }).fill('Mon script');
  const editor = page.getByRole('textbox', { name: 'Code de Mon script', exact: true });
  await editor.fill('console.log("brouillon");');
  const drawer = page.locator('app-detail-drawer mat-drawer');
  await expect(drawer.locator('.drawer-footer')).toContainText('Enregistrer le brouillon');
  await expect
    .poll(() =>
      drawer.locator('.drawer-footer').evaluate((element) => {
        const bounds = element.getBoundingClientRect();
        return {
          top: Math.floor(bounds.top),
          bottom: Math.ceil(bounds.bottom),
          viewport: innerHeight,
        };
      }),
    )
    .toMatchObject({ bottom: page.viewportSize()!.height, viewport: page.viewportSize()!.height });
  for (const dismissal of ['close', 'escape', 'backdrop']) {
    if (dismissal === 'close')
      await drawer.getByRole('button', { name: 'Fermer le panneau', exact: true }).click();
    if (dismissal === 'escape') {
      await editor.focus();
      await page.keyboard.press('Escape');
    }
    if (dismissal === 'backdrop')
      await page
        .locator('.mat-drawer-backdrop.mat-drawer-shown')
        .click({ position: { x: 5, y: 5 } });
    const dialog = page.getByRole('dialog', { name: 'Abandonner les modifications ?' });
    await expect(dialog).toBeVisible();
    await expect(drawer).toHaveClass(/mat-drawer-opened/);
    await dialog.getByRole('button', { name: 'Annuler', exact: true }).click();
    await expect(editor).toHaveText('console.log("brouillon");');
  }
  await drawer.getByRole('button', { name: 'Fermer le panneau', exact: true }).click();
  await page
    .getByRole('dialog', { name: 'Abandonner les modifications ?' })
    .getByRole('button', { name: 'Abandonner', exact: true })
    .click();
  await expect(drawer).not.toHaveClass(/mat-drawer-opened/);
});

test('browser back asks before losing a script draft', async ({ page }) => {
  await page.goto('/');
  await page.keyboard.press('Control+k');
  const search = page.getByRole('combobox', { name: /Rechercher dans/ });
  await search.fill('Scripts');
  await search.press('Enter');
  await page.getByRole('button', { name: 'Créer le premier script' }).click();
  await page.getByRole('textbox', { name: 'Nom du script', exact: true }).fill('À conserver');
  await page.goBack();
  const dialog = page.getByRole('dialog', { name: 'Quitter sans enregistrer ?' });
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: 'Annuler', exact: true }).click();
  await expect(page).toHaveURL(/libraries\/scripts/);
  await expect(page.getByRole('textbox', { name: 'Nom du script', exact: true })).toHaveValue(
    'À conserver',
  );
});
