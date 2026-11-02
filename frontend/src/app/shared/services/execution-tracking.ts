import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import {
  catchError,
  exhaustMap,
  forkJoin,
  filter,
  map,
  merge,
  Observable,
  of,
  scan,
  shareReplay,
  Subject,
  timeout,
  timer,
} from 'rxjs';
import {
  ExecutionOverviewResponse,
  ExecutionEventResponse,
  ExecutionPipelineStep,
  ExecutionFilterQuery,
  ExecutionResponse,
  ExecutionState,
} from '../models/execution';
import { FlowDocument } from '../models/flow-validation';

export interface ExecutionTrackingState {
  readonly status: 'loading' | 'ready' | 'error';
  readonly data: ExecutionOverviewResponse | null;
  readonly moreStates?: readonly ExecutionState[];
}
export const INITIAL_EXECUTION_TRACKING: ExecutionTrackingState = { status: 'loading', data: null };

@Injectable({ providedIn: 'root' })
export class ExecutionTracking {
  private readonly http = inject(HttpClient);
  private readonly refreshRequested = new Subject<void>();
  private lastSseUpdate = 0;
  private readonly sseUpdates$ = new Observable<ExecutionTrackingState>((subscriber) => {
    if (typeof EventSource === 'undefined') {
      subscriber.complete();
      return undefined;
    }
    let source: EventSource | null = null;
    try {
      source = new EventSource('/api/v1/executions/stream', { withCredentials: true });
      source.addEventListener('overview', (event: MessageEvent<string>) => {
        try {
          const data = JSON.parse(event.data) as ExecutionOverviewResponse;
          if (!Array.isArray(data.executions)) return;
          this.lastSseUpdate = Date.now();
          subscriber.next({ status: 'ready', data });
        } catch {
          // Ignore malformed event payloads; polling remains active as fallback.
        }
      });
      // EventSource reconnects automatically; HTTP polling takes over while the stream is stale.
    } catch {
      source?.close();
      this.lastSseUpdate = 0;
      source = null;
    }
    return () => {
      source?.close();
      this.lastSseUpdate = 0;
    };
  });

  readonly state$ = merge(
    merge(timer(0, 5000), this.refreshRequested).pipe(
      filter(() => Date.now() - this.lastSseUpdate > 15000),
      exhaustMap(() =>
        this.overview().pipe(
          timeout(8000),
          map((data) => ({ status: 'ready', data }) as ExecutionTrackingState),
          catchError(() => of({ status: 'error', data: null } as ExecutionTrackingState)),
        ),
      ),
    ),
    this.sseUpdates$,
  ).pipe(
    scan((previous: ExecutionTrackingState, next: ExecutionTrackingState) => {
      if (next.status === 'error')
        return Date.now() - this.lastSseUpdate < 15000
          ? previous
          : { ...next, data: previous.data };
      return previous.data &&
        next.data &&
        Date.parse(previous.data.observedAt) > Date.parse(next.data.observedAt)
        ? previous
        : next;
    }, INITIAL_EXECUTION_TRACKING),
    shareReplay({ bufferSize: 1, refCount: true }),
  );

  overview(query?: ExecutionFilterQuery) {
    let params = new HttpParams();
    if (query?.state) params = params.set('state', query.state);
    if (query?.libraryId) params = params.set('libraryId', query.libraryId);
    if (query?.nodeId) params = params.set('nodeId', query.nodeId);
    if (query?.since) params = params.set('since', query.since);
    if (query?.limit !== undefined) params = params.set('limit', String(query.limit));
    if (query?.offset !== undefined) params = params.set('offset', String(query.offset));
    return this.http.get<ExecutionOverviewResponse>('/api/v1/executions', { params }).pipe(
      map((data) => {
        if (!Array.isArray(data.executions))
          throw new Error('Réponse de suivi des traitements invalide.');
        return data;
      }),
    );
  }

  execution(id: string) {
    return this.http.get<ExecutionResponse>('/api/v1/executions/' + encodeURIComponent(id));
  }

  /** Fetch each requested state independently so a large queue cannot hide history or active work. */
  observeStates(
    states: readonly ExecutionState[],
    limit = 25,
    offset = 0,
  ): Observable<ExecutionTrackingState> {
    if (!states.length) return of({ status: 'ready', data: null, moreStates: [] });
    const known = new Map<ExecutionState, ExecutionOverviewResponse>();
    return merge(timer(0, 10000), this.refreshRequested).pipe(
      exhaustMap(() =>
        forkJoin(
          states.map((state) =>
            this.overview({ state, limit, offset }).pipe(
              timeout(20000),
              map((data) => ({ state, data })),
              catchError(() => of({ state, data: null })),
            ),
          ),
        ),
      ),
      map((results): ExecutionTrackingState => {
        for (const result of results) if (result.data) known.set(result.state, result.data);
        const pages = states.flatMap((state) => (known.has(state) ? [known.get(state)!] : []));
        return {
          status: results.every((result) => result.data) ? 'ready' : 'error',
          moreStates: states.filter((state) => {
            const page = known.get(state);
            return (
              page && (page.totalCount ?? page.executions.length) > offset + page.executions.length
            );
          }),
          data: pages.length
            ? {
                ...pages[0],
                executions: pages.flatMap((page) => page.executions),
                totalCount: pages.reduce(
                  (total, page) => total + (page.totalCount ?? page.executions.length),
                  0,
                ),
                limit,
                offset,
              }
            : null,
        };
      }),
    );
  }

  events(id: string, options?: { readonly afterRevision?: number; readonly limit?: number }) {
    let params = new HttpParams();
    if (options?.afterRevision !== undefined)
      params = params.set('afterRevision', String(options.afterRevision));
    if (options?.limit !== undefined) params = params.set('limit', String(options.limit));
    return this.http.get<readonly ExecutionEventResponse[]>(
      '/api/v1/executions/' + encodeURIComponent(id) + '/events',
      { params },
    );
  }
  pipeline(id: string) {
    return this.http.get<readonly ExecutionPipelineStep[]>(
      '/api/v1/executions/' + encodeURIComponent(id) + '/pipeline',
    );
  }
  flow(id: string) {
    return this.http.get<FlowDocument>(
      '/api/v1/executions/' + encodeURIComponent(id) + '/flow',
    );
  }
  artifacts(id: string) {
    return this.http.get<readonly { sha256: string; name: string; bytes: number }[]>(
      '/api/v1/executions/' + encodeURIComponent(id) + '/artifacts',
    );
  }
  cancel(id: string) {
    return this.http.post<void>('/api/v1/executions/' + encodeURIComponent(id) + '/cancel', {});
  }
  retry(id: string) {
    return this.http.post<{ id: string }>(
      '/api/v1/executions/' + encodeURIComponent(id) + '/retry',
      {},
    );
  }
  resume(id: string) {
    return this.http.post<{ id: string }>(
      '/api/v1/executions/' + encodeURIComponent(id) + '/resume',
      {},
    );
  }
  refresh() {
    this.refreshRequested.next();
  }
}
