import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NotificationService } from '../services/notification.service';
import { Subscription } from 'rxjs';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogContent,
  MatDialogRef,
} from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { DialogFrameComponent } from './dialog-frame.component';
import { IconComponent } from './icon.component';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { AiToolsService } from '../services/ai-tools.service';
import { ClipboardService } from '../services/clipboard.service';
import { DeviceAuthPrompt } from '../models/ai-tools';

export interface DeviceAuthDialogData {
  readonly toolId: string;
  readonly toolName: string;
  readonly prompt: DeviceAuthPrompt;
  readonly attemptId?: string | null;
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-device-auth-dialog',
  imports: [
    CommonModule,
    FormsModule,
    MatButtonModule,
    MatDialogActions,
    MatDialogContent,
    DialogFrameComponent,
    IconComponent,
    MatProgressSpinnerModule,
    MatFormFieldModule,
    MatInputModule,
  ],
  template: `
    <app-dialog-frame
      eyebrow="Authentification sécurisée"
      icon="⚿"
      [heading]="'Associer ' + data.toolName"
    >
      <mat-dialog-content class="app-dialog-body device-auth-body">
        @if (data.prompt.userCode) {
          <p class="instruction">
            Ouvrez le lien de connexion dans votre navigateur, puis saisissez le code ci-dessous
            lorsque {{ data.toolName }} le demande.
          </p>
        } @else {
          <p class="instruction">
            Ouvrez le lien de connexion dans votre navigateur et connectez-vous à
            {{ data.toolName }}. Si la page affiche un code, collez-le ci-dessous.
          </p>
        }
        <p class="instruction">
          Une fois associé, le compte est copié chiffré sur tous les nœuds du cluster.
        </p>
        <p class="instruction">La tentative continue sur le nœud si vous fermez cette fenêtre.</p>

        @if (data.prompt.userCode; as userCode) {
          <div class="code-box">
            <span class="code-value">{{ userCode }}</span>
            <button matButton="outlined" type="button" (click)="copyCode()">
              <app-icon [name]="copied() ? 'check' : 'copy'" />
              {{ copied() ? 'Copié !' : 'Copier le code' }}
            </button>
          </div>
        }

        <div class="action-link-box">
          <a
            matButton="filled"
            [href]="data.prompt.verificationUri"
            target="_blank"
            rel="noopener noreferrer"
          >
            <app-icon name="outward" />
            Ouvrir le lien
          </a>
          <button matButton="outlined" type="button" (click)="copyLink()">
            <app-icon [name]="linkCopied() ? 'check' : 'copy'" />
            {{ linkCopied() ? 'Lien copié !' : 'Copier le lien' }}
          </button>
        </div>

        @if (data.prompt.requiresInput && !inputSubmitted()) {
          <mat-form-field appearance="outline">
            <mat-label>Code de retour Claude</mat-label>
            <input
              matInput
              name="authInput"
              autocomplete="one-time-code"
              [(ngModel)]="authInput"
              [ngModelOptions]="{ standalone: true }"
              [disabled]="submittingInput() || expired()"
            />
          </mat-form-field>
          <button
            matButton="tonal"
            type="button"
            [disabled]="!authInput().trim() || submittingInput() || expired()"
            (click)="submitInput()"
          >
            {{ submittingInput() ? 'Transmission…' : 'Transmettre le code à Claude Code' }}
          </button>
        } @else if (inputSubmitted()) {
          <p role="status">Code transmis. Claude Code vérifie la connexion…</p>
        }

        <div class="status-indicator">
          @if (isChecking()) {
            <mat-spinner diameter="20"></mat-spinner>
            <span>Vérification de l'autorisation...</span>
          } @else if (isAuthorized()) {
            <span class="authorized-badge">
              <app-icon name="check" /> Compte associé avec succès !
            </span>
          } @else {
            <span>La connexion est vérifiée automatiquement.</span>
          }
        </div>
        @if (statusMessage()) {
          <p role="status">{{ statusMessage() }}</p>
        }
      </mat-dialog-content>

      <mat-dialog-actions class="app-dialog-footer" align="end">
        <button matButton="text" type="button" (click)="close()">Fermer</button>
      </mat-dialog-actions>
    </app-dialog-frame>
  `,
  styles: [
    `
      .device-auth-dialog {
        min-width: 0;
        width: 100%;
        max-width: 520px;
      }
      .device-auth-body {
        display: flex;
        flex-direction: column;
        gap: 16px;
        padding: 16px 20px;
      }
      .instruction {
        margin: 0;
        color: var(--mat-sys-on-surface-variant);
        font-size: 0.95rem;
        line-height: 1.4;
      }
      .code-box {
        display: flex;
        align-items: center;
        justify-content: space-between;
        padding: 12px 16px;
        background: var(--surface-container-high);
        border-radius: var(--radius-md);
        flex-wrap: wrap;
        gap: var(--space-3);
      }
      .code-value {
        font-family: monospace;
        font-size: 1.4rem;
        font-weight: 700;
        letter-spacing: 2px;
        color: var(--mat-sys-primary);
      }
      .action-link-box {
        display: flex;
        flex-wrap: wrap;
        justify-content: center;
        gap: var(--space-3);
        margin: 8px 0;
      }
      .device-auth-body {
        overflow-x: hidden;
      }
      .status-indicator {
        display: flex;
        align-items: center;
        justify-content: center;
        gap: 8px;
        min-height: 36px;
      }
      .authorized-badge {
        display: flex;
        align-items: center;
        gap: 6px;
        color: var(--success);
        font-weight: 600;
      }
    `,
  ],
})
export class DeviceAuthDialogComponent implements OnInit, OnDestroy {
  readonly data = inject<DeviceAuthDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<DeviceAuthDialogComponent>);
  private readonly aiService = inject(AiToolsService);
  private readonly clipboard = inject(ClipboardService);

  private readonly notifications = inject(NotificationService);
  private polling?: ReturnType<typeof setInterval>;
  private subscription?: Subscription;
  private expiresAt = 0;
  readonly expired = signal(false);
  readonly statusMessage = signal('');
  readonly authInput = signal('');
  readonly submittingInput = signal(false);
  readonly inputSubmitted = signal(false);
  ngOnInit(): void {
    this.expiresAt = Date.now() + this.data.prompt.expiresInSeconds * 1000;
    this.polling = setInterval(
      () => this.checkStatus(),
      Math.max(2, this.data.prompt.intervalSeconds) * 1000,
    );
    this.checkStatus();
  }
  ngOnDestroy(): void {
    clearInterval(this.polling);
    this.subscription?.unsubscribe();
  }
  readonly copied = signal(false);
  readonly isChecking = signal(false);
  readonly isAuthorized = signal(false);

  readonly linkCopied = signal(false);

  async copyLink(): Promise<void> {
    if (!(await this.clipboard.copy(this.data.prompt.verificationUri))) {
      this.notifications.show('Impossible de copier le lien.');
      return;
    }
    this.linkCopied.set(true);
    setTimeout(() => this.linkCopied.set(false), 3000);
  }

  copyCode(): void {
    const userCode = this.data.prompt.userCode;
    if (!userCode) return;
    this.clipboard.copy(userCode);
    this.copied.set(true);
    setTimeout(() => this.copied.set(false), 3000);
  }

  submitInput(): void {
    const value = this.authInput().trim();
    if (!value || this.submittingInput() || this.inputSubmitted()) return;
    this.submittingInput.set(true);
    this.aiService.submitAuthInput(this.data.toolId, value, this.data.attemptId).subscribe({
      next: () => {
        this.authInput.set('');
        this.inputSubmitted.set(true);
        this.submittingInput.set(false);
      },
      error: () => {
        this.submittingInput.set(false);
        this.statusMessage.set('Le code n’a pas pu être transmis. Vérifiez-le puis réessayez.');
      },
    });
  }

  checkStatus(): void {
    if (this.isChecking() || this.isAuthorized() || this.expired()) return;
    if (Date.now() >= this.expiresAt) {
      this.expired.set(true);
      this.statusMessage.set('La connexion a expiré. Fermez puis relancez l’association.');
      clearInterval(this.polling);
      return;
    }
    this.isChecking.set(true);
    this.subscription = this.aiService.getDeviceAuthStatus(this.data.toolId).subscribe({
      next: (status) => {
        this.isChecking.set(false);
        const attemptState = status.attemptState ?? status.state;
        if (status.state === 'authorized' || attemptState === 'authorized') {
          this.isAuthorized.set(true);
          clearInterval(this.polling);
          this.notifications.show('Compte associé.');
          this.dialogRef.close({ success: true });
        } else if (attemptState === 'failed' || attemptState === 'expired') {
          this.expired.set(true);
          this.statusMessage.set(
            status.attemptErrorMessage ||
              status.errorMessage ||
              'La connexion a échoué ou a expiré. Relancez l’association.',
          );
          clearInterval(this.polling);
        }
      },
      error: () => {
        this.isChecking.set(false);
        this.statusMessage.set('Vérification indisponible. Nouvelle tentative automatique.');
      },
    });
  }

  close(): void {
    this.dialogRef.close({ success: this.isAuthorized() });
  }
}
