import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { SystemTabComponent } from './system-tab.component';

describe('SystemTabComponent', () => {
  async function setup() {
    await TestBed.configureTestingModule({
      imports: [SystemTabComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(SystemTabComponent);
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    http
      .expectOne('/api/v1/cluster')
      .flush({ id: 'abc-123', name: 'Maison', standalone: true, hasClusterToken: false });
    http.expectOne('/health/details').flush({
      status: 'degraded',
      totalDurationMs: 40,
      checks: [
        { name: 'database', status: 'healthy', durationMs: 4 },
        { name: 'cluster', status: 'degraded', durationMs: 2000, description: 'Pair muet' },
      ],
    });
    await new Promise((resolve) => setTimeout(resolve));
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('lists the services needing attention first, with their reason', async () => {
    const element = await setup();
    const names = [...element.querySelectorAll('.service strong')].map((n) => n.textContent);
    expect(names).toEqual(['Maillage du cluster', 'Base de données']);
    expect(element.querySelector('.service.attention')?.textContent).toContain('Pair muet');
    expect(element.querySelector('.banner')?.textContent).toContain('1 service demande');
  });

  it('shows the cluster identity as rows', async () => {
    const element = await setup();
    expect(element.textContent).toContain('Maison');
    expect(element.textContent).toContain('abc-123');
  });
});
