import assert from 'node:assert/strict';
import test from 'node:test';
import { loadModel } from './model-loader.mjs';
const {
  workerAcceleratorBadges,
  workerHardwareLabel,
  workerUnvalidatedAccelerators,
  workerUnvalidatedAcceleratorBadges,
} = await loadModel('worker-hardware');

const node = (extra = {}) => ({ roles: ['worker'], capabilities: [], ...extra });
const ffmpeg = (checks, accelerationTypes = []) => ({
  toolId: 'ffmpeg',
  version: '7',
  mediaTypes: ['video'],
  accelerationTypes,
  encoderChecks: checks.map(([encoderId, status]) => ({ encoderId, status })),
});

test('un worker sans worker n’a pas d’accélérateur applicable', () => {
  assert.deepEqual(workerAcceleratorBadges(node({ roles: ['server'] })), ['Non applicable']);
});

test('aucune capacité déclarée : pas de CPU inventé', () => {
  assert.deepEqual(workerAcceleratorBadges(node()), ['Non déclaré']);
});

test('l’accélérateur vérifié le plus prioritaire passe en premier, les détectés ne sont pas annoncés', () => {
  const nodeObj = node({
    capabilities: [
      ffmpeg(
        [
          ['h264_vaapi', 'usable'],
          ['h264_qsv', 'usable'],
          ['hevc_nvenc', 'detected'],
        ],
        ['qsv', 'vaapi'],
      ),
    ],
  });
  const badges = workerAcceleratorBadges(nodeObj);
  assert.deepEqual(badges, ['Quick Sync', 'VAAPI']);
  assert.deepEqual(workerUnvalidatedAccelerators(nodeObj), ['NVENC']);
  assert.deepEqual(workerUnvalidatedAcceleratorBadges(nodeObj), [
    'NVENC (encodeur FFmpeg présent, non validé)',
  ]);
});

test('repli CPU quand aucun encodeur matériel n’est vérifié, encodeurs non validés indiqués séparément', () => {
  const nodeObj = node({
    capabilities: [
      ffmpeg([
        ['h264_qsv', 'detected'],
        ['libx264', 'usable'],
      ]),
    ],
  });
  const badges = workerAcceleratorBadges(nodeObj);
  assert.deepEqual(badges, ['CPU']);
  assert.deepEqual(workerUnvalidatedAccelerators(nodeObj), ['Quick Sync']);
  assert.deepEqual(workerUnvalidatedAcceleratorBadges(nodeObj), [
    'Quick Sync (encodeur FFmpeg présent, non validé)',
  ]);
});

test('fiche AMD FirePro V4900 : seul CPU est annoncé, NVENC/QSV/VAAPI détectés dans FFmpeg sont séparés', () => {
  const fireproNode = node({
    capabilities: [
      ffmpeg([
        ['libx264', 'usable'],
        ['h264_nvenc', 'detected'],
        ['h264_qsv', 'detected'],
        ['h264_vaapi', 'detected'],
      ]),
    ],
  });
  assert.deepEqual(workerAcceleratorBadges(fireproNode), ['CPU']);
  assert.deepEqual(workerUnvalidatedAccelerators(fireproNode), ['NVENC', 'Quick Sync', 'VAAPI']);
  assert.deepEqual(workerUnvalidatedAcceleratorBadges(fireproNode), [
    'NVENC (encodeur FFmpeg présent, non validé)',
    'Quick Sync (encodeur FFmpeg présent, non validé)',
    'VAAPI (encodeur FFmpeg présent, non validé)',
  ]);
});

test('matériel : inventaire absent puis complet', () => {
  assert.equal(workerHardwareLabel(node()), 'Non renseigné');
  assert.equal(
    workerHardwareLabel(
      node({
        inventory: {
          operatingSystem: 'Linux',
          architecture: 'x64',
          processorCount: 6,
          memoryMb: 32768,
          processorModel: 'Intel Core i5-9500T',
          gpus: [{ name: 'Intel UHD Graphics 630' }],
        },
      }),
    ),
    'Linux x64 · Intel Core i5-9500T · 6 cœurs · 32 Go de RAM · GPU : Intel UHD Graphics 630',
  );
});

const { workerMemberSections, tidyProcessorName } = await loadModel('worker-hardware');
const inventory = {
  operatingSystem: 'Ubuntu 24.04.5 LTS',
  architecture: 'X64',
  processorCount: 8,
  memoryMb: 24000,
  processorModel: 'Intel(R) Xeon(R) CPU E5620 @ 2.40GHz',
  gpus: [{ name: 'AMD Radeon HD 6570' }],
};

test('le nom du processeur est nettoyé', () => {
  assert.equal(
    tidyProcessorName('Intel(R) Xeon(R) CPU E5620 @ 2.40GHz'),
    'Intel Xeon E5620 @ 2.40GHz',
  );
});

test('carte : CPU principal, aucun accélérateur non vérifié replié', () => {
  const [accelerator, hardware, processing] = workerMemberSections(
    node({
      status: 'online',
      maxConcurrentTasks: 1,
      inventory,
      capabilities: [
        ffmpeg([
          ['libx264', 'usable'],
          ['h264_qsv', 'detected'],
          ['h264_vaapi', 'detected'],
        ]),
      ],
    }),
    { activeTasks: 0, libraryAccess: 'À configurer' },
  );
  assert.equal(accelerator.primary.label, 'CPU');
  assert.equal(accelerator.folded, undefined);
  assert.deepEqual(
    hardware.facts.map((fact) => fact.label),
    ['OS', 'CPU', 'RAM', 'GPU'],
  );
  assert.equal(hardware.facts[1].value, 'Intel Xeon E5620 @ 2.40GHz · 8 cœurs');
  assert.equal(hardware.facts[3].value, 'AMD Radeon HD 6570');
  assert.equal(processing.meter.valueLabel, '0 / 1 tâche(s)');
});

test('carte : un accélérateur vérifié devient principal avec son ton', () => {
  const [accelerator] = workerMemberSections(
    node({
      status: 'online',
      maxConcurrentTasks: 1,
      capabilities: [
        ffmpeg(
          [
            ['h264_qsv', 'usable'],
            ['h264_vaapi', 'usable'],
          ],
          ['qsv', 'vaapi'],
        ),
      ],
    }),
    { activeTasks: 0, libraryAccess: 'x' },
  );
  assert.deepEqual([accelerator.primary.label, accelerator.primary.tone], ['Quick Sync', 'blue']);
  assert.equal(accelerator.chips[0].label, 'VAAPI');
  assert.equal(accelerator.folded, undefined);
});

test('carte : charge inconnue ou worker hors ligne n’affiche pas de jauge', () => {
  const offline = workerMemberSections(node({ status: 'offline', maxConcurrentTasks: 2 }), {
    activeTasks: 1,
    libraryAccess: 'x',
  })[2];
  assert.equal(offline.meter, undefined);
  assert.match(offline.hint, /Hors ligne/);
  const unknown = workerMemberSections(node({ status: 'online', maxConcurrentTasks: 2 }), {
    activeTasks: null,
    libraryAccess: 'x',
  })[2];
  assert.equal(unknown.meter, undefined);
  assert.match(unknown.hint, /indisponible/);
});

test('carte : sans matériel déclaré, le dit explicitement', () => {
  const hardware = workerMemberSections(node(), { activeTasks: null, libraryAccess: 'x' })[1];
  assert.match(hardware.hint, /Non renseigné/);
});

const { detectOsFamily, workerAcceleratorChips } = await loadModel('worker-hardware');

test('détection de la famille de système d’exploitation', () => {
  assert.equal(detectOsFamily('Ubuntu 24.04.5 LTS'), 'linux');
  assert.equal(detectOsFamily('Debian GNU/Linux 12 (bookworm)'), 'linux');
  assert.equal(detectOsFamily('Microsoft Windows Server 2022'), 'windows');
  assert.equal(detectOsFamily('Darwin Kernel Version 23.4.0'), 'macos');
  assert.equal(detectOsFamily(null), 'unknown');
  assert.equal(detectOsFamily(''), 'unknown');
});

test('pastilles accélérateur matériel sans CPU', () => {
  const chips = workerAcceleratorChips(
    node({
      capabilities: [
        ffmpeg([
          ['h264_nvenc', 'usable'],
          ['h264_qsv', 'usable'],
        ]),
      ],
    }),
  );
  assert.deepEqual(chips, [
    { label: 'NVENC', tone: 'lime' },
    { label: 'Quick Sync', tone: 'blue' },
  ]);
});

test('sections membre : omission possible de la section accélérateur', () => {
  const sections = workerMemberSections(
    node({ status: 'online', maxConcurrentTasks: 2 }),
    { activeTasks: 0, libraryAccess: 'x' },
    { includeAccelerator: false },
  );
  assert.equal(sections.length, 2);
  assert.equal(sections[0].label, 'Matériel déclaré');
  assert.equal(sections[1].label, 'Traitements');
});
