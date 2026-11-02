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
  Subject,
  timer,
  timeout,
} from 'rxjs';
import { ExecutionResponse } from '../../shared/models/execution';
import { ExecutionTracking } from '../../shared/services/execution-tracking';

interface HomeExecutionState {
  readonly status: 'loading' | 'ready' | 'error';
  readonly active: readonly ExecutionResponse[];
  readonly publications: readonly ExecutionResponse[];
}
export const INITIAL_HOME_EXECUTIONS: HomeExecutionState = {
  status: 'loading',
  active: [],
  publications: [],
};

/** Filtered queries keep the dashboard independent of the global journal's page limit. */
@Injectable()
export class HomeExecutions {
  private readonly tracking = inject(ExecutionTracking);
  private readonly refreshRequested = new Subject<void>();
  readonly state$ = merge(timer(0, 10000), this.refreshRequested).pipe(
    exhaustMap(() =>
      forkJoin({
        running: this.tracking.overview({ state: 'running', limit: 3 }),
        succeeded: this.tracking.overview({ state: 'succeeded', limit: 5 }),
      }).pipe(
        timeout(8000),
        map(({ running, succeeded }): HomeExecutionState => ({
          status: 'ready',
          active: (running.executions ?? [])
            .filter((execution) => execution.isActivelyProcessing === true)
            .sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))
            .slice(0, 3),
          publications: succeeded.executions ?? [],
        })),
        catchError(() => of({ ...INITIAL_HOME_EXECUTIONS, status: 'error' } as HomeExecutionState)),
      ),
    ),
    scan(
      (previous: HomeExecutionState, next: HomeExecutionState) =>
        next.status === 'error' ? { ...previous, status: 'error' as const } : next,
      INITIAL_HOME_EXECUTIONS,
    ),
    shareReplay({ bufferSize: 1, refCount: true }),
  );
  refresh(): void {
    this.refreshRequested.next();
  }
}
