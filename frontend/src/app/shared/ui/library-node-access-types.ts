export type LibraryNodeAccessMode = 'direct' | 'unsupported' | 'transfer';

export interface LibraryNodeAccessResponse {
  readonly libraryId: string;
  readonly nodeId: string;
  readonly nodeName: string;
  readonly mode: LibraryNodeAccessMode;
  readonly isEnabled: boolean;
  readonly localPath: string | null;
  readonly canRead: boolean;
  readonly canWrite: boolean;
  readonly transferSourceNodeId: string | null;
  readonly transferSourceNodeName: string | null;
  readonly updatedAt: string;
}
