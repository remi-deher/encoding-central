import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { LibraryWorkflow, LibraryProcessingSettings } from '../models/library-workflow';
import { EnrollmentWindow, FirstLinkAcceptance } from '../models/auth';
import { LibraryWorkerAccessResponse } from '../ui/library-worker-access-types';
import { WorkerSchedule } from '../models/worker-schedule';
import { WorkerResponse, LibraryResponse, WorkerProcessingSettings } from '../models/central';
import { CentralIdentity } from '../models/local-worker';

@Injectable({ providedIn: 'root' })
export class CentralApi {
  private readonly http = inject(HttpClient);
  /** Identité du central : `standalone` vrai tant qu'aucun central n'a été créé et qu'un seul worker est actif (architecture 0045). */
  identity() {
    return this.http.get<CentralIdentity>('/api/v1/central');
  }
  listNodes() {
    return this.http.get<readonly WorkerResponse[]>('/api/v1/workers');
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
    return this.http.get<WorkerProcessingSettings>('/api/v1/workers/' + id + '/processing');
  }
  saveNodeProcessing(
    id: string,
    maxConcurrentTasks: number | null,
    expectedRevision: number,
    schedule: WorkerSchedule,
  ) {
    return this.http.put<void>('/api/v1/workers/' + id + '/processing', {
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
    return this.http.get<readonly LibraryWorkerAccessResponse[]>(
      '/api/v1/libraries/' + id + '/worker-access',
    );
  }
  enrollmentWindow() {
    return this.http.get<EnrollmentWindow>('/api/v1/workers/enrollment-window');
  }
  openEnrollmentWindow(minutes: number) {
    return this.http.put<EnrollmentWindow>('/api/v1/workers/enrollment-window', { minutes });
  }
  closeEnrollmentWindow() {
    return this.http.delete<void>('/api/v1/workers/enrollment-window');
  }
  firstLinkAcceptance() {
    return this.http.get<FirstLinkAcceptance>('/api/v1/workers/first-link');
  }
  enableFirstLinkAcceptance(minutes: number) {
    return this.http.put<FirstLinkAcceptance>('/api/v1/workers/first-link', { minutes });
  }
  disableFirstLinkAcceptance() {
    return this.http.delete<void>('/api/v1/workers/first-link');
  }
}
