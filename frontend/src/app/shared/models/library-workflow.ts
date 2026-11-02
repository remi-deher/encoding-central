export interface LibraryFlowAssignment {
  readonly flowId: string;
  readonly version: number;
  readonly revision: number;
  readonly name: string;
}
export interface LibraryMediaFile {
  readonly id: string;
  readonly relativePath: string;
  readonly bytes: number;
  readonly modifiedAt: string;
}
export interface LibraryWorkflow {
  readonly revision: number;
  readonly assignment: LibraryFlowAssignment | null;
  readonly scan: {
    readonly id: string;
    readonly state: 'queued' | 'running' | 'completed' | 'failed';
    readonly detail: string;
    readonly completedAt: string | null;
  } | null;
  readonly files: readonly LibraryMediaFile[];
  readonly totalFiles?: number;
  readonly limit?: number;
  readonly offset?: number;
}

export interface LibraryProcessingSettings {
  readonly watchEnabled: boolean;
  readonly scanIntervalSeconds: number;
  readonly maxConcurrentTasks: number | null;
  readonly nodeLimits: readonly { nodeId: string; maxConcurrentTasks: number }[];
  readonly revision: number;
}
