import { assessCentralHealth } from './central-health';
import { LibraryResponse, WorkerResponse } from './central';
import { LibraryWorkerAccessResponse } from '../ui/library-worker-access-types';

function node(id: string, extra: Partial<WorkerResponse> = {}): WorkerResponse {
  return {
    id,
    name: 'worker-' + id,
    roles: ['worker'],
    maxConcurrentTasks: 1,
    capabilities: [],
    status: 'online',
    lastSeenAt: '2026-10-15T10:00:00Z',
    ...extra,
  };
}
const library = { id: 'l1', name: 'Films', isActive: true } as LibraryResponse;
const access = (nodeId: string): LibraryWorkerAccessResponse =>
  ({ libraryId: 'l1', workerId: nodeId, mode: 'direct', isEnabled: true }) as LibraryWorkerAccessResponse;

describe('assessClusterHealth', () => {
  it('is healthy when a worker reaches every active library', () => {
    const health = assessCentralHealth([node('a')], [library], [access('a')]);
    expect(health.tone).toBe('success');
    expect(health.issues).toEqual([]);
  });

  it('flags a quarantined node as critical and ignores it for library access', () => {
    const health = assessCentralHealth(
      [node('a', { lifecycle: 'quarantined' })],
      [library],
      [access('a')],
    );
    expect(health.tone).toBe('danger');
    expect(health.issues.join(' ')).toContain('Quarantaine');
    expect(health.issues.join(' ')).toContain('Films');
  });

  it('warns when a library has no reachable worker', () => {
    const health = assessCentralHealth([node('a')], [library], []);
    expect(health.tone).toBe('warning');
    expect(health.issues).toEqual(['Aucun worker disponible n’accède à « Films ».']);
  });
});
