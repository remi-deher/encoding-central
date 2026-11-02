import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Subscription } from 'rxjs';
import { ExecutionOverviewResponse, ExecutionResponse } from '../models/execution';
import { ExecutionTracking, ExecutionTrackingState } from './execution-tracking';

const overview = (id: string, observedAt = '2026-10-23T12:00:00Z'): ExecutionOverviewResponse => ({
  executions: [
    {
      id,
      state: 'succeeded',
      media: 'Film.mkv',
      library: 'Films',
      flow: 'HEVC',
      flowVersion: 1,
      nodeId: null,
      node: null,
      progress: null,
      detail: 'Terminé',
      attempt: 1,
      revision: 1,
      queuedAt: observedAt,
      startedAt: observedAt,
      finishedAt: observedAt,
      updatedAt: observedAt,
    } satisfies ExecutionResponse,
  ],
  queuedCount: 6000,
  activeCount: 1,
  failedCount: 0,
  cancelledCount: 0,
  succeededCount: 80,
  observedAt,
  activity: [],
  totalCount: 80,
});

describe('ExecutionTracking', () => {
  let tracking: ExecutionTracking;
  let http: HttpTestingController;
  let subscription: Subscription;
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-23T12:00:01Z'));
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    tracking = TestBed.inject(ExecutionTracking);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => {
    subscription?.unsubscribe();
    http.verify({ ignoreCancelled: true });
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });
  it('reports malformed responses through the error channel', () => {
    let error: unknown;
    subscription = tracking.overview().subscribe({ error: (value) => (error = value) });
    http.expectOne('/api/v1/executions').flush([]);
    expect(error).toBeInstanceOf(Error);
  });

  it('loads history by state with server pagination and keeps it if another state fails', async () => {
    let latest: ExecutionTrackingState | undefined;
    subscription = tracking
      .observeStates(['succeeded', 'deferred'], 25, 25)
      .subscribe((state) => (latest = state));
    await vi.advanceTimersByTimeAsync(0);
    const succeeded = http.expectOne((request) => request.params.get('state') === 'succeeded');
    expect(succeeded.request.params.get('offset')).toBe('25');
    succeeded.flush(overview('done-25'));
    http
      .expectOne((request) => request.params.get('state') === 'deferred')
      .flush({}, { status: 503, statusText: 'Unavailable' });
    expect(latest?.status).toBe('error');
    expect(latest?.data?.executions[0].id).toBe('done-25');
    expect(latest?.moreStates).toContain('succeeded');
    tracking.refresh();
    http
      .expectOne((request) => request.params.get('state') === 'succeeded')
      .flush({}, { status: 503, statusText: 'Unavailable' });
    http
      .expectOne((request) => request.params.get('state') === 'deferred')
      .flush({ ...overview('deferred'), executions: [], totalCount: 0 });
    expect(latest?.data?.executions[0].id).toBe('done-25');
    tracking.refresh();
    http.expectOne((request) => request.params.get('state') === 'succeeded').flush(overview('new'));
    http
      .expectOne((request) => request.params.get('state') === 'deferred')
      .flush({ ...overview('deferred'), executions: [], totalCount: 0 });
    expect(latest?.status).toBe('ready');
  });

  it('does not replace a fresh stream snapshot with an older HTTP response or an HTTP error', async () => {
    let deliver: ((event: { data: string }) => void) | undefined;
    const closed = vi.fn();
    vi.stubGlobal(
      'EventSource',
      class {
        addEventListener(_name: string, listener: (event: { data: string }) => void) {
          deliver = listener;
        }
        close = closed;
      },
    );
    let latest: ExecutionTrackingState | undefined;
    subscription = tracking.state$.subscribe((state) => (latest = state));
    await vi.advanceTimersByTimeAsync(0);
    deliver!({ data: JSON.stringify(overview('stream', '2026-10-23T12:00:01Z')) });
    http.expectOne('/api/v1/executions').flush(overview('old', '2026-10-23T12:00:00Z'));
    expect(latest?.data?.executions[0].id).toBe('stream');
    await vi.advanceTimersByTimeAsync(16000);
    tracking.refresh();
    deliver!({ data: JSON.stringify(overview('stream-new', '2026-10-23T12:00:17Z')) });
    http.expectOne('/api/v1/executions').flush({}, { status: 503, statusText: 'Unavailable' });
    expect(latest?.status).toBe('ready');
    expect(latest?.data?.executions[0].id).toBe('stream-new');
    subscription.unsubscribe();
    expect(closed).toHaveBeenCalled();
  });
});
