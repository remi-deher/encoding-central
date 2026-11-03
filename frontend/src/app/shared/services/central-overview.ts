import { inject, Injectable } from '@angular/core';
import {
  catchError,
  exhaustMap,
  forkJoin,
  map,
  merge,
  of,
  scan,
  shareReplay,
  switchMap,
  Subject,
  timeout,
} from 'rxjs';
import { CentralApi } from './central-api';
import { LiveUpdates } from './live-updates';
import { LibraryResponse, WorkerResponse } from '../models/central';
import { CentralIdentity } from '../models/local-worker';
import { LibraryWorkerAccessResponse } from '../ui/library-worker-access-types';

export interface CentralSnapshot {
  readonly identity: CentralIdentity;
  readonly nodes: readonly WorkerResponse[];
  readonly libraries: readonly LibraryResponse[];
  /** Accès de chaque worker à chaque bibliothèque (liste vide si indisponible). */
  readonly accesses: readonly LibraryWorkerAccessResponse[];
  readonly observedAt: Date;
}
export interface CentralOverviewState {
  readonly status: 'loading' | 'ready' | 'error';
  readonly snapshot: CentralSnapshot | null;
}
export const INITIAL_CLUSTER_OVERVIEW: CentralOverviewState = { status: 'loading', snapshot: null };

@Injectable({ providedIn: 'root' })
export class CentralOverview {
  private readonly api = inject(CentralApi);
  private readonly live = inject(LiveUpdates);
  private readonly refreshRequested = new Subject<void>();
  readonly state$ = merge(this.live.drive(['workers', 'libraries', 'executions']), this.refreshRequested).pipe(
    exhaustMap(() =>
      forkJoin({
        identity: this.api.identity(),
        nodes: this.api.listNodes(),
        libraries: this.api.listLibraries(),
      }).pipe(
        switchMap((data) =>
          (data.libraries.length
            ? forkJoin(
                data.libraries.map((library) =>
                  this.api
                    .libraryNodeAccess(library.id)
                    .pipe(catchError(() => of([] as readonly LibraryWorkerAccessResponse[]))),
                ),
              )
            : of([] as (readonly LibraryWorkerAccessResponse[])[])
          ).pipe(map((lists) => ({ ...data, accesses: lists.flat() }))),
        ),
        timeout(8000),
        map(
          (data) =>
            ({
              status: 'ready',
              snapshot: { ...data, observedAt: new Date() },
            }) as CentralOverviewState,
        ),
        catchError(() => of({ status: 'error', snapshot: null } as CentralOverviewState)),
      ),
    ),
    scan(
      (previous: CentralOverviewState, next: CentralOverviewState) =>
        next.status === 'error' ? { ...next, snapshot: previous.snapshot } : next,
      INITIAL_CLUSTER_OVERVIEW,
    ),
    shareReplay({ bufferSize: 1, refCount: true }),
  );
  refresh(): void {
    this.refreshRequested.next();
  }
}
