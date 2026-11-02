import {
  activityExecutionLabel,
  activityExecutionTone,
  LibraryActivityFile,
} from './library-activity';

const file = (overrides: Partial<LibraryActivityFile> = {}): LibraryActivityFile => ({
  id: 'file-1',
  libraryId: 'library-1',
  library: 'Test',
  relativePath: 'episode.mkv',
  bytes: 1,
  modifiedAt: '2026-01-01T00:00:00Z',
  libraryActive: true,
  flow: 'HEVC',
  flowVersion: 8,
  status: 'cancelled',
  executionId: 'execution-1',
  executionState: 'cancelled',
  detail: 'Annulée avant démarrage.',
  nodeId: null,
  node: null,
  progress: null,
  updatedAt: null,
  ...overrides,
});

describe('library activity execution diagnostics', () => {
  it('shows a cancelled execution with warning tone in the cancelled section', () => {
    expect(activityExecutionLabel(file())).toBe('Annulée');
    expect(activityExecutionTone(file())).toBe('warning');
  });

  it('shows quarantine separately from the execution state', () => {
    const quarantined = file({ executionOwnershipState: 'quarantined' });
    expect(activityExecutionLabel(quarantined)).toBe('En quarantaine · Annulée');
    expect(activityExecutionTone(quarantined)).toBe('warning');
  });

  it('shows queued attempts as waiting rather than hiding their state', () => {
    expect(activityExecutionLabel(file({ status: 'untreated', executionState: 'queued' }))).toBe(
      'En attente',
    );
  });
});
