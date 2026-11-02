import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { PlexSetupAssistantComponent } from './plex-setup-assistant.component';
import { PlexLibraries } from './plex-setup.api';

const LIBRARIES: PlexLibraries = {
  sections: [
    { key: '3', title: 'Films', kind: 'movie', locations: ['/media/FILMS', '/media2/FILMS'] },
    { key: '2', title: 'Séries TV', kind: 'show', locations: ['/media/SERIES'] },
  ],
  libraries: [
    {
      id: 'films-data',
      name: 'Films — NAS DATA',
      localPaths: ['/media/FILMS'],
      currentRemoteRoot: null,
      suggestion: {
        libraryId: 'films-data',
        remoteRoot: '/media/FILMS',
        remoteKey: '3',
        remoteTitle: 'Films',
        reason: 'Même chemin sur le nœud et dans Plex',
      },
    },
    {
      id: 'series-data',
      name: 'Séries — NAS DATA',
      localPaths: ['/media/SERIES'],
      currentRemoteRoot: '/media/SERIES',
      suggestion: null,
    },
    { id: 'autre', name: 'Autre', localPaths: [], currentRemoteRoot: null, suggestion: null },
  ],
};

describe('Plex setup assistant', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }),
  );
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  async function withLibraries() {
    const fixture = TestBed.createComponent(PlexSetupAssistantComponent);
    fixture.detectChanges();
    fixture.nativeElement.querySelector('[data-testid="plex-load-libraries"]').click();
    const http = TestBed.inject(HttpTestingController);
    const request = http.expectOne('/api/v1/plugins/plex/setup/libraries');
    expect(request.request.method).toBe('POST');
    request.flush(LIBRARIES);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, http };
  }

  it('prefills saved mappings and suggestions, and explains each one', async () => {
    const { fixture } = await withLibraries();
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Films — NAS DATA');
    expect(text).toContain('Proposée : Même chemin sur le nœud et dans Plex');
    expect(text).toContain('Enregistrée');
    expect(text).toContain('aucun accès direct déclaré');
  });

  it('saves only chosen mappings and reports the saved configuration', async () => {
    const { fixture, http } = await withLibraries();
    const saved: unknown[] = [];
    fixture.componentInstance.configurationSaved.subscribe((value) => saved.push(value));
    fixture.nativeElement.querySelector('[data-testid="plex-save-mappings"]').click();
    const request = http.expectOne('/api/v1/plugins/plex/setup/mappings');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({
      mappings: [
        { libraryId: 'films-data', remoteRoot: '/media/FILMS' },
        { libraryId: 'series-data', remoteRoot: '/media/SERIES' },
      ],
    });
    request.flush({ pluginId: 'plex', revision: 4, values: {}, configuredSecrets: ['apiKey'] });
    await fixture.whenStable();
    http.expectOne('/api/v1/plugins/plex/setup/libraries').flush(LIBRARIES);
    await fixture.whenStable();
    expect(saved.length).toBe(1);
  });

  it('blocks saving when one Plex folder is chosen twice', async () => {
    const { fixture } = await withLibraries();
    (fixture.componentInstance as unknown as { choose(id: string, root: string): void }).choose(
      'autre',
      '/media/FILMS',
    );
    fixture.detectChanges();
    const button = fixture.nativeElement.querySelector('[data-testid="plex-save-mappings"]');
    expect(button.disabled).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('Un même dossier Plex est choisi');
  });

  it('shows the server error message without inventing a state', async () => {
    const fixture = TestBed.createComponent(PlexSetupAssistantComponent);
    fixture.detectChanges();
    fixture.nativeElement.querySelector('[data-testid="plex-load-libraries"]').click();
    TestBed.inject(HttpTestingController)
      .expectOne('/api/v1/plugins/plex/setup/libraries')
      .flush(
        { title: 'Plex a refusé l’accès : jeton invalide ou révoqué.' },
        { status: 502, statusText: 'Bad Gateway' },
      );
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('jeton invalide ou révoqué');
  });
});
