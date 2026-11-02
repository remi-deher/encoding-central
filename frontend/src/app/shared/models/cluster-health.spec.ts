import { assessClusterHealth } from './cluster-health';
import { LibraryResponse, NodeResponse } from './cluster';
import { LibraryNodeAccessResponse } from '../ui/library-node-access-types';

function node(id: string, extra: Partial<NodeResponse> = {}): NodeResponse {
  return {
    id,
    name: 'nœud-' + id,
    roles: ['worker'],
    maxConcurrentTasks: 1,
    capabilities: [],
    status: 'online',
    lastSeenAt: '2026-10-15T10:00:00Z',
    ...extra,
  };
}
const library = { id: 'l1', name: 'Films', isActive: true } as LibraryResponse;
const access = (nodeId: string): LibraryNodeAccessResponse =>
  ({ libraryId: 'l1', nodeId, mode: 'direct', isEnabled: true }) as LibraryNodeAccessResponse;

describe('assessClusterHealth', () => {
  it('is healthy when a worker reaches every active library', () => {
    const health = assessClusterHealth([node('a')], [library], [access('a')]);
    expect(health.tone).toBe('success');
    expect(health.issues).toEqual([]);
  });

  it('flags a quarantined node as critical and ignores it for library access', () => {
    const health = assessClusterHealth(
      [node('a', { lifecycle: 'quarantined' })],
      [library],
      [access('a')],
    );
    expect(health.tone).toBe('danger');
    expect(health.issues.join(' ')).toContain('Quarantaine');
    expect(health.issues.join(' ')).toContain('Films');
  });

  it('warns when a library has no reachable worker', () => {
    const health = assessClusterHealth([node('a')], [library], []);
    expect(health.tone).toBe('warning');
    expect(health.issues).toEqual(['Aucun worker disponible n’accède à « Films ».']);
  });
});
