export type ExecutionState =
  'queued' | 'running' | 'cancelling' | 'succeeded' | 'failed' | 'cancelled' | 'deferred';
export interface ExecutionResponse {
  readonly failure?: {
    readonly code: string;
    readonly stepId: string | null;
    readonly publicationConfirmed: boolean | null;
  } | null;
  readonly ownership?: {
    readonly executionId: string;
    readonly ownerPeerId: string;
    readonly epoch: number;
    readonly state: string;
  } | null;
  readonly id: string;
  readonly media: string;
  readonly library: string;
  readonly flow: string;
  readonly flowVersion: number;
  readonly state: ExecutionState;
  readonly nodeId: string | null;
  readonly node: string | null;
  readonly progress: number | null;
  readonly detail: string;
  readonly attempt: number;
  readonly revision: number;
  readonly queuedAt: string;
  readonly startedAt: string | null;
  readonly finishedAt: string | null;
  readonly beforeMetadata?: {
    readonly format?: { readonly size?: string };
    readonly streams?: readonly unknown[];
  } | null;
  readonly afterMetadata?: {
    readonly format?: { readonly size?: string };
    readonly streams?: readonly unknown[];
  } | null;
  readonly publishedPath?: string | null;
  readonly currentPhase?: string | null;
  /** True only while a running execution holds a valid lease under active ownership. */
  readonly isActivelyProcessing?: boolean;
  readonly videoEncodings?: readonly ResolvedVideoEncoding[] | null;
  readonly libraryId?: string | null;
  readonly updatedAt: string;
}

export interface RuntimeCommandLog {
  readonly kind?: 'command' | 'operation' | null;
  readonly bytesProcessed?: number | null;
  readonly progressPercent?: number | null;
  readonly id: string;
  readonly command: string;
  readonly standardOutput: string;
  readonly standardError: string;
  readonly exitCode: number | null;
  readonly startedAt: string;
  readonly finishedAt: string | null;
  readonly truncated: boolean;
}

export interface ExecutionPipelineStep {
  readonly diagnostic?: { readonly code: string; readonly message: string } | null;
  readonly nodeId: string;
  readonly nodeName: string;
  readonly invocationId: string;
  readonly componentKind: string;
  readonly status: 'completed' | 'failed' | 'cancelled' | 'cancelling' | 'running';
  readonly startedAt: string;
  readonly completedAt: string | null;
  readonly outputId: string | null;
  readonly outputLabel: string | null;
  readonly nextNodeId: string | null;
  readonly commands: readonly RuntimeCommandLog[];
}
export interface ResolvedVideoEncoding {
  readonly flowNodeId: string;
  readonly workerId: string;
  readonly codec: string;
  readonly encoderId: string;
  readonly ffmpegVersion: string;
  readonly acceleration: string;
  readonly qualityMode: string;
  readonly qualityOption: string;
  readonly quality: number;
  readonly presetId: string;
  readonly presetOption: string | null;
  readonly presetValue: string | null;
  readonly profileId: string;
  readonly profileVersion: number;
  readonly profileName: string;
  readonly priority: number;
  readonly resolvedAt: string;
}
export type ExecutionEventState = ExecutionState | 'media.published';
export interface ExecutionEventResponse {
  readonly id: string;
  readonly revision: number;
  readonly state: ExecutionEventState;
  readonly detail: string;
  readonly createdAt: string;
}
export interface ExecutionOverviewResponse {
  readonly executions: readonly ExecutionResponse[];
  readonly queuedCount: number;
  readonly quarantinedCount?: number;
  readonly activeCount: number;
  readonly failedCount: number;
  readonly cancelledCount: number;
  readonly succeededCount: number;
  readonly deferredCount?: number;
  readonly observedAt: string;
  readonly activity: readonly ExecutionActivityResponse[];
  readonly totalCount?: number;
  readonly limit?: number;
  readonly offset?: number;
}
export interface ExecutionFilterQuery {
  readonly state?: ExecutionState;
  readonly libraryId?: string;
  readonly nodeId?: string;
  readonly since?: string;
  readonly limit?: number;
  readonly offset?: number;
}
export const EXECUTION_STATE_LABELS: Record<ExecutionState, string> = {
  queued: 'En attente',
  running: 'En cours',
  cancelling: 'Annulation demandée',
  succeeded: 'Réussie',
  failed: 'Échec',
  cancelled: 'Annulée',
  deferred: 'Reportée',
};

export interface ExecutionActivityResponse {
  readonly id: string;
  readonly executionId: string;
  readonly media: string;
  readonly state: ExecutionState;
  readonly detail: string;
  readonly createdAt: string;
}

export function executionStateLabel(execution: ExecutionResponse): string {
  switch (execution.ownership?.state) {
    case 'quarantined':
      return 'En quarantaine';
    case 'frozen':
      return ['queued', 'running', 'cancelling'].includes(execution.state)
        ? 'Transfert en préparation'
        : EXECUTION_STATE_LABELS[execution.state];
    case 'received':
      return 'Réception inactive';
    case 'transferred':
      return ['queued', 'running', 'cancelling'].includes(execution.state)
        ? 'Cédée à un autre serveur'
        : EXECUTION_STATE_LABELS[execution.state];
    default:
      return EXECUTION_STATE_LABELS[execution.state];
  }
}

export function executionPhaseLabel(phase: string | null | undefined): string {
  return (
    (
      {
        publication: 'Publication en cours',
        source_transfer: 'Préparation et transfert de la source',
        waiting_handoff: 'Attente du nœud de relais',
      } as Record<string, string>
    )[phase ?? ''] ?? ''
  );
}
