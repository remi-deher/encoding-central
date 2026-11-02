import { ExecutionState, EXECUTION_STATE_LABELS } from './execution';

export type LibraryActivityStatus =
  | 'untreated'
  | 'active'
  | 'done'
  | 'failed'
  | 'cancelled'
  | 'disabled';

export const ACTIVITY_STATUS_LABELS: Record<LibraryActivityStatus, string> = {
  untreated: 'À traiter',
  active: 'En cours',
  done: 'Traité',
  failed: 'Erreurs',
  cancelled: 'Annulées',
  disabled: 'Désactivé',
};

export interface LibraryActivityFile {
  readonly id: string;
  readonly libraryId: string;
  readonly library: string;
  readonly relativePath: string;
  readonly bytes: number;
  readonly modifiedAt: string;
  readonly libraryActive: boolean;
  readonly flow: string | null;
  readonly flowVersion: number | null;
  readonly status: LibraryActivityStatus;
  readonly executionId: string | null;
  readonly executionState: string | null;
  readonly detail: string | null;
  readonly nodeId: string | null;
  readonly node: string | null;
  readonly progress: number | null;
  readonly updatedAt: string | null;
  readonly executionOwnershipState?: string | null;
  readonly quarantineReason?: string | null;
  readonly conflictingExecutionId?: string | null;
}

export function activityExecutionLabel(file: LibraryActivityFile): string | null {
  const state = file.executionState as ExecutionState | null;
  if (!state) return null;
  const label = EXECUTION_STATE_LABELS[state] ?? state;
  return file.executionOwnershipState === 'quarantined' ? `En quarantaine · ${label}` : label;
}

export function activityExecutionTone(
  file: LibraryActivityFile,
): 'accent' | 'neutral' | 'danger' | 'success' | 'warning' {
  if (file.executionOwnershipState === 'quarantined') return 'warning';
  switch (file.executionState) {
    case 'running':
    case 'cancelling':
      return 'accent';
    case 'failed':
      return 'danger';
    case 'cancelled':
      return 'warning';
    case 'succeeded':
      return 'success';
    case 'deferred':
      return 'warning';
    default:
      return 'neutral';
  }
}

export interface LibraryActivityCounts {
  readonly untreated: number;
  readonly active: number;
  readonly done: number;
  readonly failed: number;
  readonly cancelled?: number;
  readonly disabled: number;
}

export interface LibraryActivityResponse {
  readonly files: readonly LibraryActivityFile[];
  readonly counts: LibraryActivityCounts;
  readonly totalCount: number;
  readonly limit: number;
  readonly offset: number;
  readonly observedAt: string;
}
