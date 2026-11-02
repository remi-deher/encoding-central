import { Injectable } from '@angular/core';
import { debounceTime, filter, map, merge, Observable, of, share, timer } from 'rxjs';

export type LiveTopic = 'executions' | 'nodes' | 'libraries' | 'plugins';

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

  /** Émet tout de suite, puis quand un sujet change, puis lentement si le flux ne répond plus. */
  drive(topics: readonly LiveTopic[], quietMs = 30_000): Observable<void> {
    const wanted = new Set(topics);
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
    );
  }
}
