import {
  NodeEncoderCapability,
  NodeInventory,
  NodeRole,
  NodeToolCapability,
} from './cluster';

export interface SetupNodeAddress {
  readonly interface: string;
  readonly address: string;
}

export interface SetupNodeTool {
  readonly toolId: string;
  readonly accelerationTypes: readonly string[];
  readonly encoderChecks: readonly NodeEncoderCapability[];
}

/** Fiche du nœud local pour l'assistant de premier démarrage (`/api/v1/auth/setup/node`), sans secret ni identifiant de cluster. */
export interface SetupNodeSheet {
  readonly name: string | null;
  readonly displayName: string | null;
  readonly agentAvailable: boolean;
  readonly inventory: NodeInventory | null;
  readonly tools: readonly SetupNodeTool[] | null;
  readonly addresses: readonly SetupNodeAddress[];
  readonly urls: readonly string[];
}

export interface ClusterIdentity {
  readonly id: string;
  readonly name: string;
  readonly standalone: boolean;
}

export interface DiscoveredCluster {
  readonly id: string;
  readonly name: string;
  readonly apiBaseUrl: string;
  readonly discoveredAt: string;
  readonly source?: 'network' | 'manual' | 'configured';
  readonly reachable?: boolean;
}

export interface LocalNodeStatus {
  readonly nodeId: string;
  readonly name: string;
  readonly displayName?: string | null;
  readonly roles: readonly NodeRole[];
  readonly maxConcurrentTasks: number;
  readonly automaticEnrollmentEnabled: boolean;
  readonly capabilities: readonly NodeToolCapability[];
  /** Matériel lu par l'agent local : disponible même sans serveur local ni liste de nœuds. */
  readonly inventory?: NodeInventory | null;
  readonly isEnrolled: boolean;
  readonly standalone?: boolean;
  readonly pairingKey: string | null;
  readonly pairingExpiresAt: string | null;
  readonly clusterId: string | null;
  readonly clusterName: string | null;
  readonly verificationCode: string | null;
  readonly switching: {
    readonly previousClusterName: string | null;
    readonly deadline: string | null;
  } | null;
  readonly switchFailure: string | null;
}

export interface LocalNodePairing {
  readonly pairingKey: string;
  readonly expiresAt: string;
  readonly verificationCode?: string | null;
}
