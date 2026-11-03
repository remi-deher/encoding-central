export type LibraryWorkerAccessMode = 'direct' | 'unsupported' | 'transfer';

export interface LibraryWorkerAccessResponse {
  readonly libraryId: string;
  readonly workerId: string;
  readonly workerName: string;
  readonly mode: LibraryWorkerAccessMode;
  readonly isEnabled: boolean;
  readonly localPath: string | null;
  readonly canRead: boolean;
  readonly canWrite: boolean;
  readonly transferSourceWorkerId: string | null;
  readonly transferSourceWorkerName: string | null;
  readonly updatedAt: string;
}
