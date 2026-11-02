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
  timer,
} from 'rxjs';
import { ClusterApi } from './cluster-api';
import { LibraryResponse, NodeResponse } from '../models/cluster';
import { ClusterIdentity } from '../models/local-node';
import { LibraryNodeAccessResponse } from '../ui/library-node-access-types';

export interface ClusterSnapshot {
  readonly identity: ClusterIdentity;
  readonly nodes: readonly NodeResponse[];
  readonly libraries: readonly LibraryResponse[];
  /** Accès de chaque nœud à chaque bibliothèque (liste vide si indisponible). */
  readonly accesses: readonly LibraryNodeAccessResponse[];
  readonly observedAt: Date;
}
export interface ClusterOverviewState {
  readonly status: 'loading' | 'ready' | 'error';
  readonly snapshot: ClusterSnapshot | null;
}
export const INITIAL_CLUSTER_OVERVIEW: ClusterOverviewState = { status: 'loading', snapshot: null };

@Injectable({ providedIn: 'root' })
export class ClusterOverview {
  private readonly api = inject(ClusterApi);
  private readonly refreshRequested = new Subject<void>();
  readonly state$ = merge(timer(0, 10000), this.refreshRequested).pipe(
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
                    .pipe(catchError(() => of([] as readonly LibraryNodeAccessResponse[]))),
                ),
              )
            : of([] as (readonly LibraryNodeAccessResponse[])[])
          ).pipe(map((lists) => ({ ...data, accesses: lists.flat() }))),
        ),
        timeout(8000),
        map(
          (data) =>
            ({
              status: 'ready',
              snapshot: { ...data, observedAt: new Date() },
            }) as ClusterOverviewState,
        ),
        catchError(() => of({ status: 'error', snapshot: null } as ClusterOverviewState)),
      ),
    ),
    scan(
      (previous: ClusterOverviewState, next: ClusterOverviewState) =>
        next.status === 'error' ? { ...next, snapshot: previous.snapshot } : next,
      INITIAL_CLUSTER_OVERVIEW,
    ),
    shareReplay({ bufferSize: 1, refCount: true }),
  );
  refresh(): void {
    this.refreshRequested.next();
  }
}
