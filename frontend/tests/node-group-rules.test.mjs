import assert from 'node:assert/strict';
import test from 'node:test';
import { loadModel } from './model-loader.mjs';

const {
  validateLabelKey,
  parseLabels,
  matchesGroupCriteria,
  nodeSupportsEncoder,
  filterNodesByCriteria,
  formatGroupCriteriaBadges,
  extractAvailableOperatingSystems,
  extractAvailableArchitectures,
  extractAvailableTools,
} = await loadModel('node-group-rules');

const makeNode = (extra = {}) => ({
  id: '00000000-0000-0000-0000-000000000001',
  name: 'node-1',
  displayName: 'Worker 1',
  roles: ['worker'],
  maxConcurrentTasks: 4,
  status: 'online',
  lastSeenAt: '2026-10-17T00:00:00Z',
  capabilities: [
    { toolId: 'ffmpeg', version: '7.0', mediaTypes: ['video'], accelerationTypes: [] },
    { toolId: 'ffprobe', version: '7.0', mediaTypes: ['video'], accelerationTypes: [] },
  ],
  inventory: {
    operatingSystem: 'linux',
    architecture: 'X64',
    processorCount: 16,
    memoryMb: 32768,
  },
  ...extra,
});

test('valide la clé d’étiquette selon le format cluster', () => {
  assert.equal(validateLabelKey('gpu'), true);
  assert.equal(validateLabelKey('env.production'), true);
  assert.equal(validateLabelKey('pool_fast-1'), true);
  assert.equal(validateLabelKey(''), false);
  assert.equal(validateLabelKey('1invalid'), false);
  assert.equal(validateLabelKey('invalid with space'), false);
  assert.equal(validateLabelKey('invalid@char'), false);
});

test('analyse les étiquettes en clé ou clé=valeur', () => {
  const parsed = parseLabels('gpu=nvidia\nenv=prod, fast_transcode');
  assert.deepEqual(parsed, {
    gpu: 'nvidia',
    env: 'prod',
    fast_transcode: null,
  });
});

test('évaluation d’un critère vide : correspond à tout nœud', () => {
  const node = makeNode();
  assert.equal(matchesGroupCriteria({}, node), true);
});

test('évaluation des critères de rôle, OS et architecture', () => {
  const node = makeNode();
  assert.equal(matchesGroupCriteria({ roles: ['worker'] }, node), true);
  assert.equal(matchesGroupCriteria({ roles: ['server'] }, node), false);
  assert.equal(matchesGroupCriteria({ operatingSystem: 'linux' }, node), true);
  assert.equal(matchesGroupCriteria({ operatingSystem: 'windows' }, node), false);
  assert.equal(matchesGroupCriteria({ architecture: 'X64' }, node), true);
  assert.equal(matchesGroupCriteria({ architecture: 'Arm64' }, node), false);
});

test('évaluation de la mémoire, CPU et concurrence minimale', () => {
  const node = makeNode({
    inventory: {
      operatingSystem: 'linux',
      architecture: 'X64',
      processorCount: 8,
      memoryMb: 16384,
    },
    maxConcurrentTasks: 2,
  });
  assert.equal(matchesGroupCriteria({ minimumMemoryMb: 16384 }, node), true);
  assert.equal(matchesGroupCriteria({ minimumMemoryMb: 32768 }, node), false);
  assert.equal(matchesGroupCriteria({ minimumProcessorCount: 8 }, node), true);
  assert.equal(matchesGroupCriteria({ minimumProcessorCount: 16 }, node), false);
  assert.equal(matchesGroupCriteria({ minimumConcurrentTasks: 2 }, node), true);
  assert.equal(matchesGroupCriteria({ minimumConcurrentTasks: 4 }, node), false);
});

test('évaluation des outils requis', () => {
  const node = makeNode();
  assert.equal(matchesGroupCriteria({ requiredToolIds: ['ffmpeg'] }, node), true);
  assert.equal(matchesGroupCriteria({ requiredToolIds: ['ffmpeg', 'ffprobe'] }, node), true);
  assert.equal(matchesGroupCriteria({ requiredToolIds: ['ffmpeg', 'mkvmerge'] }, node), false);
});

test('filtrage d’une liste de nœuds par critères', () => {
  const node1 = makeNode({
    id: '1',
    roles: ['worker'],
    inventory: {
      operatingSystem: 'linux',
      architecture: 'X64',
      processorCount: 16,
      memoryMb: 32768,
    },
  });
  const node2 = makeNode({
    id: '2',
    roles: ['server'],
    inventory: {
      operatingSystem: 'windows',
      architecture: 'X64',
      processorCount: 4,
      memoryMb: 8192,
    },
  });
  const matched = filterNodesByCriteria({ roles: ['worker'], operatingSystem: 'linux' }, [
    node1,
    node2,
  ]);
  assert.equal(matched.length, 1);
  assert.equal(matched[0].id, '1');
});

test('formatage des critères en pastilles textuelles lisibles', () => {
  const badges = formatGroupCriteriaBadges({
    roles: ['worker'],
    operatingSystem: 'Linux',
    architecture: 'X64',
    minimumMemoryMb: 16384,
    minimumProcessorCount: 8,
    minimumConcurrentTasks: 2,
    requiredToolIds: ['ffmpeg', 'mkvmerge'],
  });
  assert.deepEqual(badges, [
    'Workers',
    'OS: Linux',
    'Arch: X64',
    'RAM ≥ 16 Go',
    'CPU ≥ 8',
    'Tâches ≥ 2',
    'Outils: ffmpeg, mkvmerge',
  ]);
});

test('extraction des options disponibles depuis l’inventaire des nœuds', () => {
  const node1 = makeNode({
    inventory: { operatingSystem: 'Linux', architecture: 'X64', processorCount: 8, memoryMb: 8192 },
    capabilities: [{ toolId: 'ffmpeg' }, { toolId: 'ffprobe' }],
  });
  const node2 = makeNode({
    inventory: {
      operatingSystem: 'Windows',
      architecture: 'Arm64',
      processorCount: 4,
      memoryMb: 4096,
    },
    capabilities: [{ toolId: 'ffmpeg' }, { toolId: 'mkvmerge' }],
  });
  assert.deepEqual(extractAvailableOperatingSystems([node1, node2]), ['Linux', 'Windows']);
  assert.deepEqual(extractAvailableArchitectures([node1, node2]), ['Arm64', 'X64']);
  assert.deepEqual(extractAvailableTools([node1, node2]), ['ffmpeg', 'ffprobe', 'mkvmerge']);
});

test('détection du support des encodeurs et codecs vidéo majeurs', () => {
  const nodeWithChecks = makeNode({
    capabilities: [
      {
        toolId: 'ffmpeg',
        encoderChecks: [
          { encoderId: 'av1_nvenc', status: 'usable' },
          { encoderId: 'hevc_nvenc', status: 'usable' },
          { encoderId: 'h264_nvenc', status: 'broken' },
        ],
      },
    ],
  });

  assert.equal(nodeSupportsEncoder(nodeWithChecks, 'av1'), true);
  assert.equal(nodeSupportsEncoder(nodeWithChecks, 'hevc'), true);
  assert.equal(nodeSupportsEncoder(nodeWithChecks, 'h264'), false);
  assert.equal(nodeSupportsEncoder(nodeWithChecks, 'av1_nvenc'), true);
  assert.equal(nodeSupportsEncoder(nodeWithChecks, 'libsvtav1'), false);

  const nodeWithRawEncoders = makeNode({
    capabilities: [
      {
        toolId: 'ffmpeg',
        encoders: ['libsvtav1', 'libx265', 'libx264'],
      },
    ],
  });

  assert.equal(nodeSupportsEncoder(nodeWithRawEncoders, 'av1'), true);
  assert.equal(nodeSupportsEncoder(nodeWithRawEncoders, 'hevc'), true);
  assert.equal(nodeSupportsEncoder(nodeWithRawEncoders, 'h264'), true);
});

test('évaluation du critère requiredEncoders pour les groupes de nœuds', () => {
  const av1Node = makeNode({
    capabilities: [
      {
        toolId: 'ffmpeg',
        encoderChecks: [{ encoderId: 'libsvtav1', status: 'usable' }],
      },
    ],
  });

  const legacyNode = makeNode({
    capabilities: [
      {
        toolId: 'ffmpeg',
        encoderChecks: [{ encoderId: 'libx264', status: 'usable' }],
      },
    ],
  });

  assert.equal(matchesGroupCriteria({ requiredEncoders: ['av1'] }, av1Node), true);
  assert.equal(matchesGroupCriteria({ requiredEncoders: ['av1'] }, legacyNode), false);
  assert.equal(matchesGroupCriteria({ requiredEncoders: ['av1', 'h264'] }, av1Node), false);

  const badges = formatGroupCriteriaBadges({ requiredEncoders: ['av1', 'hevc'] });
  assert.deepEqual(badges, ['Encodeurs: AV1, HEVC']);
});
