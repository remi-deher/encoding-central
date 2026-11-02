export type ExtensionTab = 'plugins' | 'tools' | 'store';

export interface ClusterTool {
  readonly id: string;
  readonly enabled: boolean;
  readonly name: string;
  readonly role: string;
  readonly provider: 'stock' | 'custom' | 'managed';
  readonly stockVersion: string;
  readonly customPath?: string | null;
  readonly conformanceStatus: 'ready' | 'pending' | 'failed';
  readonly probePreserved: boolean;
  readonly executables: readonly string[];
  readonly mediaTypes: readonly string[];
  readonly encoders: readonly {
    readonly id: string;
    readonly codec: string;
    readonly name: string;
    readonly acceleration: string;
    readonly qualityMode: string;
  }[];
  readonly clusterReadiness: string;
}

export interface StoreRepository {
  readonly id: string;
  readonly name: string;
  readonly url: string;
  readonly description: string;
  readonly isOfficial: boolean;
  readonly isLocked: boolean;
  readonly enabled: boolean;
  readonly packagesCount: number;
  readonly updatedAt?: string | null;
  readonly lastError?: string | null;
}

export interface StorePackage {
  readonly id: string;
  readonly name: string;
  readonly kind: 'plugin' | 'tool';
  readonly platform?: string | null;
  readonly sha256?: string | null;
  readonly installedSha256?: string | null;
  readonly category: string;
  readonly categoryLabel: string;
  readonly version: string;
  readonly installedVersion?: string | null;
  readonly hasUpdate: boolean;
  readonly author: string;
  readonly description: string;
  readonly requiredTools: readonly string[];
  readonly repositoryId: string;
  readonly icon: string;
}

export interface RejectedPlugin {
  readonly file: string;
  readonly reason: string;
}
