import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { SystemLogsService } from './system-logs.service';
import { SystemLogEntry, SystemLogOverviewResponse } from '../models/system-logs';

describe('SystemLogsService', () => {
  let service: SystemLogsService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [SystemLogsService, provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(SystemLogsService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('queries logs with filter parameters', () => {
    const mockResponse: SystemLogOverviewResponse = {
      entries: [
        {
          id: 'log-1',
          timestamp: '2026-10-18T20:00:00Z',
          level: 'Warning',
          category: 'Encoding.Core',
          message: 'Test warning',
        },
      ],
      totalBuffered: 1,
    };

    service
      .getLogs({
        minLevel: 'Warning',
        category: 'Encoding.Core',
        search: 'warning',
        limit: 50,
      })
      .subscribe((res) => {
        expect(res.entries.length).toBe(1);
        expect(res.entries[0].message).toBe('Test warning');
        expect(res.totalBuffered).toBe(1);
      });

    const req = httpMock.expectOne((request) => {
      return (
        request.url === '/api/v1/system/logs' &&
        request.params.get('minLevel') === 'Warning' &&
        request.params.get('category') === 'Encoding.Core' &&
        request.params.get('search') === 'warning' &&
        request.params.get('limit') === '50'
      );
    });

    expect(req.request.method).toBe('GET');
    req.flush(mockResponse);
  });

  it('accepts the bare array returned by existing server versions', () => {
    service.getLogs().subscribe((res) => {
      expect(Array.isArray(res.entries)).toBe(true);
      expect(res.entries).toEqual([]);
      expect(res.totalBuffered).toBe(0);
    });
    httpMock.expectOne('/api/v1/system/logs').flush([]);
  });

  it('rejects a malformed overview through the observable error channel', () => {
    let error: unknown;
    service.getLogs().subscribe({ error: (value) => (error = value) });
    httpMock.expectOne('/api/v1/system/logs').flush({ entries: 'invalid' });
    expect(error).toBeInstanceOf(Error);
  });

  it('queries categories and unwraps the list', () => {
    service.getCategories().subscribe((categories) => {
      expect(categories).toEqual(['Encoding.Api', 'Encoding.Core']);
    });

    const req = httpMock.expectOne('/api/v1/system/logs/categories');
    expect(req.request.method).toBe('GET');
    req.flush({ categories: ['Encoding.Api', 'Encoding.Core'] });
  });

  it('reports malformed category responses as errors, never undefined arrays', () => {
    let error: unknown;
    service.getCategories().subscribe({ error: (value) => (error = value) });
    httpMock.expectOne('/api/v1/system/logs/categories').flush({ categories: null });
    expect(error).toBeInstanceOf(Error);
  });

  it('queries categories with nodeId if specified', () => {
    service.getCategories('node-123').subscribe((categories) => {
      expect(categories).toEqual(['WorkerNode']);
    });

    const req = httpMock.expectOne(
      (request) =>
        request.url === '/api/v1/system/logs/categories' &&
        request.params.get('nodeId') === 'node-123',
    );
    expect(req.request.method).toBe('GET');
    req.flush({ categories: ['WorkerNode'] });
  });

  it('creates an EventSource stream for SSE', () => {
    const originalEventSource = (globalThis as unknown as { EventSource?: unknown }).EventSource;

    let createdUrl = '';
    let source: MockEventSource;
    class MockEventSource {
      onmessage: ((event: MessageEvent) => void) | null = null;
      onerror: ((error: unknown) => void) | null = null;
      closed = false;
      listener: ((event: MessageEvent) => void) | null = null;
      addEventListener(_type: string, listener: (event: MessageEvent) => void) {
        this.listener = listener;
      }

      constructor(url: string) {
        createdUrl = url;
        source = this;
      }

      close() {
        this.closed = true;
      }
    }

    (globalThis as unknown as { EventSource: unknown }).EventSource = MockEventSource;

    try {
      const received: SystemLogEntry[] = [];
      const sub = service
        .createLogStream({ minLevel: 'Error', search: 'critical issue' })
        .subscribe((entry) => {
          received.push(entry);
        });

      expect(createdUrl).toContain('/api/v1/system/logs/stream');
      expect(createdUrl).toContain('minLevel=Error');
      expect(createdUrl).toContain('search=critical+issue');

      const entry: SystemLogEntry = {
        id: 'stream-1',
        timestamp: '2026-10-19T00:00:00Z',
        level: 'Error',
        category: 'CLI',
        message: 'Authentication failed',
      };
      source!.listener!(new MessageEvent('log', { data: JSON.stringify(entry) }));
      expect(received).toEqual([entry]);
      sub.unsubscribe();
      expect(source!.closed).toBe(true);
    } finally {
      (globalThis as unknown as { EventSource?: unknown }).EventSource = originalEventSource;
    }
  });
});
