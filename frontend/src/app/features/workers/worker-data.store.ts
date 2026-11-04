import { HttpClient } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, EMPTY, forkJoin, of, switchMap } from 'rxjs';
import { LiveUpdates } from '../../shared/services/live-updates';
import { CentralApi } from '../../shared/services/central-api';
import { NotificationService } from '../../shared/services/notification.service';
import { ShellConfig } from '../../core/shell/shell-config';
import { LocalWorkerController } from '../../shared/services/local-worker-controller';
import { LibraryResponse, WorkerResponse } from '../../shared/models/central';
import { DataTableRow } from '../../shared/ui/data-table.component';
import { LibraryWorkerAccessResponse } from '../../shared/ui/library-worker-access-types';
import {
  PendingWorkerEnrollmentResponse,
  toWorkerRecord,
  WorkerRecord,
} from './worker-catalog.utils';

export type WorkerActionType = 'enrollment' | 'library-access' | 'create-library' | 'identity';

/**
 * État brut partagé de la page Workers : membres du central, demandes de rattachement,
 * accès aux bibliothèques, sélection et tiroir d’action. Les stores métiers
 * (membres, détail, enrôlement, groupes) s’appuient dessus.
 */
@Injectable()
export class WorkerDataStore {
  private readonly http = inject(HttpClient);
  private readonly clusterApi = inject(CentralApi);
  private readonly live = inject(LiveUpdates);
  private readonly notifications = inject(NotificationService);
  readonly local = inject(LocalWorkerController);

  readonly nodes = signal<readonly WorkerResponse[]>([]);
  readonly pendingEnrollments = signal<readonly PendingWorkerEnrollmentResponse[]>([]);
  readonly activeTasks = signal<Readonly<Record<string, number>>>({});
  private readonly activityLoaded = signal(false);
  readonly loadState = signal<'loading' | 'ready' | 'error'>('loading');
  readonly allLibraries = signal<readonly LibraryResponse[]>([]);
  readonly nodeAccessByLibrary = signal<
    ReadonlyMap<string, readonly LibraryWorkerAccessResponse[]>
  >(new Map());
  private readonly libraryAccessLoadState = signal<'loading' | 'ready' | 'error'>('loading');
  readonly libraryAccessState = computed(() => this.libraryAccessLoadState());

  readonly selectedNode = signal<WorkerRecord | null>(null);
  readonly selectedNodeTabIndex = signal(0);
  readonly nodeLibraryLoading = signal(false);
  readonly actionOpen = signal(false);
  readonly actionType = signal<WorkerActionType>('enrollment');

  readonly localNode = computed(
    () => this.nodes().find((node) => node.id === this.local.status()?.workerId) ?? null,
  );

  readonly localName = computed(
    () =>
      this.localNode()?.displayName?.trim() ||
      this.local.status()?.displayName?.trim() ||
      this.local.status()?.name ||
      'Ce worker',
  );

  constructor() {
    // La liste des workers suit le flux SSE du central (sujet « workers ») : rechargée à l'ouverture,
    // puis à chaque changement annoncé, sans attendre ni recharger la page.
    this.live
      .drive(['workers'])
      .pipe(
        switchMap(() =>
          forkJoin({
            nodes: this.clusterApi.listNodes(),
            pending: this.http.get<readonly PendingWorkerEnrollmentResponse[]>(
              '/api/v1/workers/enrollment-requests',
            ),
          }).pipe(
            catchError(() => {
              this.loadState.set(this.nodes().length ? 'ready' : 'error');
              return EMPTY;
            }),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe(({ nodes, pending }) => {
        this.nodes.set(nodes);
        this.pendingEnrollments.set(pending);
        this.loadState.set('ready');
      });
    // Le nombre de tâches actives suit les exécutions et la présence des workers.
    this.live
      .drive(['executions', 'workers'])
      .pipe(
        switchMap(() =>
          this.http
            .get<Readonly<Record<string, number>>>('/api/v1/workers/activity')
            .pipe(catchError(() => EMPTY)),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((activity) => {
        this.activeTasks.set(activity);
        this.activityLoaded.set(true);
      });
    // L'image worker ne montre pas les bibliothèques du central.
    if (!inject(ShellConfig).worker()) this.loadLibraryAccessSummary();
  }

  activityKnown(): boolean {
    return this.activityLoaded();
  }

  toRecord(node: WorkerResponse): WorkerRecord {
    return toWorkerRecord(node, {
      activityKnown: this.activityKnown(),
      activeTasks: this.activeTasks(),
      libraryAccessLoadState: this.libraryAccessLoadState(),
      allLibraries: this.allLibraries(),
      nodeAccessByLibrary: this.nodeAccessByLibrary(),
    });
  }

  openNodeCard(id: string): void {
    const node = this.nodes().find((node) => node.id === id);
    this.selectedNode.set(node ? this.toRecord(node) : null);
  }

  onNodeOpen(row: DataTableRow): void {
    const node = this.nodes().find((node) => node.id === row['id']);
    this.selectedNode.set(node ? this.toRecord(node) : null);
    this.selectedNodeTabIndex.set(0);
    this.loadNodeLibraries();
  }

  onDetailClosed(): void {
    this.selectedNode.set(null);
    this.selectedNodeTabIndex.set(0);
  }

  loadNodeLibraries(): void {
    const node = this.selectedNode();
    if (!node) return;
    this.nodeLibraryLoading.set(true);
    this.clusterApi
      .listLibraries()
      .pipe(
        switchMap((libraries) =>
          forkJoin({
            libraries: Promise.resolve(libraries),
            accessLists: libraries.length
              ? forkJoin(
                  libraries.map((library) =>
                    this.http.get<readonly LibraryWorkerAccessResponse[]>(
                      `/api/v1/libraries/${library.id}/worker-access`,
                    ),
                  ),
                )
              : Promise.resolve([] as readonly (readonly LibraryWorkerAccessResponse[])[]),
          }),
        ),
        catchError(() => {
          this.nodeLibraryLoading.set(false);
          this.notifications.show(
            'Les bibliothèques autorisées pour ce worker n’ont pas pu être chargées.',
          );
          return EMPTY;
        }),
      )
      .subscribe(({ libraries, accessLists }) => {
        this.allLibraries.set(libraries);
        this.nodeAccessByLibrary.set(
          new Map(libraries.map((library, index) => [library.id, accessLists[index]])),
        );
        this.libraryAccessLoadState.set('ready');
        this.nodeLibraryLoading.set(false);
      });
  }

  loadLibraryAccessSummary(): void {
    this.clusterApi
      .listLibraries()
      .pipe(
        switchMap((libraries) =>
          forkJoin({
            libraries: of(libraries),
            accessLists: libraries.length
              ? forkJoin(
                  libraries.map((library) =>
                    this.http.get<readonly LibraryWorkerAccessResponse[]>(
                      `/api/v1/libraries/${library.id}/worker-access`,
                    ),
                  ),
                )
              : of([] as readonly (readonly LibraryWorkerAccessResponse[])[]),
          }),
        ),
        catchError(() => {
          this.libraryAccessLoadState.set('error');
          return EMPTY;
        }),
      )
      .subscribe(({ libraries, accessLists }) => {
        this.allLibraries.set(libraries);
        this.nodeAccessByLibrary.set(
          new Map(libraries.map((library, index) => [library.id, accessLists[index]])),
        );
        this.libraryAccessLoadState.set('ready');
      });
  }
}
