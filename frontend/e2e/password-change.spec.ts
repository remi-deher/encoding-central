import { expect, test, type Page } from '@playwright/test';

const current = 'ancien-mot-de-passe';
const next = 'nouveau-mot-de-passe-1';

test.beforeEach(async ({ page }) => {
  await page.route('**/api/**', (route) => route.fulfill({ json: [] }));
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({
      json: { setupRequired: false, user: { id: 'admin-id', username: 'admin', role: 'admin' } },
    }),
  );
});

async function openDialog(page: Page) {
  await page.goto('/');
  const account = page.getByRole('button', { name: /Compte de admin/ });
  if (!(await account.isVisible().catch(() => false))) {
    await page.getByRole('button', { name: 'Plus', exact: true }).click();
  }
  await account.click();
  await page.getByRole('menuitem', { name: 'Changer le mot de passe' }).click();
  const dialog = page.getByRole('dialog', { name: 'Changer le mot de passe' });
  await expect(dialog).toBeVisible();
  // Material moves focus after the opening animation. Visibility alone allows fill()
  // to race that focus transfer and send confirmation text to the first input on mobile.
  await expect(dialog.getByLabel('Mot de passe actuel', { exact: true })).toBeFocused();
  return dialog;
}

test('changes the password with the three fields and closes the dialog', async ({ page }) => {
  let body: unknown;
  let csrf: string | undefined;
  await page.route('**/api/v1/auth/password', async (route) => {
    body = route.request().postDataJSON();
    csrf = route.request().headers()['x-encoding-csrf'];
    await route.fulfill({ status: 204 });
  });
  const dialog = await openDialog(page);
  await expect(
    dialog.getByLabel('Confirmer le nouveau mot de passe', { exact: true }),
  ).toBeVisible();
  await dialog.getByLabel('Mot de passe actuel', { exact: true }).fill(current);
  await dialog.getByLabel('Nouveau mot de passe', { exact: true }).fill(next);
  await dialog.getByLabel('Confirmer le nouveau mot de passe', { exact: true }).fill(next);
  await dialog.getByRole('button', { name: 'Changer le mot de passe' }).click();
  await expect(dialog).toHaveCount(0);
  expect(body).toEqual({ currentPassword: current, newPassword: next });
  expect(csrf).toBe('1');
  await expect(page.getByText('Votre mot de passe a été modifié.')).toBeVisible();
});

test('validates inline without calling the API', async ({ page }) => {
  let calls = 0;
  await page.route('**/api/v1/auth/password', async (route) => {
    calls++;
    await route.fulfill({ status: 204 });
  });
  const dialog = await openDialog(page);
  await dialog.getByRole('button', { name: 'Changer le mot de passe' }).click();
  await expect(dialog.getByText('Ce champ est obligatoire.')).toHaveCount(3);
  await dialog.getByLabel('Mot de passe actuel', { exact: true }).fill(current);
  await dialog.getByLabel('Nouveau mot de passe', { exact: true }).fill('court');
  await dialog.getByLabel('Confirmer le nouveau mot de passe', { exact: true }).fill('autre');
  await dialog.getByRole('button', { name: 'Changer le mot de passe' }).click();
  await expect(dialog.getByText('au moins 12 caractères.', { exact: false }).first()).toBeVisible();
  await expect(dialog.getByText('Les deux mots de passe ne correspondent pas.')).toBeVisible();
  await dialog.getByLabel('Nouveau mot de passe', { exact: true }).fill(current);
  await dialog.getByLabel('Confirmer le nouveau mot de passe', { exact: true }).fill(current);
  await expect(
    dialog.getByText('Le nouveau mot de passe doit être différent de l’actuel.'),
  ).toBeVisible();
  expect(calls).toBe(0);
});

test('shows the API refusal under the current password and keeps the dialog open', async ({
  page,
}) => {
  await page.route('**/api/v1/auth/password', (route) =>
    route.fulfill({
      status: 400,
      contentType: 'application/problem+json',
      json: { errors: { currentPassword: ['Le mot de passe actuel est incorrect.'] } },
    }),
  );
  const dialog = await openDialog(page);
  await dialog.getByLabel('Mot de passe actuel', { exact: true }).fill('faux');
  await dialog.getByLabel('Nouveau mot de passe', { exact: true }).fill(next);
  await dialog.getByLabel('Confirmer le nouveau mot de passe', { exact: true }).fill(next);
  await dialog.getByRole('button', { name: 'Changer le mot de passe' }).click();
  await expect(dialog.getByText('Le mot de passe actuel est incorrect.')).toBeVisible();
  await expect(dialog.getByLabel('Mot de passe actuel', { exact: true })).toBeEnabled();
});

test('reports an expired session inside the dialog', async ({ page }) => {
  await page.route('**/api/v1/auth/password', (route) => route.fulfill({ status: 401 }));
  const dialog = await openDialog(page);
  await dialog.getByLabel('Mot de passe actuel', { exact: true }).fill(current);
  await dialog.getByLabel('Nouveau mot de passe', { exact: true }).fill(next);
  await dialog.getByLabel('Confirmer le nouveau mot de passe', { exact: true }).fill(next);
  await dialog.getByRole('button', { name: 'Changer le mot de passe' }).click();
  await expect(dialog.getByRole('alert')).toContainText('Votre session a expiré');
});

test('toggles password visibility per field', async ({ page }) => {
  const dialog = await openDialog(page);
  const field = dialog.getByLabel('Nouveau mot de passe', { exact: true });
  await expect(field).toHaveAttribute('type', 'password');
  await dialog.getByRole('button', { name: 'Afficher le nouveau mot de passe' }).click();
  await expect(field).toHaveAttribute('type', 'text');
  await dialog.getByRole('button', { name: 'Masquer le nouveau mot de passe' }).click();
  await expect(field).toHaveAttribute('type', 'password');
  await expect(dialog.getByLabel('Mot de passe actuel', { exact: true })).toHaveAttribute(
    'type',
    'password',
  );
});

test('shows the server explanation when there is no account session', async ({ page }) => {
  await page.route('**/api/v1/auth/password', (route) =>
    route.fulfill({
      status: 401,
      json: { title: 'Aucune session de compte : connectez-vous avec votre compte.' },
    }),
  );
  const dialog = await openDialog(page);
  await dialog.getByLabel('Mot de passe actuel', { exact: true }).fill(current);
  await dialog.getByLabel('Nouveau mot de passe', { exact: true }).fill(next);
  await dialog.getByLabel('Confirmer le nouveau mot de passe', { exact: true }).fill(next);
  await expect(dialog.getByLabel('Mot de passe actuel', { exact: true })).toHaveValue(current);
  await expect(dialog.getByLabel('Nouveau mot de passe', { exact: true })).toHaveValue(next);
  await expect(dialog.getByLabel('Confirmer le nouveau mot de passe', { exact: true })).toHaveValue(
    next,
  );
  const [response] = await Promise.all([
    page.waitForResponse('**/api/v1/auth/password'),
    dialog.getByRole('button', { name: 'Changer le mot de passe' }).click(),
  ]);
  expect(response.status()).toBe(401);
  await expect(dialog.getByRole('alert')).toContainText('Aucune session de compte');
});

test('an administrator resets another account with the same dialog', async ({ page }) => {
  const users = [
    {
      id: 'admin-id',
      username: 'admin',
      role: 'admin',
      isActive: true,
      createdAt: '2026-10-18T08:00:00Z',
    },
    {
      id: 'viewer-id',
      username: 'lecteur',
      role: 'viewer',
      isActive: true,
      createdAt: '2026-10-18T08:00:00Z',
    },
  ];
  await page.route('**/api/v1/users', (route) => route.fulfill({ json: users }));
  let reset: { url: string; body: unknown } | undefined;
  await page.route('**/api/v1/users/viewer-id/password', async (route) => {
    reset = { url: route.request().url(), body: route.request().postDataJSON() };
    await route.fulfill({ status: 204 });
  });
  await page.goto('/settings?tab=access');
  await page.getByRole('button', { name: 'Changer le mot de passe de lecteur' }).click();
  const dialog = page.getByRole('dialog', { name: 'Réinitialiser le mot de passe' });
  await expect(dialog.getByLabel('Mot de passe actuel', { exact: true })).toHaveCount(0);
  await expect(dialog.getByLabel('Nouveau mot de passe', { exact: true })).toBeFocused();
  await dialog.getByLabel('Nouveau mot de passe', { exact: true }).fill('court');
  await dialog.getByLabel('Confirmer le nouveau mot de passe', { exact: true }).fill('court');
  await dialog.getByRole('button', { name: 'Réinitialiser' }).click();
  await expect(dialog.getByText('au moins 12 caractères.', { exact: false }).first()).toBeVisible();
  await dialog.getByLabel('Nouveau mot de passe', { exact: true }).fill(next);
  await dialog.getByLabel('Confirmer le nouveau mot de passe', { exact: true }).fill(next);
  await dialog.getByRole('button', { name: 'Réinitialiser' }).click();
  await expect(dialog).toHaveCount(0);
  expect(reset?.body).toEqual({ newPassword: next });

  // Sur son propre compte, l'action ouvre le changement avec le mot de passe actuel.
  await page.getByRole('button', { name: 'Changer le mot de passe de admin' }).click();
  await expect(
    page
      .getByRole('dialog', { name: 'Changer le mot de passe' })
      .getByLabel('Mot de passe actuel', { exact: true }),
  ).toBeVisible();
});
