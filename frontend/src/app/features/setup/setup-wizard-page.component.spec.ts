import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { signal } from '@angular/core';
import { SetupWizardPageComponent } from './setup-wizard-page.component';
import { AuthService } from '../../core/auth/auth.service';
import { LocalNodeController } from '../../shared/services/local-node-controller';
import { NotificationService } from '../../shared/services/notification.service';
import { LocalNodeStatus } from '../../shared/models/local-node';

const VALID_PASSWORD = 'un-mot-de-passe-long';

function fillAdmin(component: SetupWizardPageComponent): void {
  component['adminPassword'].set(VALID_PASSWORD);
  component['adminConfirm'].set(VALID_PASSWORD);
}

describe('SetupWizardPageComponent', () => {
  let setupFn: ReturnType<typeof vi.fn>;
  let navigateFn: ReturnType<typeof vi.fn>;
  let statusSignal: ReturnType<typeof signal<LocalNodeStatus | null>>;
  let pendingSignal: ReturnType<typeof signal<boolean>>;

  beforeEach(() => {
    setupFn = vi.fn().mockResolvedValue(undefined);
    navigateFn = vi.fn().mockResolvedValue(true);
    pendingSignal = signal(false);
    statusSignal = signal<LocalNodeStatus | null>({
      workerId: 'node-1',
      name: 'srv-transcode',
      displayName: 'Serveur Salon',
      roles: ['server', 'worker'],
      maxConcurrentTasks: 2,
      automaticEnrollmentEnabled: true,
      capabilities: [
        {
          toolId: 'ffmpeg',
          version: '7.1',
          accelerationTypes: ['nvenc'],
          mediaTypes: ['video', 'audio'],
        },
      ],
      inventory: {
        processorCount: 16,
        memoryMb: 32768,
        operatingSystem: 'Linux',
        architecture: 'x64',
        processorModel: 'AMD Ryzen 7 5800X',
        gpus: [{ name: 'NVIDIA GeForce RTX 3080', device: 'gpu0' }],
      },
      isEnrolled: true,
      pairingKey: null,
      pairingExpiresAt: null,
      centralId: null,
      centralName: null,
      verificationCode: null,
      enrollmentFailure: null,
    });

    TestBed.configureTestingModule({
      imports: [SetupWizardPageComponent],
      providers: [
        {
          provide: AuthService,
          useValue: { setup: setupFn },
        },
        {
          provide: Router,
          useValue: { navigate: navigateFn },
        },
        {
          provide: NotificationService,
          useValue: { show: vi.fn() },
        },
      ],
    });

    TestBed.overrideComponent(SetupWizardPageComponent, {
      set: {
        providers: [
          {
            provide: LocalNodeController,
            useValue: {
              status: statusSignal,
              clusters: signal([]),
              joinableClusters: signal([]),
              target: signal(null),
              pairing: signal(null),
              busy: signal(false),
              canJoin: signal(true),
              pending: pendingSignal,
              joinIndex: signal(0),
              error: signal(''),
              refresh: vi.fn(),
              verifyAddress: vi.fn(),
              sendRequest: vi.fn(),
            },
          },
        ],
      },
    });
  });

  it('shows the detected node info and hardware on the welcome step', () => {
    const fixture = TestBed.createComponent(SetupWizardPageComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.textContent).toContain('Configurer le central');
    expect(el.textContent).toContain('AMD Ryzen 7 5800X');
    expect(el.textContent).toContain('32 Go');
    expect(el.textContent).toContain('NVIDIA GeForce RTX 3080');
    expect(el.textContent).toContain('Accélération GPU');
    expect(el.textContent).toContain('NVENC');
  });

  it('displays anonymous setup node sheet with IP address, announced URL and hardware when session is not yet created', () => {
    statusSignal.set(null);
    const fixture = TestBed.createComponent(SetupWizardPageComponent);
    fixture.componentInstance['setupSheet'].set({
      name: 'encoding-host',
      displayName: 'Worker Salon',
      agentAvailable: true,
      inventory: {
        operatingSystem: 'Ubuntu 24.04 LTS',
        architecture: 'X64',
        processorCount: 8,
        memoryMb: 16384,
        processorModel: 'Intel Core i5-12400',
        gpus: [
          { name: 'Intel UHD Graphics 730', vendor: 'Intel', driver: 'i915', device: 'card0' },
        ],
      },
      tools: [
        {
          toolId: 'ffmpeg',
          accelerationTypes: ['qsv'],
          encoderChecks: [{ encoderId: 'hevc_qsv', status: 'usable' }],
        },
      ],
      addresses: [
        { interface: 'eth0', address: '172.16.2.2' },
        { interface: 'eth0', address: 'fd00::42' },
        { interface: 'lo', address: '127.0.0.1' },
        { interface: 'docker0', address: '172.17.0.1' },
      ],
      urls: ['https://transcodage.deher.net'],
    });
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.textContent).toContain('encoding-host · Ubuntu 24.04 LTS (X64)');
    const addresses = Array.from(el.querySelectorAll('.network-addresses li')).map((item) =>
      item.textContent?.trim(),
    );
    expect(addresses).toEqual([
      '172.16.2.2 (eth0)',
      'fd00::42 (eth0)',
      '127.0.0.1 (lo)',
      '172.17.0.1 (docker0)',
    ]);
    expect(fixture.componentInstance['networkAccessUrls']()).toEqual([
      'https://transcodage.deher.net',
    ]);
    expect(el.textContent).toContain('Adresse annoncée');
    expect(el.textContent).toContain('Intel Core i5-12400 (8 cœurs)');
    expect(el.textContent).toContain('16 Go');
    expect(el.textContent).toContain('Intel UHD Graphics 730');
    expect(el.textContent).toContain('QSV');
  });

  it('keeps the browser host and published port as access fallback in bridge mode', () => {
    const fixture = TestBed.createComponent(SetupWizardPageComponent);
    fixture.componentInstance['setupSheet'].set({
      name: 'container',
      displayName: null,
      agentAvailable: false,
      inventory: null,
      tools: null,
      addresses: [{ interface: 'eth0', address: '172.16.2.2' }],
      urls: [],
    });
    fixture.detectChanges();
    expect(fixture.componentInstance['networkAccessUrls']()).toEqual([window.location.origin]);
    expect(fixture.nativeElement.textContent).toContain('172.16.2.2 (eth0)');
    expect(fixture.nativeElement.textContent).toContain('Adresse utilisée par ce navigateur');
  });

  it('shows browser access even when no interface address can be detected', () => {
    statusSignal.set(null);
    const fixture = TestBed.createComponent(SetupWizardPageComponent);
    fixture.componentInstance['setupSheet'].set({
      name: null,
      displayName: null,
      agentAvailable: false,
      inventory: null,
      tools: null,
      addresses: [],
      urls: [],
    });
    fixture.detectChanges();
    expect(fixture.componentInstance['networkAccessUrls']()).toEqual([window.location.origin]);
    expect(fixture.nativeElement.textContent).toContain('Aucune adresse détectée');
    expect(fixture.nativeElement.textContent).not.toContain('Locale uniquement');
  });

  it('renders the central review step', () => {
    const fixture = TestBed.createComponent(SetupWizardPageComponent);
    fixture.componentInstance['welcome'].set(false);
    fixture.componentInstance['currentStep'].set(2);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.textContent).toContain('Vérifier le central');
    expect(el.textContent).toContain('Initialiser le central');
  });

  it('starts on a welcome screen that is not counted as a step', () => {
    const fixture = TestBed.createComponent(SetupWizardPageComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.textContent).toContain('Bienvenue');
    expect(el.textContent).toContain('Commencer');
    expect(el.textContent).not.toContain('Étape');
    expect(el.querySelector('#admin-username-input')).toBeNull();

    fixture.componentInstance['start']();
    fixture.detectChanges();
    expect(el.textContent).toContain('Étape 1 sur 3');
    expect(el.textContent).not.toContain('Configuration détectée');

    fixture.componentInstance['previousStep']();
    fixture.detectChanges();
    expect(el.textContent).toContain('Bienvenue');
    expect(el.textContent).toContain('Configuration détectée');
  });

  it('only advances after the administrator form is valid', () => {
    const fixture = TestBed.createComponent(SetupWizardPageComponent);
    const component = fixture.componentInstance;

    component['nextFromAccount']();
    expect(component['currentStep']()).toBe(0);

    fillAdmin(component);
    component['nextFromAccount']();
    expect(component['currentStep']()).toBe(1);

    component['advanceToReview']();
    expect(component['currentStep']()).toBe(2);
    component['previousStep']();
    expect(component['currentStep']()).toBe(1);
  });

  it('submits the central initialisation with the cluster name', async () => {
    const fixture = TestBed.createComponent(SetupWizardPageComponent);
    fixture.detectChanges();
    const component = fixture.componentInstance;

    fillAdmin(component);
    component['clusterName'].set('Mon Central Maison');
    await component['submitCreateCluster']();

    expect(setupFn).toHaveBeenCalledWith({
      nodeDisplayName: 'Serveur Salon',
      clusterName: 'Mon Central Maison',
      username: 'admin',
      password: VALID_PASSWORD,
    });
    expect(navigateFn).toHaveBeenCalledWith(['/']);
  });

  it('refuses to finish without a valid administrator account', async () => {
    const fixture = TestBed.createComponent(SetupWizardPageComponent);
    fixture.componentInstance['welcome'].set(false);
    fixture.detectChanges();
    const component = fixture.componentInstance;
    const el = fixture.nativeElement as HTMLElement;

    await component['submitCreateCluster']();
    fixture.detectChanges();
    expect(setupFn).not.toHaveBeenCalled();
    expect(el.textContent).toContain('au moins 12 caractères');

    component['adminPassword'].set('court');
    component['adminConfirm'].set('court');
    await component['submitCreateCluster']();
    expect(setupFn).not.toHaveBeenCalled();

    component['adminPassword'].set(VALID_PASSWORD);
    component['adminConfirm'].set('autre-mot-de-passe');
    await component['submitCreateCluster']();
    fixture.detectChanges();
    expect(setupFn).not.toHaveBeenCalled();
    expect(el.textContent).toContain('Les deux mots de passe sont différents.');

    component['adminConfirm'].set(VALID_PASSWORD);
    component['adminUsername'].set('nom invalide!');
    await component['submitCreateCluster']();
    expect(setupFn).not.toHaveBeenCalled();
  });
});
