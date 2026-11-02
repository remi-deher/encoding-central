import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { FlowCatalogPageComponent } from './flow-catalog-page.component';

describe('Flow catalog recovery', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      imports: [FlowCatalogPageComponent],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    }).overrideComponent(FlowCatalogPageComponent, { set: { template: '' } });
  });
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    TestBed.resetTestingModule();
    vi.useRealTimers();
  });
  it('resumes loading after an HTTP error', async () => {
    const fixture = TestBed.createComponent(FlowCatalogPageComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    await vi.advanceTimersByTimeAsync(0);
    http
      .expectOne('/api/v1/flows')
      .flush('Unavailable', { status: 503, statusText: 'Unavailable' });
    expect(fixture.componentInstance['catalogState']()).toBe('error');
    await vi.advanceTimersByTimeAsync(5000);
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
