import assert from 'node:assert/strict';
import test from 'node:test';
import { loadModel } from './model-loader.mjs';
const { localNodeSheet, formatLastSignal, clusterJoinSteps, reconciliationBannerPresentation } =
  await loadModel('node-management');
const { formatNodeLabel } = await loadModel('cluster');

const NOW = Date.parse('2026-10-15T12:00:00Z');
const status = {
  nodeId: 'n1',
  name: 'nas',
  roles: ['server', 'worker'],
  maxConcurrentTasks: 2,
  capabilities: [
    {
      toolId: 'ffmpeg',
      version: '7.1',
      mediaTypes: [],
      accelerationTypes: ['vaapi'],
      encoderChecks: [
        { encoderId: 'libx265', status: 'usable' },
        { encoderId: 'hevc_vaapi', status: 'detected' },
      ],
    },
  ],
  clusterName: 'Maison',
  switchFailure: null,
};
const node = {
  id: 'n1',
  displayName: 'NAS principal',
  lastSeenAt: '2026-10-15T11:59:56Z',
  desiredRoles: null,
  inventory: {
    operatingSystem: 'Linux',
    architecture: 'X64',
    processorCount: 16,
    memoryMb: 32768,
    processorModel: 'AMD Ryzen 7 5700G',
    gpus: [{ name: 'AMD 0x1638', driver: 'amdgpu', device: 'card0' }],
  },
};
const base = {
  status,
  node,
  mode: { server: true, worker: true },
  standalone: false,
  clusterName: 'Maison',
  agentUnavailable: false,
  activeTasks: 1,
  now: NOW,
};
const section = (sheet, id) => sheet.sections.find((s) => s.id === id);

test('a healthy member shows load, hardware, CPU model, GPU and tools', () => {
  const sheet = localNodeSheet(base);
  assert.equal(sheet.title, 'NAS principal');
  assert.equal(sheet.subtitle, 'Membre de Maison');
  assert.equal(sheet.notices.length, 0);
  const [load, signal] = section(sheet, 'state').metrics;
  assert.equal(load.value, '1 / 2');
  assert.equal(load.meter.stale, false);
  assert.equal(signal.value, 'À l’instant');
  const [cpu, gpu] = section(sheet, 'hardware').metrics;
  assert.equal(cpu.value, 'AMD Ryzen 7 5700G');
  assert.match(cpu.detail, /32 Go/);
  assert.match(gpu.detail, /pilote amdgpu/);
  const motor = section(sheet, 'capabilities').items[0];
  assert.equal(motor.label, 'Moteur d’accélération');
  assert.deepEqual(
    motor.badges.map((b) => b.label),
    ['VAAPI', 'AMD 0x1638'],
  );
});

test('a disabled worker has no processing metric and no tools', () => {
  const sheet = localNodeSheet({
    ...base,
    mode: { server: true, worker: false },
    activeTasks: null,
  });
  assert.equal(section(sheet, 'state').metrics[0].value, 'Non applicable');
  assert.equal(section(sheet, 'capabilities').items.length, 0);
});

test('an unreachable agent keeps last known values and never shows zero', () => {
  const sheet = localNodeSheet({
    ...base,
    agentUnavailable: true,
    activeTasks: null,
    now: NOW + 14 * 60_000,
  });
  assert.equal(sheet.notices[0].tone, 'danger');
  assert.match(section(sheet, 'state').items[0].badges[0].label, /Dernier état connu/);
  assert.equal(section(sheet, 'state').metrics[0].value, 'Indisponible');
  const stale = localNodeSheet({ ...base, agentUnavailable: true });
  assert.equal(section(stale, 'state').metrics[0].meter.stale, true);
});

test('a pending role change is shown apart from the applied roles', () => {
  const sheet = localNodeSheet({ ...base, node: { ...node, desiredRoles: ['server'] } });
  assert.equal(sheet.notices[0].title, 'Changement de fonctions en attente');
  const worker = section(sheet, 'state').items[1].badges.map((b) => b.label);
  assert.deepEqual(worker, ['Actif', 'Demandé : inactif']);
});

test('an old agent without GPU data says so instead of claiming none', () => {
  const old = { ...node, inventory: { ...node.inventory, processorModel: null, gpus: null } };
  const [cpu, gpu] = section(localNodeSheet({ ...base, node: old }), 'hardware').metrics;
  assert.equal(cpu.value, '16 cœurs');
  assert.equal(gpu.value, 'Non déclaré');
});

test('a standalone node names no cluster and an invalid date stays unknown', () => {
  const sheet = localNodeSheet({ ...base, standalone: true, clusterName: null });
  assert.equal(sheet.subtitle, 'Nœud autonome');
  assert.equal(formatLastSignal('n’importe quoi'), null);
});

test('a member without a local server still shows its technical information from the agent', () => {
  const agentStatus = { ...status, inventory: node.inventory };
  const sheet = localNodeSheet({ ...base, status: agentStatus, node: null, activeTasks: null });
  const [cpu, gpu] = section(sheet, 'hardware').metrics;
  assert.equal(cpu.value, 'AMD Ryzen 7 5700G');
  assert.match(gpu.value, /AMD/);
  assert.equal(section(sheet, 'capabilities').items[0].label, 'Moteur d’accélération');
  const [load, signal] = section(sheet, 'state').metrics;
  assert.equal(load.value, 'Indisponible');
  assert.match(signal.detail, /serveur du cluster/);
});

test('cluster join step waiting highlights the verification code as heroCode', () => {
  const steps = clusterJoinSteps({
    addressForm: { title: '', description: '', fields: [] },
    target: { id: 'c1', name: 'Maison', apiBaseUrl: 'http://127.0.0.1:8080' },
    status: { name: 'encoding-50', verificationCode: 'R7EPDH', pairingKey: 'KEY123' },
    pairing: {
      verificationCode: 'R7EPDH',
      expiresAt: '2026-10-17T16:08:00Z',
      pairingKey: 'KEY123',
    },
    clusters: [],
    localServerActive: true,
    canChoose: false,
  });
  const waiting = steps.find((s) => s.id === 'waiting');
  assert.ok(waiting);
  assert.equal(waiting.heroCode?.code, 'R7EPDH');
  assert.equal(waiting.heroCode?.badge, 'En attente d’approbation');
});

test('reconciliationBannerPresentation returns Serveur unique when single server and 0 peers', () => {
  const banner = reconciliationBannerPresentation(
    {
      enabled: true,
      disabledReason: null,
      discoveryAvailable: true,
      discoveryError: null,
      peers: [],
    },
    1,
    true,
  );
  assert.equal(banner.title, 'Serveur unique');
  assert.match(banner.description, /Ce cluster ne comporte qu’un seul serveur/);
});

test('reconciliationBannerPresentation warns about mDNS when multiple servers and 0 peers discovered', () => {
  const banner = reconciliationBannerPresentation(
    {
      enabled: true,
      disabledReason: null,
      discoveryAvailable: true,
      discoveryError: null,
      peers: [],
    },
    2,
    true,
  );
  assert.equal(banner.title, 'Aucun serveur pair détecté');
  assert.match(banner.description, /Aucun pair n’a été découvert/);
});

test('reconciliationBannerPresentation displays active status when peers are connected', () => {
  const banner = reconciliationBannerPresentation(
    {
      enabled: true,
      disabledReason: null,
      discoveryAvailable: true,
      discoveryError: null,
      peers: [
        {
          url: 'http://node2:5080/',
          configured: false,
          discovered: true,
          lastAttemptAt: '2026-10-17T16:00:00Z',
          lastSuccessAt: '2026-10-17T16:00:00Z',
          lastError: null,
        },
      ],
    },
    2,
    true,
  );
  assert.match(banner.title, /Synchronisation active avec 1 serveur/);
});

test('formatNodeLabel formats displayName (hostname) when distinct', () => {
  assert.equal(formatNodeLabel('ENCODING-50', 'TRUENAS'), 'TRUENAS (ENCODING-50)');
});

test('formatNodeLabel returns single name when identical or case-insensitive match', () => {
  assert.equal(formatNodeLabel('TRUENAS', 'TRUENAS'), 'TRUENAS');
  assert.equal(formatNodeLabel('truenas', 'TRUENAS'), 'TRUENAS');
});

test('formatNodeLabel falls back gracefully when either name is missing', () => {
  assert.equal(formatNodeLabel('ENCODING-50', null), 'ENCODING-50');
  assert.equal(formatNodeLabel('', 'TRUENAS'), 'TRUENAS');
  assert.equal(formatNodeLabel('', ''), 'Nœud inconnu');
});

test('isolated task owner preserves computation and shows deferred source writes', () => {
  const banner = reconciliationBannerPresentation(null, 2, true, {
    isolated: true,
    waitingSourceWrites: 3,
    pendingTransfers: 0,
    observations: [],
  });
  assert.match(banner.title, /isolé/);
  assert.match(banner.description, /3 opération/);
  assert.match(banner.description, /ne peut pas être repris automatiquement/);
  assert.equal(banner.isAlert, true);
});

test('a received transfer is shown as inactive until activation', () => {
  const banner = reconciliationBannerPresentation(null, 2, true, {
    isolated: false,
    waitingSourceWrites: 0,
    pendingTransfers: 1,
    observations: [],
  });
  assert.match(banner.description, /réception inactive/);
  assert.match(banner.description, /avant son activation/);
});

test('a neutralized local server does not appear ready to resume', () => {
  const banner = reconciliationBannerPresentation(null, 2, true, {
    isolated: false,
    neutralized: true,
    waitingSourceWrites: 0,
    pendingTransfers: 0,
    observations: [],
  });
  assert.match(banner.title, /neutralisé/);
  assert.match(banner.description, /réadmis/);
  assert.equal(banner.isAlert, true);
});
