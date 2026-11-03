import {
  WorkerEncoderCapability,
  WorkerInventory,
  WorkerRole,
  WorkerToolCapability,
} from './central';

export interface SetupWorkerAddress {
  readonly interface: string;
  readonly address: string;
}

export interface SetupWorkerTool {
  readonly toolId: string;
  readonly accelerationTypes: readonly string[];
  readonly encoderChecks: readonly WorkerEncoderCapability[];
}

/** Fiche du worker local pour l'assistant de premier démarrage (`/api/v1/auth/setup/worker`), sans secret ni identifiant de central. */
export interface SetupWorkerSheet {
  readonly name: string | null;
  readonly displayName: string | null;
  readonly agentAvailable: boolean;
  readonly inventory: WorkerInventory | null;
  readonly tools: readonly SetupWorkerTool[] | null;
  readonly addresses: readonly SetupWorkerAddress[];
  readonly urls: readonly string[];
}

export interface CentralIdentity {
  readonly id: string;
  readonly name: string;
}

export interface DiscoveredCentral {
  readonly id: string;
  readonly name: string;
  readonly apiBaseUrl: string;
  readonly discoveredAt: string;
  readonly source?: 'network' | 'manual' | 'configured';
  readonly reachable?: boolean;
}

export interface LocalWorkerStatus {
  readonly workerId: string;
  readonly name: string;
  readonly displayName?: string | null;
  readonly roles: readonly WorkerRole[];
  readonly maxConcurrentTasks: number;
  readonly automaticEnrollmentEnabled: boolean;
  readonly capabilities: readonly WorkerToolCapability[];
  /** Matériel lu par l'agent local : disponible même sans serveur local ni liste de workers. */
  readonly inventory?: WorkerInventory | null;
  readonly isEnrolled: boolean;
  readonly pairingKey: string | null;
  readonly pairingExpiresAt: string | null;
  readonly centralId: string | null;
  readonly centralName: string | null;
  readonly verificationCode: string | null;
  readonly enrollmentFailure: string | null;
}

export interface LocalWorkerPairing {
  readonly pairingKey: string;
  readonly expiresAt: string;
  readonly verificationCode?: string | null;
}
