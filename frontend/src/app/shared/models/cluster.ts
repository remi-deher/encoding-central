import { NodeSchedule } from './node-schedule';
export type NodeRole = 'server' | 'worker';
export interface NodeToolCapability {
  readonly encoders?: readonly string[] | null;
  readonly encoderChecks?: readonly NodeEncoderCapability[] | null;
  readonly toolId: string;
  readonly version: string;
  readonly mediaTypes: readonly string[];
  readonly accelerationTypes: readonly string[];
}
export interface NodeEncoderCapability {
  readonly encoderId: string;
  readonly status: 'usable' | 'detected';
}
export interface NodeResponse {
  readonly id: string;
  readonly name: string;
  readonly roles: readonly NodeRole[];
  readonly maxConcurrentTasks: number;
  readonly capabilities: readonly NodeToolCapability[];
  readonly status: 'online' | 'offline';
  readonly lastSeenAt: string;
  readonly inventory?: NodeInventory | null;
  readonly displayName?: string | null;
  readonly labels?: Readonly<Record<string, string | null>>;
  /** Mode demandé par un administrateur et pas encore appliqué par le nœud. */
  readonly desiredRoles?: readonly NodeRole[] | null;
  /** État de cycle de vie ; le plus restrictif l'emporte (ADR 0041). */
  readonly lifecycle?: 'online' | 'offline' | 'draining' | 'incompatible' | 'quarantined' | null;
  readonly lifecycleReason?: string | null;
}
export interface NodeInventory {
  readonly operatingSystem: string;
  readonly architecture: string;
  readonly processorCount: number;
  readonly memoryMb: number;
  /** Informations matérielles statiques, absentes des agents antérieurs. */
  readonly processorModel?: string | null;
  readonly gpus?: readonly NodeGpu[] | null;
  readonly fontSets?: readonly string[] | null;
  /** Environnement d’exécution signalé par l’agent (`docker`, `podman`, `container`, `native`). */
  readonly runtimeEnvironment?: string | null;
}
export interface NodeGpu {
  readonly name: string;
  readonly vendor?: string | null;
  readonly driver?: string | null;
  readonly device?: string | null;
}
export type NodeGroupMode = 'manual' | 'automatic';
export interface NodeGroupCriteria {
  readonly roles?: readonly NodeRole[] | null;
  readonly operatingSystem?: string | null;
  readonly architecture?: string | null;
  readonly minimumMemoryMb?: number | null;
  readonly requiredToolIds?: readonly string[] | null;
  readonly minimumConcurrentTasks?: number | null;
  readonly minimumProcessorCount?: number | null;
  readonly requiredEncoders?: readonly string[] | null;
}
export interface NodeGroupResponse {
  readonly id: string;
  readonly name: string;
  readonly description: string;
  readonly mode: NodeGroupMode;
  readonly criteria: NodeGroupCriteria;
  readonly labels: Readonly<Record<string, string | null>>;
  readonly nodeIds: readonly string[];
  readonly createdAt: string;
  readonly updatedAt: string;
}
export interface NodeIdentityResponse {
  readonly nodeId: string;
  readonly displayName: string | null;
  readonly labels: Readonly<Record<string, string | null>>;
  readonly effectiveLabels: Readonly<Record<string, string | null>>;
  readonly groupIds: readonly string[];
}
export interface LibraryResponse {
  readonly flowName?: string | null;
  readonly flowVersion?: number | null;
  readonly id: string;
  readonly name: string;
  readonly createdAt: string;
  readonly configuredNodeCount: number;
  readonly directAccessNodeCount: number;
  readonly transferNodeCount: number;
  readonly isActive: boolean;
}
export const NODE_ROLE_LABELS: Readonly<Record<NodeRole, string>> = {
  server: 'Serveur',
  worker: 'Worker',
};

export interface NodeActivityResponse {
  readonly active: number;
  readonly succeeded: number;
  readonly failed: number;
  readonly cancelled: number;
  readonly lastActivity: string | null;
  readonly sourceTasks: number;
}

export interface NodeProcessingSettings {
  readonly maxConcurrentTasks: number | null;
  readonly effectiveMaxConcurrentTasks: number;
  readonly revision: number;
  readonly schedule: NodeSchedule;
}

/**
 * Formate le nom d'un nœud en affichant son nom d'affichage et son nom d'hôte entre parenthèses
 * lorsqu'ils sont tous deux présents et différents (ex. "TRUENAS (ENCODING-50)").
 */
export function formatNodeLabel(name: string, displayName?: string | null): string {
  const host = name?.trim() ?? '';
  const display = displayName?.trim();
  if (display && host && !display.localeCompare(host, undefined, { sensitivity: 'accent' })) {
    return display;
  }
  if (display && host) {
    return `${display} (${host})`;
  }
  return display || host || 'Nœud inconnu';
}
