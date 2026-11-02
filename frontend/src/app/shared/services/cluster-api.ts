import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { LibraryWorkflow, LibraryProcessingSettings } from '../models/library-workflow';
import { EnrollmentWindow, FirstLinkAcceptance } from '../models/auth';
import { LibraryNodeAccessResponse } from '../ui/library-node-access-types';
import { NodeSchedule } from '../models/node-schedule';
import { NodeResponse, LibraryResponse, NodeProcessingSettings } from '../models/cluster';
import { ClusterIdentity } from '../models/local-node';

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
