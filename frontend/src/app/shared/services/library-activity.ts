import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { LibraryActivityResponse, LibraryActivityStatus } from '../models/library-activity';

@Injectable({ providedIn: 'root' })
export class LibraryActivity {
  private readonly http = inject(HttpClient);

  list(options: {
    readonly status: LibraryActivityStatus;
    readonly libraryId?: string;
    readonly flow?: string;
    readonly nodeId?: string;
    readonly search?: string;
    readonly limit: number;
    readonly offset: number;
  }) {
    let params = new HttpParams()
      .set('status', options.status)
      .set('limit', String(options.limit))
      .set('offset', String(options.offset));
    if (options.libraryId) params = params.set('libraryId', options.libraryId);
    if (options.flow) params = params.set('flow', options.flow);
    if (options.nodeId) params = params.set('nodeId', options.nodeId);
    if (options.search?.trim()) params = params.set('search', options.search.trim());
    return this.http.get<LibraryActivityResponse>('/api/v1/activity/files', { params });
  }

  stream(options: {
    readonly status: LibraryActivityStatus;
    readonly libraryId?: string;
    readonly flow?: string;
    readonly nodeId?: string;
    readonly search?: string;
    readonly limit: number;
    readonly offset: number;
  }): Observable<LibraryActivityResponse> {
    return new Observable<LibraryActivityResponse>((subscriber) => {
      if (typeof EventSource === 'undefined') {
        subscriber.complete();
        return undefined;
      }
      const params = new URLSearchParams();
      params.set('status', options.status);
      params.set('limit', String(options.limit));
      params.set('offset', String(options.offset));
      if (options.libraryId) params.set('libraryId', options.libraryId);
      if (options.flow) params.set('flow', options.flow);
      if (options.nodeId) params.set('nodeId', options.nodeId);
      if (options.search?.trim()) params.set('search', options.search.trim());

      const url = `/api/v1/activity/stream?${params.toString()}`;
      let source: EventSource | null = null;
      try {
        source = new EventSource(url, { withCredentials: true });
        source.addEventListener('activity', (event: MessageEvent<string>) => {
          try {
            const data = JSON.parse(event.data) as LibraryActivityResponse;
            if (data && Array.isArray(data.files) && data.counts) {
              subscriber.next(data);
            }
          } catch {
            // Ignore parse errors, let next event or fallback handle it
          }
        });
        source.onerror = () => {
          // EventSource will automatically retry connecting.
        };
      } catch (err) {
        source?.close();
        subscriber.error(err);
      }
      return () => {
        source?.close();
      };
    });
  }
}
