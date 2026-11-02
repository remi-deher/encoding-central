import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of } from 'rxjs';
import { DeviceAuthDialogComponent } from './device-auth-dialog.component';
import { AiToolsService } from '../services/ai-tools.service';
import { ClipboardService } from '../services/clipboard.service';
import { NotificationService } from '../services/notification.service';

describe('DeviceAuthDialogComponent', () => {
  const close = vi.fn();
  const status = vi.fn();
  const cancel = vi.fn();
  const submit = vi.fn();
  beforeEach(() => {
    close.mockReset();
    status.mockReset();
    cancel.mockReset();
    submit.mockReset();
    cancel.mockReturnValue(of({}));
    submit.mockReturnValue(of({ accepted: true }));
  });
  async function setup(state: string, data?: unknown) {
    status.mockReturnValue(of({ state }));
    await TestBed.configureTestingModule({
      imports: [DeviceAuthDialogComponent],
      providers: [
        {
          provide: MAT_DIALOG_DATA,
          useValue: data ?? {
            toolId: 'codex-cli',
            toolName: 'Codex',
            attemptId: 'attempt-123',
            prompt: {
              userCode: 'ABCD-12345',
              verificationUri: 'https://auth.openai.com/codex/device',
              expiresInSeconds: 60,
              intervalSeconds: 5,
            },
          },
        },
        { provide: MatDialogRef, useValue: { close } },
        {
          provide: AiToolsService,
          useValue: { getDeviceAuthStatus: status, cancelAuth: cancel, submitAuthInput: submit },
        },
        { provide: ClipboardService, useValue: { copy: vi.fn() } },
        { provide: NotificationService, useValue: { show: vi.fn() } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(DeviceAuthDialogComponent);
    fixture.detectChanges();
    return fixture;
  }
  it('ne déclare aucun succès et laisse la tentative continuer à la fermeture', async () => {
    const fixture = await setup('pending');
    expect(fixture.componentInstance.isAuthorized()).toBe(false);
    expect(fixture.nativeElement.textContent).not.toContain('Simuler');
    fixture.componentInstance.close();
    expect(close).toHaveBeenCalledWith({ success: false });
    fixture.destroy();
    expect(cancel).not.toHaveBeenCalled();
  });
  it('ne confirme que le statut autorisé renvoyé par le serveur', async () => {
    const fixture = await setup('authorized');
    expect(close).toHaveBeenCalledWith({ success: true });
    fixture.destroy();
    expect(cancel).not.toHaveBeenCalled();
  });
  it('arrête le parcours lorsque le serveur refuse la connexion', async () => {
    const fixture = await setup('failed');
    expect(fixture.componentInstance.expired()).toBe(true);
    expect(close).not.toHaveBeenCalled();
    fixture.destroy();
  });

  it('affiche le lien Claude et transmet le code demandé à la CLI', async () => {
    const fixture = await setup('pending', {
      toolId: 'claude-cli',
      toolName: 'Claude Code',
      attemptId: 'attempt-claude',
      prompt: {
        userCode: null,
        verificationUri: 'https://claude.com/oauth/authorize?flow=test',
        expiresInSeconds: 900,
        intervalSeconds: 5,
        requiresInput: true,
      },
    });
    expect(fixture.nativeElement.querySelector('a')?.getAttribute('href')).toBe(
      'https://claude.com/oauth/authorize?flow=test',
    );
    fixture.componentInstance.authInput.set('return-code');
    fixture.componentInstance.submitInput();
    expect(submit).toHaveBeenCalledWith('claude-cli', 'return-code', 'attempt-claude');
    expect(fixture.componentInstance.inputSubmitted()).toBe(true);
    fixture.destroy();
  });
});
