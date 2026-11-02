import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { LibraryWorkflow, LibraryProcessingSettings } from '../models/library-workflow';
import { EnrollmentWindow, FirstLinkAcceptance } from '../models/auth';
import { LibraryNodeAccessResponse } from '../ui/library-node-access-types';
import { NodeSchedule } from '../models/node-schedule';
import { NodeResponse, LibraryResponse, NodeProcessingSettings } from '../models/cluster';
import { ClusterIdentity } from '../models/local-node';

export interface ClusterReconciliationPeerHealth {
  readonly authenticatedPeerId?: string | null;
  readonly catchUpConfirmed?: boolean;
  readonly url: string;
  readonly configured: boolean;
  readonly discovered: boolean;
  readonly lastAttemptAt: string | null;
  readonly lastSuccessAt: string | null;
  readonly lastError: string | null;
}

export interface ClusterProjectionDiagnostic {
  readonly eventId: string;
  readonly table: string;
  readonly cause: string;
  readonly firstFailedAt: string;
  readonly lastFailedAt: string;
  readonly attempts: number;
  readonly action: string;
}

export interface ClusterReconciliationHealthStatus {
  readonly projections?: readonly ClusterProjectionDiagnostic[];
  readonly attributionBlockers?: readonly string[];
  readonly enabled: boolean;
  readonly disabledReason: string | null;
  readonly discoveryAvailable: boolean;
  readonly discoveryError: string | null;
  readonly peers: readonly ClusterReconciliationPeerHealth[];
}

export interface ClusterTaskStatus {
  readonly localPeerId?: string;
  readonly neutralized?: boolean;
  readonly catchingUp?: boolean;
  readonly pendingCheckpointPreparations?: number;
  readonly pendingCheckpointCaches?: number;
  readonly isolated: boolean;
  readonly waitingSourceWrites: number;
  readonly pendingTransfers: number;
  readonly observations: readonly {
    readonly observerPeerId: string;
    readonly subjectPeerId: string;
    readonly sequence: number;
    readonly observedAt: string;
    readonly expiresAt: string;
    readonly reachable: boolean;
  }[];
}

export interface ClusterTaskActionResult {
  readonly success: boolean;
  readonly detail: string;
  readonly transferId?: string | null;
}

@Injectable({ providedIn: 'root' })
export class ClusterApi {
  private readonly http = inject(HttpClient);
  /** Identité du cluster : `standalone` vrai tant qu'aucun cluster n'a été créé et qu'un seul nœud est actif (architecture 0045). */
  identity() {
    return this.http.get<ClusterIdentity>('/api/v1/cluster');
  }
  listNodes() {
    return this.http.get<readonly NodeResponse[]>('/api/v1/nodes');
  }
  taskStatus() {
    return this.http.get<ClusterTaskStatus>('/api/v1/cluster/tasks/status');
  }
  forceSourceRetry(id: string, confirmedStopped: boolean, reason: string) {
    return this.http.post<ClusterTaskActionResult>(
      '/api/v1/cluster/tasks/' + encodeURIComponent(id) + '/force-source-retry',
      { confirmedStopped, reason },
    );
  }
  reconciliationStatus() {
    return this.http.get<ClusterReconciliationHealthStatus>(
      '/api/v1/cluster/reconciliation/status',
    );
  }
  libraryProcessing(id: string) {
    return this.http.get<LibraryProcessingSettings>('/api/v1/libraries/' + id + '/processing');
  }
  saveLibraryProcessing(
    id: string,
    settings: Omit<LibraryProcessingSettings, 'revision'>,
    expectedRevision: number,
  ) {
    return this.http.put<void>('/api/v1/libraries/' + id + '/processing', {
      ...settings,
      expectedRevision,
    });
  }
  nodeProcessing(id: string) {
    return this.http.get<NodeProcessingSettings>('/api/v1/nodes/' + id + '/processing');
  }
  saveNodeProcessing(
    id: string,
    maxConcurrentTasks: number | null,
    expectedRevision: number,
    schedule: NodeSchedule,
  ) {
    return this.http.put<void>('/api/v1/nodes/' + id + '/processing', {
      maxConcurrentTasks,
      expectedRevision,
      schedule,
    });
  }
  libraryWorkflow(
    id: string,
    options?: { readonly search?: string; readonly limit?: number; readonly offset?: number },
  ) {
    let params = new HttpParams();
    if (options?.search) params = params.set('search', options.search);
    if (options?.limit !== undefined) params = params.set('limit', String(options.limit));
    if (options?.offset !== undefined) params = params.set('offset', String(options.offset));
    return this.http.get<LibraryWorkflow>('/api/v1/libraries/' + id + '/workflow', { params });
  }
  assignLibraryFlow(
    id: string,
    flowId: string | null,
    version: number | null,
    expectedRevision: number,
  ) {
    return this.http.put<void>('/api/v1/libraries/' + id + '/flow', {
      flowId,
      version,
      expectedRevision,
    });
  }
  discoverLibrary(id: string) {
    return this.http.post<{ id: string }>('/api/v1/libraries/' + id + '/discover', {});
  }
  enqueueLibraryFiles(id: string, fileIds: readonly string[], assignmentRevision: number) {
    return this.http.post<{ queued: number; skipped: number }>(
      '/api/v1/libraries/' + id + '/executions',
      { fileIds, assignmentRevision },
    );
  }
  listLibraries() {
    return this.http.get<readonly LibraryResponse[]>('/api/v1/libraries');
  }
  libraryNodeAccess(id: string) {
    return this.http.get<readonly LibraryNodeAccessResponse[]>(
      '/api/v1/libraries/' + id + '/node-access',
    );
  }
  enrollmentWindow() {
    return this.http.get<EnrollmentWindow>('/api/v1/nodes/enrollment-window');
  }
  openEnrollmentWindow(minutes: number) {
    return this.http.put<EnrollmentWindow>('/api/v1/nodes/enrollment-window', { minutes });
  }
  closeEnrollmentWindow() {
    return this.http.delete<void>('/api/v1/nodes/enrollment-window');
  }
  firstLinkAcceptance() {
    return this.http.get<FirstLinkAcceptance>('/api/v1/nodes/first-link');
  }
  enableFirstLinkAcceptance(minutes: number) {
    return this.http.put<FirstLinkAcceptance>('/api/v1/nodes/first-link', { minutes });
  }
  disableFirstLinkAcceptance() {
    return this.http.delete<void>('/api/v1/nodes/first-link');
  }
}
