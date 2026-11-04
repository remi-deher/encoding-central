import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { catchError, exhaustMap, of, switchMap } from 'rxjs';
import { LocalWorkerStoreState } from '../models/local-worker';
import { LiveUpdates } from './live-updates';

/**
 * État de synchronisation Store de CE worker, en lecture seule
 * (`GET /api/v1/workers/{id}/store-state`). Fourni par la page qui l'utilise.
 */
@Injectable()
export class LocalWorkerStoreStateService {
  private readonly http = inject(HttpClient);
  private readonly live = inject(LiveUpdates);
  private readonly workerId = signal<string | null>(null);

  readonly state = signal<LocalWorkerStoreState | null>(null);
  readonly loaded = signal(false);

  constructor() {
    toObservable(this.workerId)
      .pipe(
        switchMap((id) =>
          id
            ? this.live
                .drive(['workers', 'plugins'], { refreshMs: 15_000 })
                .pipe(
                  exhaustMap(() =>
                    this.http
                      .get<LocalWorkerStoreState>(`/api/v1/workers/${id}/store-state`)
                      .pipe(catchError(() => of(null))),
                  ),
                )
            : of(undefined),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((state) => {
        if (state === undefined) return;
        this.loaded.set(true);
        this.state.set(state);
      });
  }

  /** Identifiant du worker local, connu de l'agent ; vide tant que le worker n'est pas rattaché. */
  setWorkerId(id: string | null | undefined): void {
    this.workerId.set(id || null);
  }
}
