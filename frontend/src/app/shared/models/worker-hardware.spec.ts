import { WorkerResponse } from './central';
import {
  detectOsFamily,
  workerAcceleratorBadges,
  workerAcceleratorChips,
  workerHardwareLabel,
  workerMemberSections,
  workerOsFamily,
  workerUnvalidatedAccelerators,
  workerUnvalidatedAcceleratorBadges,
} from './worker-hardware';

function node(extra: Partial<WorkerResponse> = {}): WorkerResponse {
  return {
    id: 'a',
    name: 'a',
    roles: ['worker'],
    maxConcurrentTasks: 1,
    capabilities: [],
    status: 'online',
    lastSeenAt: '2026-10-16T10:00:00Z',
    ...extra,
  };
}
const ffmpeg = (checks: [string, 'usable' | 'detected'][], acceleration: string[] = []) => ({
  toolId: 'ffmpeg',
  version: '7',
  mediaTypes: ['video'],
  accelerationTypes: acceleration,
  encoderChecks: checks.map(([encoderId, status]) => ({ encoderId, status })),
});

describe('nodeAcceleratorBadges', () => {
  it('is not applicable without the worker role', () => {
    expect(workerAcceleratorBadges(node({ roles: ['server'] }))).toEqual(['Non applicable']);
  });

  it('does not invent an accelerator when nothing is declared', () => {
    expect(workerAcceleratorBadges(node())).toEqual(['Non déclaré']);
  });

  it('puts the preferred verified accelerator first and ignores detected-only ones', () => {
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
    expect(badges).toEqual(['Quick Sync', 'VAAPI']);
    expect(workerUnvalidatedAccelerators(nodeObj)).toEqual(['NVENC']);
    expect(workerUnvalidatedAcceleratorBadges(nodeObj)).toEqual([
      'NVENC (encodeur FFmpeg présent, non validé)',
    ]);
  });

  it('falls back to CPU when no hardware encoder is verified', () => {
    const nodeObj = node({
      capabilities: [
        ffmpeg([
          ['h264_qsv', 'detected'],
          ['libx264', 'usable'],
        ]),
      ],
    });
    const badges = workerAcceleratorBadges(nodeObj);
    expect(badges).toEqual(['CPU']);
    expect(workerUnvalidatedAccelerators(nodeObj)).toEqual(['Quick Sync']);
    expect(workerUnvalidatedAcceleratorBadges(nodeObj)).toEqual([
      'Quick Sync (encodeur FFmpeg présent, non validé)',
    ]);
  });

  it('displays only CPU for instance with failed hardware mire despite NVENC, QSV and VAAPI in FFmpeg', () => {
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
    expect(workerAcceleratorBadges(fireproNode)).toEqual(['CPU']);
    expect(workerUnvalidatedAccelerators(fireproNode)).toEqual(['NVENC', 'Quick Sync', 'VAAPI']);
    expect(workerUnvalidatedAcceleratorBadges(fireproNode)).toEqual([
      'NVENC (encodeur FFmpeg présent, non validé)',
      'Quick Sync (encodeur FFmpeg présent, non validé)',
      'VAAPI (encodeur FFmpeg présent, non validé)',
    ]);
  });
});

describe('nodeHardwareLabel', () => {
  it('reports missing inventory explicitly', () => {
    expect(workerHardwareLabel(node())).toBe('Non renseigné');
  });

  it('combines OS, CPU, RAM and GPU', () => {
    const label = workerHardwareLabel(
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
    );
    expect(label).toBe(
      'Linux x64 · Intel Core i5-9500T · 6 cœurs · 32 Go de RAM · GPU : Intel UHD Graphics 630',
    );
  });
});

describe('detectOsFamily and nodeOsFamily', () => {
  it('detects linux family correctly', () => {
    expect(detectOsFamily('Ubuntu 24.04 LTS')).toBe('linux');
    expect(detectOsFamily('Debian GNU/Linux 12')).toBe('linux');
    expect(detectOsFamily('Linux 6.8.0-generic')).toBe('linux');
    expect(workerOsFamily(node({ inventory: { operatingSystem: 'Ubuntu 24.04.5 LTS', architecture: 'x64', processorCount: 8, memoryMb: 16384 } }))).toBe('linux');
  });

  it('detects windows and macos families', () => {
    expect(detectOsFamily('Microsoft Windows 11 Pro')).toBe('windows');
    expect(detectOsFamily('Darwin 23.5.0')).toBe('macos');
    expect(detectOsFamily('macOS Sonoma 14.5')).toBe('macos');
    expect(detectOsFamily(null)).toBe('unknown');
    expect(detectOsFamily('')).toBe('unknown');
    expect(detectOsFamily('FreeBSD 14.0')).toBe('unknown');
  });
});

describe('nodeAcceleratorChips', () => {
  it('returns empty array when worker has no accelerators or only CPU', () => {
    expect(workerAcceleratorChips(node())).toEqual([]);
    expect(workerAcceleratorChips(node({ capabilities: [ffmpeg([['libx264', 'usable']])] }))).toEqual([]);
  });

  it('returns chips with appropriate tone for verified hardware accelerators', () => {
    const chips = workerAcceleratorChips(
      node({
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
    );
    expect(chips).toEqual([
      { label: 'Quick Sync', tone: 'blue' },
      { label: 'VAAPI', tone: 'purple' },
    ]);
  });
});

describe('nodeMemberSections options', () => {
  it('allows omitting the accelerator section when accelerators are in badges', () => {
    const sections = workerMemberSections(
      node({
        status: 'online',
        maxConcurrentTasks: 2,
        capabilities: [ffmpeg([['libx264', 'usable']])],
      }),
      { activeTasks: 1, libraryAccess: 'Toutes' },
      { includeAccelerator: false },
    );
    expect(sections.length).toBe(2);
    expect(sections[0].label).toBe('Matériel déclaré');
    expect(sections[1].label).toBe('Traitements');
  });
});
