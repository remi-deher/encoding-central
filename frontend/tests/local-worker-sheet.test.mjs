import assert from 'node:assert/strict';
import test from 'node:test';
import { loadModel } from './model-loader.mjs';
const { localWorkerSheet, formatLastSignal, centralJoinSteps } =
  await loadModel('worker-management');
const { formatWorkerLabel } = await loadModel('central');

const NOW = Date.parse('2026-10-15T12:00:00Z');
const status = {
  workerId: 'n1',
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
  centralName: 'Maison',
  enrollmentFailure: null,
};
const node = {
  id: 'n1',
  displayName: 'NAS principal',
  lastSeenAt: '2026-10-15T11:59:56Z',
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
  centralName: 'Maison',
  agentUnavailable: false,
  activeTasks: 1,
  now: NOW,
};
const section = (sheet, id) => sheet.sections.find((s) => s.id === id);

test('a healthy member shows load, hardware, CPU model, GPU and tools', () => {
  const sheet = localWorkerSheet(base);
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
  const sheet = localWorkerSheet({
    ...base,
    status: { ...status, roles: ['server'] },
    activeTasks: null,
  });
  assert.equal(section(sheet, 'state').metrics[0].value, 'Non applicable');
  assert.equal(section(sheet, 'capabilities').items.length, 0);
});

test('an unreachable agent keeps last known values and never shows zero', () => {
  const sheet = localWorkerSheet({
    ...base,
    agentUnavailable: true,
    activeTasks: null,
    now: NOW + 14 * 60_000,
  });
  assert.equal(sheet.notices[0].tone, 'danger');
  assert.match(section(sheet, 'state').items[0].badges[0].label, /Dernier état connu/);
  assert.equal(section(sheet, 'state').metrics[0].value, 'Indisponible');
  const stale = localWorkerSheet({ ...base, agentUnavailable: true });
  assert.equal(section(stale, 'state').metrics[0].meter.stale, true);
});

test('an old agent without GPU data says so instead of claiming none', () => {
  const old = { ...node, inventory: { ...node.inventory, processorModel: null, gpus: null } };
  const [cpu, gpu] = section(localWorkerSheet({ ...base, node: old }), 'hardware').metrics;
  assert.equal(cpu.value, '16 cœurs');
  assert.equal(gpu.value, 'Non déclaré');
});

test('an invalid date stays unknown and a missing central name falls back to the agent', () => {
  const sheet = localWorkerSheet({
    ...base,
    centralName: null,
    status: { ...status, centralName: null },
  });
  assert.equal(sheet.subtitle, 'Membre de son central');
  assert.equal(formatLastSignal('n’importe quoi'), null);
});

test('a member without a local server still shows its technical information from the agent', () => {
  const agentStatus = { ...status, inventory: node.inventory };
  const sheet = localWorkerSheet({ ...base, status: agentStatus, node: null, activeTasks: null });
  const [cpu, gpu] = section(sheet, 'hardware').metrics;
  assert.equal(cpu.value, 'AMD Ryzen 7 5700G');
  assert.match(gpu.value, /AMD/);
  assert.equal(section(sheet, 'capabilities').items[0].label, 'Moteur d’accélération');
  const [load, signal] = section(sheet, 'state').metrics;
  assert.equal(load.value, 'Indisponible');
  assert.match(signal.detail, /serveur du central/);
});

test('cluster join step waiting highlights the verification code as heroCode', () => {
  const steps = centralJoinSteps({
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

test('formatWorkerLabel formats displayName (hostname) when distinct', () => {
  assert.equal(formatWorkerLabel('ENCODING-50', 'TRUENAS'), 'TRUENAS (ENCODING-50)');
});

test('formatWorkerLabel returns single name when identical or case-insensitive match', () => {
  assert.equal(formatWorkerLabel('TRUENAS', 'TRUENAS'), 'TRUENAS');
  assert.equal(formatWorkerLabel('truenas', 'TRUENAS'), 'TRUENAS');
});

test('formatWorkerLabel falls back gracefully when either name is missing', () => {
  assert.equal(formatWorkerLabel('ENCODING-50', null), 'ENCODING-50');
  assert.equal(formatWorkerLabel('', 'TRUENAS'), 'TRUENAS');
  assert.equal(formatWorkerLabel('', ''), 'Worker inconnu');
});
