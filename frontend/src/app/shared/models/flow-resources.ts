export interface FlowResourceFile {
  readonly name: string;
  readonly sha256: string;
  readonly bytes: number;
}

export interface FlowResourceVersion {
  readonly resourceId: string;
  readonly version: number;
  readonly files: readonly FlowResourceFile[];
  readonly totalBytes: number;
  readonly createdAt: string;
}

export interface FlowResourceSummary {
  readonly id: string;
  readonly kind: string;
  readonly name: string;
  readonly description: string;
  readonly latestVersion: number;
  readonly totalBytes: number;
  readonly updatedAt: string;
}

export interface FlowResourceDetails {
  readonly summary: FlowResourceSummary;
  readonly versions: readonly FlowResourceVersion[];
}

export interface FlowResourceFileReference {
  readonly name: string;
  readonly sha256: string;
}
