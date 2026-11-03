export type LibraryNodeAccessMode = 'direct' | 'unsupported' | 'transfer';

export interface LibraryNodeAccessResponse {
  readonly libraryId: string;
  readonly workerId: string;
  readonly workerName: string;
  readonly mode: LibraryNodeAccessMode;
  readonly isEnabled: boolean;
  readonly localPath: string | null;
  readonly canRead: boolean;
  readonly canWrite: boolean;
  readonly transferSourceWorkerId: string | null;
  readonly transferSourceWorkerName: string | null;
  readonly updatedAt: string;
}
