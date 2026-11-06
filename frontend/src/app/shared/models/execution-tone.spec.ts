import { EXECUTION_STATE_LABELS } from './execution';
import { executionTone } from './execution-tone';

describe('executionTone', () => {
  it('maps every execution state to its shared tone and French label', () => {
    expect(executionTone('queued')).toBe('warning');
    expect(executionTone('running')).toBe('accent');
    expect(executionTone('cancelling')).toBe('danger');
    expect(executionTone('succeeded')).toBe('success');
    expect(executionTone('failed')).toBe('danger');
    expect(executionTone('cancelled')).toBe('warning');
    expect(executionTone('deferred')).toBe('warning');
    expect(EXECUTION_STATE_LABELS).toEqual({
      queued: 'En attente',
      running: 'En cours',
      cancelling: 'Annulation demandée',
      succeeded: 'Réussie',
      failed: 'Échec',
      cancelled: 'Annulée',
      deferred: 'Reportée',
    });
  });
});
