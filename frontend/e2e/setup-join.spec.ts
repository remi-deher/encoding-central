import { expect, test } from '@playwright/test';

test('first-run cluster join authenticates before protected calls and recovers from remote failures', async ({
  page,
}) => {
  const user = { id: 'admin-id', username: 'admin', role: 'admin' };
  const target = {
    id: 'remote-cluster',
    name: 'Cluster Salon',
    apiBaseUrl: 'http://192.168.1.50:8090',
    source: 'manual',
    discoveredAt: '',
  };
  let accountCreated = false;
  let setupCalls = 0;
  let unauthorizedCalls = 0;
  let verificationCalls = 0;
  let pairingCalls = 0;
  let pending = false;
  let approved = false;

  await page.route('**/api/**', (route) => route.fulfill({ json: [] }));
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({ json: { setupRequired: !accountCreated, user: accountCreated ? user : null } }),
  );
  await page.route('**/api/v1/auth/setup/node', (route) =>
    accountCreated
      ? route.fulfill({ status: 404 })
      : route.fulfill({
          json: {
            name: 'worker-garage',
            agentAvailable: true,
            inventory: null,
            tools: [],
            addresses: [],
            urls: [],
          },
        }),
  );
  await page.route('**/api/v1/auth/setup', async (route) => {
    setupCalls++;
    expect(route.request().postDataJSON()).toMatchObject({
      username: 'admin',
      password: 'un-mot-de-passe-long',
      nodeDisplayName: 'Worker Garage',
      mode: 'joinCluster',
    });
    accountCreated = true;
    await route.fulfill({ status: 201, json: user });
  });
  await page.route('**/api/v1/cluster', (route) =>
    route.fulfill({ json: { id: 'local-cluster', name: 'Local', standalone: true } }),
  );
  await page.route('**/api/v1/node/mode', (route) =>
    route.fulfill({ json: { managed: true, server: true, worker: true } }),
  );
  await page.route('**/api/v1/local-node/status', (route) =>
    route.fulfill({
      json: {
        nodeId: 'worker-id',
        name: 'worker-garage',
        displayName: 'Worker Garage',
        roles: ['server', 'worker'],
        maxConcurrentTasks: 1,
        automaticEnrollmentEnabled: false,
        capabilities: [],
        inventory: null,
        isEnrolled: approved,
        clusterId: pending || approved ? target.id : null,
        clusterName: pending || approved ? target.name : null,
        pairingKey: pending ? 'PAIRING' : null,
        pairingExpiresAt: null,
        verificationCode: pending ? 'ABC123' : null,
        switching: null,
        switchFailure: null,
      },
    }),
  );
  await page.route('**/api/v1/local-node/clusters', async (route) => {
    if (route.request().method() === 'GET') return route.fulfill({ json: [] });
    expect(route.request().postDataJSON()).toEqual({ apiBaseUrl: target.apiBaseUrl });
    verificationCalls++;
    await route.fulfill(
      verificationCalls === 1
        ? { status: 503, json: { title: 'Cluster temporairement indisponible.' } }
        : { json: target },
    );
  });
  await page.route('**/api/v1/local-node/pairing-key', async (route) => {
    expect(route.request().postDataJSON()).toMatchObject({
      clusterId: target.id,
      displayName: 'Worker Garage',
    });
    pairingCalls++;
    if (pairingCalls === 1)
      return route.fulfill({ status: 503, json: { title: 'Réessayez la demande.' } });
    pending = true;
    await route.fulfill({
      json: {
        pairingKey: 'PAIRING',
        expiresAt: '2030-01-01T00:00:00Z',
        verificationCode: 'ABC123',
      },
    });
  });
  // Model the real API boundary: these routes are unavailable before administrator setup.
  await page.route('**/api/v1/local-node/**', async (route) => {
    if (!accountCreated) {
      unauthorizedCalls++;
      return route.fulfill({ status: 401 });
    }
    if (route.request().method() === 'POST')
      expect(route.request().headers()['x-encoding-csrf']).toBe('1');
    await route.fallback();
  });

  await page.goto('/setup');
  await page.getByRole('radio', { name: /Rejoindre un cluster/ }).click();
  const verify = page.getByRole('button', { name: 'Vérifier', exact: true });
  await expect(verify).toBeDisabled();
  await page.getByRole('button', { name: 'Créer le compte et continuer' }).click();
  expect(setupCalls).toBe(0);
  await page.getByLabel('Mot de passe', { exact: true }).fill('un-mot-de-passe-long');
  await page.getByLabel('Confirmer le mot de passe').fill('un-mot-de-passe-long');
  await page.getByLabel('Nom de ce nœud').fill('Worker Garage');
  await page.getByRole('button', { name: 'Créer le compte et continuer' }).click();
  await expect(
    page.getByText('Compte administrateur local créé. Vous pouvez rejoindre un cluster.'),
  ).toBeVisible();
  await expect(page).toHaveURL(/\/setup$/);
  await expect(page.getByRole('radio', { name: /Créer un cluster/ })).toBeDisabled();
  await page.getByLabel('Adresse du cluster d’accueil').fill(target.apiBaseUrl);
  await verify.click();
  await expect(page.getByRole('alert')).toContainText('Cluster temporairement indisponible');
  await verify.click();
  const send = page.getByRole('button', { name: 'Envoyer la demande de rattachement' });
  await expect(send).toBeEnabled();
  await send.click();
  await expect(page.getByRole('alert')).toContainText('Réessayez la demande');
  await send.click();
  await expect(page.getByText('ABC123', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Suivre la demande dans Nœuds' })).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Ouvrir l’application', exact: true })).toHaveCount(
    0,
  );
  pending = false;
  approved = true;
  await expect(
    page.getByRole('status').filter({ hasText: 'Ce nœud a rejoint Cluster Salon.' }),
  ).toBeVisible({
    timeout: 12_000,
  });
  await page.getByRole('button', { name: 'Ouvrir l’application', exact: true }).click();
  await expect(page).toHaveURL(/\/$/);
  expect(setupCalls).toBe(1);
  expect(unauthorizedCalls).toBe(0);
  expect(verificationCalls).toBe(2);
  expect(pairingCalls).toBe(2);
});
