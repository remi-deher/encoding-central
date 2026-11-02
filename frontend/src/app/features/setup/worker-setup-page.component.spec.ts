import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { Router } from '@angular/router';
import { signal } from '@angular/core';
import { WorkerSetupPageComponent } from './worker-setup-page.component';
import { AuthService } from '../../core/auth/auth.service';
import { LocalNodeController } from '../../shared/services/local-node-controller';

describe('WorkerSetupPageComponent', () => {
  let login: ReturnType<typeof vi.fn>;
  let refresh: ReturnType<typeof vi.fn>;
  let verifyAddress: ReturnType<typeof vi.fn>;
  let sendRequest: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    login = vi.fn().mockResolvedValue(undefined);
    refresh = vi.fn();
    verifyAddress = vi.fn().mockResolvedValue(undefined);
    sendRequest = vi.fn().mockResolvedValue(undefined);

    TestBed.configureTestingModule({
      imports: [WorkerSetupPageComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: { login } },
        { provide: Router, useValue: { navigate: vi.fn().mockResolvedValue(true) } },
      ],
    });
    TestBed.overrideComponent(WorkerSetupPageComponent, {
      set: {
        providers: [
          {
            provide: LocalNodeController,
            useValue: {
              error: signal(''),
              target: signal(null),
              targetName: signal('Central de test'),
              pending: signal(false),
              status: signal(null),
              busy: signal(false),
              loading: signal(false),
              agentUnavailable: signal(false),
              canJoin: signal(true),
              refresh,
              verifyAddress,
              sendRequest,
            },
          },
        ],
      },
    });
  });

  it('authenticates with the central before preparing enrollment', async () => {
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

  it('uses the selected central address to prepare enrollment', async () => {
    const fixture = TestBed.createComponent(WorkerSetupPageComponent);
    const component = fixture.componentInstance;
    component['centralAddress'] = 'https://central.example.test/';

    await component['verifyEnrollment']();

    expect(verifyAddress).toHaveBeenCalledWith({
      address: 'https://central.example.test/',
    });
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
