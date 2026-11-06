import { ExecutionState } from './execution';
import { StatusTone } from '../ui/status-badge.component';

/** Ton commun pour chaque état d'exécution, indépendant de la page qui l'affiche. */
export const EXECUTION_STATE_TONES: Record<ExecutionState, StatusTone> = {
  queued: 'warning',
  running: 'accent',
  cancelling: 'danger',
  succeeded: 'success',
  failed: 'danger',
  cancelled: 'warning',
  deferred: 'warning',
};

export function executionTone(state: ExecutionState): StatusTone {
  return EXECUTION_STATE_TONES[state];
}
