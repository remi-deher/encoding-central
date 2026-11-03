import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { merge, of, Subject } from 'rxjs';
import { LiveUpdates } from '../../shared/services/live-updates';
import { FlowCatalogPageComponent } from './flow-catalog-page.component';

describe('Flow catalog recovery', () => {
  /** Le flux temps réel est simulé : un événement « flows » relance le chargement, sans minuterie. */
  const flowsChanged = new Subject<void>();
  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      imports: [FlowCatalogPageComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: LiveUpdates, useValue: { drive: () => merge(of(undefined), flowsChanged) } },
      ],
    }).overrideComponent(FlowCatalogPageComponent, { set: { template: '' } });
  });
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    TestBed.resetTestingModule();
    vi.useRealTimers();
  });
  it('resumes loading when the live stream reports a flow change after an HTTP error', async () => {
    const fixture = TestBed.createComponent(FlowCatalogPageComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    await vi.advanceTimersByTimeAsync(0);
    http
      .expectOne('/api/v1/flows')
      .flush('Unavailable', { status: 503, statusText: 'Unavailable' });
    expect(fixture.componentInstance['catalogState']()).toBe('error');
    http.expectNone('/api/v1/flows');
    await vi.advanceTimersByTimeAsync(60_000);
    http.expectNone('/api/v1/flows');
    flowsChanged.next();
    await vi.advanceTimersByTimeAsync(0);
    http.expectOne('/api/v1/flows').flush([
      {
        id: 'flow-1',
        name: 'Films',
        mediaType: 'video',
        version: 1,
        nodeCount: 2,
        isDraft: false,
        backupStatus: 'ready',
        updatedAt: '2026-10-19T10:00:00Z',
      },
    ]);
    expect(fixture.componentInstance['catalogState']()).toBe('ready');
    expect(fixture.componentInstance['catalogueItems']()[0]['name']).toBe('Films');
    fixture.destroy();
  });
});
