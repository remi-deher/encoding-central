import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PublicUrlService } from './public-url.service';

describe('PublicUrlService', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    return {
      service: TestBed.inject(PublicUrlService),
      http: TestBed.inject(HttpTestingController),
    };
  }

  it('falls back to the browser address until a public URL is declared', () => {
    const { service, http } = setup();
    http.expectOne('/api/v1/network/settings').flush({ trustProxy: false, proxies: [] });
    expect(service.origin()).toBe(window.location.origin);
    expect(service.isDeclared()).toBe(false);
  });

  it('uses the declared public URL without its trailing slash', () => {
    const { service, http } = setup();
    http
      .expectOne('/api/v1/network/settings')
      .flush({ trustProxy: false, proxies: [], publicUrl: 'https://encoding.example.com/' });
    expect(service.origin()).toBe('https://encoding.example.com');
    service.set('');
    expect(service.origin()).toBe(window.location.origin);
  });

  it('keeps the browser address when the setting cannot be read', () => {
    const { service, http } = setup();
    http.expectOne('/api/v1/network/settings').flush('', { status: 403, statusText: 'Forbidden' });
    expect(service.origin()).toBe(window.location.origin);
  });
});
