import { Injectable } from '@angular/core';
import { debounceTime, EMPTY, filter, map, merge, Observable, of, share, timer } from 'rxjs';

export type LiveTopic =
  'executions' | 'execution-logs' | 'workers' | 'libraries' | 'plugins' | 'flows' | 'settings';

/**
 * Une connexion pour toute l'interface. Le serveur nomme le sujet modifié.
 * La page recharge ce sujet, et un passage lent ne reprend que si le flux se tait.
 */
@Injectable({ providedIn: 'root' })
export class LiveUpdates {
  private lastTraffic = 0;
  private readonly topics$ = new Observable<readonly LiveTopic[]>((subscriber) => {
    if (typeof EventSource === 'undefined') {
      subscriber.complete();
      return undefined;
    }
    const source = new EventSource('/api/v1/live', { withCredentials: true });
    const mark = () => {
      this.lastTraffic = Date.now();
    };
    source.addEventListener('ready', mark);
    source.addEventListener('ping', mark);
    source.addEventListener('change', (event: MessageEvent<string>) => {
      mark();
      try {
        const body = JSON.parse(event.data) as { topics?: readonly LiveTopic[] };
        if (Array.isArray(body.topics) && body.topics.length > 0) subscriber.next(body.topics);
      } catch {
        // Un événement illisible ne déclenche pas de rechargement.
      }
    });
    return () => source.close();
  }).pipe(share());

  /**
   * Émet tout de suite, puis quand un sujet change, puis lentement si le flux ne répond plus.
   * `refreshMs` ajoute un rafraîchissement lent permanent, pour un état que nul appel ne modifie
   * (vivacité d'un worker : battements de cœur et expirations se passent côté serveur).
   */
  drive(
    topics: readonly LiveTopic[],
    options: { readonly quietMs?: number; readonly refreshMs?: number } = {},
  ): Observable<void> {
    const wanted = new Set(topics);
    const quietMs = options.quietMs ?? 30_000;
    return merge(
      of(undefined),
      this.topics$.pipe(
        filter((changed) => changed.some((topic) => wanted.has(topic))),
        debounceTime(300),
        map(() => undefined),
      ),
      timer(quietMs, quietMs).pipe(
        filter(() => Date.now() - this.lastTraffic > quietMs),
        map(() => undefined),
      ),
      options.refreshMs
        ? timer(options.refreshMs, options.refreshMs).pipe(map(() => undefined))
        : EMPTY,
    );
  }
}
