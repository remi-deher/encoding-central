import { StatusTone } from '../ui/status-badge.component';
import { LibraryWorkerAccessResponse } from '../ui/library-worker-access-types';
import { formatWorkerLabel, LibraryResponse, WorkerResponse } from './central';

export type WorkerLifecycleState = NonNullable<WorkerResponse['lifecycle']>;

export const NODE_LIFECYCLE_LABELS: Record<WorkerLifecycleState, string> = {
  online: 'En ligne',
  offline: 'Hors ligne',
  draining: 'Vidange',
  incompatible: 'Incompatible',
  quarantined: 'Quarantaine',
};

export const NODE_LIFECYCLE_TONES: Record<WorkerLifecycleState, StatusTone> = {
  online: 'success',
  offline: 'warning',
  draining: 'neutral',
  incompatible: 'danger',
  quarantined: 'danger',
};

/** État affiché d'un worker : le cycle de vie du serveur, sinon la présence observée. */
export function workerLifecycle(node: WorkerResponse): WorkerLifecycleState {
  return node.lifecycle ?? (node.status === 'online' ? 'online' : 'offline');
}

export interface CentralHealth {
  readonly tone: StatusTone;
  readonly title: string;
  readonly issues: readonly string[];
}

/** Synthèse de santé : ce qui empêche le worker ou le central de travailler normalement. */
export function assessCentralHealth(
  nodes: readonly WorkerResponse[],
  libraries: readonly LibraryResponse[],
  accesses: readonly LibraryWorkerAccessResponse[],
): CentralHealth {
  const issues: string[] = [];
  const states = nodes.map((node) => ({ node, state: workerLifecycle(node) }));
  const named = (state: WorkerLifecycleState) =>
    states
      .filter((item) => item.state === state)
      .map((item) => formatWorkerLabel(item.node.name, item.node.displayName));
  const quarantined = named('quarantined');
  if (quarantined.length)
    issues.push('Quarantaine, identité probablement dupliquée : ' + quarantined.join(', '));
  const incompatible = named('incompatible');
  if (incompatible.length) issues.push('Version incompatible : ' + incompatible.join(', '));
  const offline = named('offline');
  if (offline.length) issues.push('Hors ligne : ' + offline.join(', '));

  const available = new Set(
    states
      .filter((item) => item.state === 'online' && item.node.roles.includes('worker'))
      .map((item) => item.node.id),
  );
  if (nodes.length && available.size === 0) issues.push('Aucun worker disponible pour traiter.');
  for (const library of libraries.filter((item) => item.isActive)) {
    const reachable = accesses.some(
      (access) =>
        access.libraryId === library.id &&
        access.isEnabled &&
        access.mode !== 'unsupported' &&
        available.has(access.workerId),
    );
    if (!reachable) issues.push('Aucun worker disponible n’accède à « ' + library.name + ' ».');
  }
  const critical = quarantined.length > 0 || incompatible.length > 0 || available.size === 0;
  if (!issues.length)
    return {
      tone: 'success',
      title: 'Le central fonctionne normalement.',
      issues,
    };
  return {
    tone: critical && nodes.length ? 'danger' : 'warning',
    title:
      issues.length === 1
        ? 'Un point demande attention.'
        : issues.length + ' points demandent attention.',
    issues,
  };
}
