import { Injectable } from '@angular/core';
import {
  Observable,
  catchError,
  combineLatest,
  exhaustMap,
  map,
  of,
  startWith,
  switchMap,
  timer,
} from 'rxjs';
import { DataTableRow } from '../ui/data-table.component';
import { ExecutionPipelineStep } from '../models/execution';
import { EXECUTION_STATE_LABELS } from '../models/execution';
import { formatFileSize } from '../models/file-size';
import { ExecutionTracking } from './execution-tracking';

export interface ExecutionDetailRows {
  readonly message: string;
  readonly rows: readonly DataTableRow[];
}

export interface ExecutionPipelineState {
  readonly message: string;
  readonly steps: readonly ExecutionPipelineStep[];
}

export interface ExecutionDetailPollingState {
  readonly events: Observable<ExecutionDetailRows>;
  readonly artifacts: Observable<ExecutionDetailRows>;
  readonly pipeline: Observable<ExecutionPipelineState>;
}

/** Charge les détails uniquement tant que leur onglet est ouvert dans la fiche d'exécution. */
@Injectable({ providedIn: 'root' })
export class ExecutionDetailPolling {
  constructor(private readonly tracking: ExecutionTracking) {}

  observe(
    executionId$: Observable<string | null>,
    tabIndex$: Observable<number>,
  ): ExecutionDetailPollingState {
    const activeDetails$ = combineLatest([executionId$, tabIndex$]);

    return {
      events: activeDetails$.pipe(
        switchMap(([id, tab]) =>
          id && tab === 4
            ? timer(0, 5000).pipe(
                exhaustMap(() =>
                  this.tracking.events(id).pipe(
                    map((events) => ({
                      message: '',
                      rows: events.map((event) => ({
                        id: event.id,
                        revision: event.revision,
                        state:
                          event.state === 'media.published'
                            ? 'Média publié'
                            : EXECUTION_STATE_LABELS[event.state],
                        detail: event.detail,
                        createdAt: this.date(event.createdAt),
                      })),
                    })),
                    catchError(() =>
                      of({ message: 'Impossible de charger la chronologie.', rows: [] }),
                    ),
                  ),
                ),
                startWith({ message: 'Chargement de la chronologie…', rows: [] }),
              )
            : of({ message: '', rows: [] }),
        ),
      ),
      artifacts: activeDetails$.pipe(
        switchMap(([id, tab]) =>
          id && tab === 5
            ? timer(0, 5000).pipe(
                exhaustMap(() =>
                  this.tracking.artifacts(id).pipe(
                    map((items) => ({
                      message: '',
                      rows: items.map((item) => ({
                        id: item.sha256,
                        name: item.name,
                        size: formatFileSize(item.bytes),
                      })),
                    })),
                    catchError(() =>
                      of({ message: 'Impossible de charger les fichiers produits.', rows: [] }),
                    ),
                  ),
                ),
              )
            : of({ message: '', rows: [] }),
        ),
      ),
      pipeline: activeDetails$.pipe(
        switchMap(([id, tab]) =>
          id && (tab === 0 || tab === 1 || tab === 3)
            ? timer(0, 3000).pipe(
                exhaustMap(() =>
                  this.tracking.pipeline(id).pipe(
                    map((steps) => ({ message: '', steps })),
                    catchError(() =>
                      of({
                        message: 'Impossible de charger les commandes de cette exécution.',
                        steps: [],
                      }),
                    ),
                  ),
                ),
                startWith({ message: 'Chargement du pipeline…', steps: [] }),
              )
            : of({ message: '', steps: [] }),
        ),
      ),
    };
  }

  private date(value: string): string {
    return new Date(value).toLocaleString('fr-FR');
  }
}
