import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router } from '@angular/router';
import { signal } from '@angular/core';
import { WorkerSetupPageComponent } from './worker-setup-page.component';
import { AuthService } from '../../core/auth/auth.service';
import { LocalWorkerController } from '../../shared/services/local-worker-controller';

describe('WorkerSetupPageComponent', () => {
  let login: ReturnType<typeof vi.fn>;
  let refresh: ReturnType<typeof vi.fn>;
  let verifyAddress: ReturnType<typeof vi.fn>;
  let enroll: ReturnType<typeof vi.fn>;
  let ensureLoaded: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    login = vi.fn().mockResolvedValue(undefined);
    refresh = vi.fn();
    verifyAddress = vi.fn().mockResolvedValue(undefined);
    enroll = vi.fn().mockResolvedValue(undefined);
    ensureLoaded = vi.fn().mockResolvedValue({ setupRequired: false, user: null });

    TestBed.configureTestingModule({
      imports: [WorkerSetupPageComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: AuthService,
          useValue: { login, ensureLoaded },
        },
        { provide: Router, useValue: { navigate: vi.fn().mockResolvedValue(true) } },
      ],
    });
    TestBed.overrideComponent(WorkerSetupPageComponent, {
      set: {
        providers: [
          {
            provide: LocalWorkerController,
            useValue: {
              error: signal(''),
              targetName: signal('Central de test'),
              pending: signal(false),
              status: signal(null),
              busy: signal(false),
              loading: signal(false),
              agentUnavailable: signal(false),
              canJoin: signal(true),
              refresh,
              verifyAddress,
              target: signal({ id: 'c1', name: 'Central de test' }),
              enroll,
            },
          },
        ],
      },
    });
  });

  it('starts on a welcome screen that is not counted as a step and can be reached again', () => {
    const fixture = TestBed.createComponent(WorkerSetupPageComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.textContent).toContain('Bienvenue');
    expect(el.textContent).toContain('Commencer');
    expect(el.textContent).toContain('Configuration détectée');
    expect(el.textContent).not.toContain('Étape');

    fixture.componentInstance['start']();
    fixture.detectChanges();
    expect(el.textContent).toContain('Étape 1 sur 3');
    expect(el.textContent).not.toContain('Configuration détectée');

    fixture.componentInstance['back']();
    fixture.detectChanges();
    expect(el.textContent).toContain('Bienvenue');
  });

  it('authenticates with the central before enrolling', async () => {
    const fixture = TestBed.createComponent(WorkerSetupPageComponent);
    const component = fixture.componentInstance;
    component['step'].set(1);
    component['username'] = 'admin';
    component['password'] = 'password';

    await component['login']();

    expect(login).toHaveBeenCalledWith('admin', 'password');
    expect(refresh).toHaveBeenCalled();
    expect(component['password']).toBe('');
    expect(component['step']()).toBe(2);
  });

  it('enrolls with the administrator session on the selected central', async () => {
    const fixture = TestBed.createComponent(WorkerSetupPageComponent);
    const component = fixture.componentInstance;
    component['centralAddress'] = 'https://central.example.test/';

    await component['attach']();

    expect(verifyAddress).toHaveBeenCalledWith({
      address: 'https://central.example.test/',
    });
    expect(enroll).toHaveBeenCalledTimes(1);
  });

  it('lists the centrals detected by the worker and registers the chosen one', async () => {
    const fixture = TestBed.createComponent(WorkerSetupPageComponent);
    const component = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/v1/worker/hardware').flush({ inventory: null, tools: [] });
    http
      .expectOne('/api/v1/worker/centrals')
      .flush([
        { id: 'c1', name: 'Mon central', apiBaseUrl: 'http://server:8080/', discoveredAt: '', source: 'network' },
      ]);
    await fixture.whenStable();
    expect(component['detected']().map((central) => central.name)).toEqual(['Mon central']);

    const choosing = component['chooseDetected'](component['detected']()[0]);
    const post = http.expectOne('/api/v1/worker/centrals');
    expect(post.request.method).toBe('POST');
    expect(post.request.body).toEqual({ apiBaseUrl: 'http://server:8080/' });
    post.flush({});
    await choosing;

    expect(component['step']()).toBe(1);
  });

  it('keeps the manual address available when no central is detected', async () => {
    const fixture = TestBed.createComponent(WorkerSetupPageComponent);
    const component = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/v1/worker/hardware').flush({ inventory: null, tools: [] });
    http.expectOne('/api/v1/worker/centrals').flush('', { status: 401, statusText: 'Unauthorized' });
    await fixture.whenStable();

    expect(component['detected']()).toEqual([]);
    expect(component['discovering']()).toBe(false);
  });

  it('goes straight to the enrollment step when an administrator session is already open', async () => {
    ensureLoaded.mockResolvedValue({
      setupRequired: false,
      user: { id: 'u1', username: 'admin', role: 'admin' },
    });
    const fixture = TestBed.createComponent(WorkerSetupPageComponent);
    const component = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    component['centralAddress'] = 'http://server:8080/';

    const connecting = component['connect']();
    http.expectOne((request) => request.method === 'POST').flush({});
    await connecting;

    expect(component['step']()).toBe(2);
    expect(component['signedInAs']()).toBe('admin');
    expect(refresh).toHaveBeenCalled();
    component['back']();
    expect(component['step']()).toBe(0);
  });

  it('asks for an administrator when the open session is not one', async () => {
    ensureLoaded.mockResolvedValue({
      setupRequired: false,
      user: { id: 'u2', username: 'lecteur', role: 'viewer' },
    });
    const fixture = TestBed.createComponent(WorkerSetupPageComponent);
    const component = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    component['centralAddress'] = 'http://server:8080/';

    const connecting = component['connect']();
    http.expectOne((request) => request.method === 'POST').flush({});
    await connecting;

    expect(component['step']()).toBe(1);
    expect(component['signedInAs']()).toBeNull();
    expect(component['errorMessage']()).toContain('lecteur');
  });

  it('shows login failures without advancing', async () => {
    login.mockRejectedValueOnce(new Error('invalid credentials'));
    const fixture = TestBed.createComponent(WorkerSetupPageComponent);
    const component = fixture.componentInstance;
    component['step'].set(1);

    await component['login']();

    expect(component['step']()).toBe(1);
    expect(component['errorMessage']()).toBe('Les identifiants sont invalides.');
  });
});
