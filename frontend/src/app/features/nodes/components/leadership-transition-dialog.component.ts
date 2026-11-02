import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  OnDestroy,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogContent,
  MatDialogRef,
} from '@angular/material/dialog';
import { DialogFrameComponent, DialogVariant } from '../../../shared/ui/dialog-frame.component';
import { ClusterApi } from '../../../shared/services/cluster-api';

export interface LeadershipTransitionDialogData {
  readonly targetNodeId: string;
  readonly targetNodeName: string;
  readonly currentNodeName?: string;
  readonly targetNodeHost?: string | null;
  readonly defaultTargetUrl?: string | null;
  readonly animationDelayMs?: number;
}

export type StepStatus = 'pending' | 'active' | 'completed' | 'failed';

export interface TransitionStep {
  readonly id: string;
  readonly label: string;
  readonly description: string;
  status: StepStatus;
  detail?: string;
}

const INITIAL_STEPS: readonly Omit<TransitionStep, 'status'>[] = [
  {
    id: 'drain',
    label: 'Préparation & Drainage',
    description: 'Suspension des nouvelles attributions sur le maître actuel',
  },
  {
    id: 'snapshot',
    label: 'Instantané SQLite',
    description: 'Création du snapshot VACUUM INTO et calcul de l’empreinte SHA-256',
  },
  {
    id: 'receive',
    label: 'Réception & Validation',
    description: 'Téléchargement par le worker et vérification de l’intégrité',
  },
  {
    id: 'switchover',
    label: 'Bascule des rôles',
    description: 'Remplacement de cluster.db et démarrage du serveur sur la cible',
  },
  {
    id: 'health',
    label: 'Vérification en direct',
    description: 'Sondage de /health/ready sur le nouveau Maître',
  },
];

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-leadership-transition-dialog',
  imports: [MatButtonModule, MatDialogActions, MatDialogContent, DialogFrameComponent],
  template: `
    <app-dialog-frame
      eyebrow="Gouvernance du cluster"
      [heading]="frameHeading()"
      [variant]="frameVariant()"
      [icon]="frameIcon()"
    >
      <mat-dialog-content class="app-dialog-body transition-dialog-body">
        @if (mode() === 'confirm') {
          <p class="transition-lead">
            Vous allez transférer la gouvernance active du cluster vers le nœud
            <strong>{{ data.targetNodeName }}</strong
            >.
          </p>

          <div class="transition-nodes-card">
            <div class="node-chip">
              <span class="node-chip-label">Maître actuel</span>
              <span class="node-chip-name">{{ data.currentNodeName || 'Ce nœud' }}</span>
            </div>
            <span class="transition-arrow" aria-hidden="true">→</span>
            <div class="node-chip">
              <span class="node-chip-label">Nouveau Maître</span>
              <span class="node-chip-name">{{ data.targetNodeName }}</span>
            </div>
          </div>

          <div class="transition-url-row">
            <label for="target-url-input">URL d'accès au nouveau Maître :</label>
            <input
              id="target-url-input"
              type="text"
              class="url-input"
              [value]="targetUrl()"
              (input)="onUrlInput($event)"
              placeholder="http://192.168.1.51:8090/"
            />
            <p class="url-hint">
              L'URL sur laquelle le navigateur sera redirigé après la passation.
            </p>
          </div>

          <p class="transition-notice transition-notice_warning" role="note">
            Le maître actuel drainera temporairement ses tâches en cours, générera un instantané
            cohérent de la base SQLite et basculera en simple Worker.
          </p>
        } @else {
          <div class="transition-timeline">
            @for (step of steps(); track step.id; let idx = $index) {
              <div
                class="step-row"
                [class.step-row_pending]="step.status === 'pending'"
                [class.step-row_active]="step.status === 'active'"
                [class.step-row_completed]="step.status === 'completed'"
                [class.step-row_failed]="step.status === 'failed'"
              >
                <div class="step-indicator">
                  @switch (step.status) {
                    @case ('completed') {
                      <span class="step-icon_completed" aria-label="Étape validée">✓</span>
                    }
                    @case ('active') {
                      <span class="step-spinner" aria-label="En cours"></span>
                    }
                    @case ('failed') {
                      <span class="step-icon_failed" aria-label="Erreur">✕</span>
                    }
                    @default {
                      <span class="step-bullet">{{ idx + 1 }}</span>
                    }
                  }
                </div>
                <div class="step-content">
                  <div class="step-header">
                    <span class="step-title">{{ step.label }}</span>
                    @if (step.detail) {
                      <span class="step-badge">{{ step.detail }}</span>
                    }
                  </div>
                  <p class="step-desc">{{ step.description }}</p>
                </div>
              </div>
            }
          </div>

          @if (mode() === 'success') {
            <p class="transition-notice transition-notice_success" role="status">
              La passation est réussie ! Le nœud « {{ data.targetNodeName }} » est désormais le
              Maître actif du cluster.
            </p>
            <div class="countdown-bar">
              <span>Redirection automatique dans {{ countdown() }} s...</span>
              <button matButton type="button" (click)="stopCountdown()">
                Annuler la redirection
              </button>
            </div>
          }

          @if (mode() === 'failed' && errorMessage()) {
            <p class="transition-notice transition-notice_error" role="alert">
              {{ errorMessage() }}
            </p>
          }
        }
      </mat-dialog-content>

      <mat-dialog-actions class="app-dialog-footer" align="end">
        @switch (mode()) {
          @case ('confirm') {
            <button matButton type="button" (click)="dialogRef.close(false)">Annuler</button>
            <button matButton="filled" type="button" (click)="startTransfer()">
              Transférer la gouvernance
            </button>
          }
          @case ('in_progress') {
            <button matButton type="button" disabled>Passation en cours...</button>
          }
          @case ('success') {
            <button matButton="filled" type="button" (click)="redirectToNewMaster()">
              Accéder au nouveau Maître
            </button>
          }
          @case ('failed') {
            <button matButton type="button" (click)="dialogRef.close(false)">Fermer</button>
            <button matButton="filled" type="button" (click)="retry()">Réessayer</button>
          }
        }
      </mat-dialog-actions>
    </app-dialog-frame>
  `,
  styleUrls: ['./leadership-transition-dialog.component.scss'],
})
export class LeadershipTransitionDialogComponent implements OnDestroy {
  protected readonly data = inject<LeadershipTransitionDialogData>(MAT_DIALOG_DATA);
  protected readonly dialogRef = inject(MatDialogRef<LeadershipTransitionDialogComponent>);
  private readonly clusterApi = inject(ClusterApi);

  protected readonly mode = signal<'confirm' | 'in_progress' | 'success' | 'failed'>('confirm');
  protected readonly targetUrl = signal<string>(
    this.data.defaultTargetUrl ||
      (this.data.targetNodeHost ? `http://${this.data.targetNodeHost}:8090/` : ''),
  );
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly countdown = signal<number>(5);

  protected readonly steps = signal<TransitionStep[]>(
    INITIAL_STEPS.map((step) => ({ ...step, status: 'pending' })),
  );

  private countdownTimer: ReturnType<typeof setInterval> | null = null;
  private abortController: AbortController | null = null;
  private resolvedMasterUrl: string | null = null;

  protected readonly frameHeading = computed(() => {
    switch (this.mode()) {
      case 'confirm':
        return 'Passation de Maître';
      case 'in_progress':
        return 'Passation en cours';
      case 'success':
        return 'Passation réussie';
      case 'failed':
        return 'Échec de la passation';
    }
  });

  protected readonly frameVariant = computed<DialogVariant>(() => {
    switch (this.mode()) {
      case 'failed':
        return 'danger';
      case 'in_progress':
        return 'info';
      default:
        return 'neutral';
    }
  });

  protected readonly frameIcon = computed(() => {
    switch (this.mode()) {
      case 'success':
        return '✓';
      case 'failed':
        return '!';
      case 'in_progress':
        return 'i';
      default:
        return '◈';
    }
  });

  constructor() {
    this.dialogRef.addPanelClass('app-dialog-panel');
  }

  ngOnDestroy(): void {
    this.stopCountdown();
    this.abortController?.abort();
  }

  protected onUrlInput(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.targetUrl.set(input?.value ?? '');
  }

  protected async startTransfer(): Promise<void> {
    this.mode.set('in_progress');
    this.errorMessage.set(null);
    this.updateStepStatus('drain', 'active');

    const destinationUrl = this.targetUrl().trim();
    const delay = this.data.animationDelayMs ?? 500;

    try {
      // 1. Drain & Initier le transfert
      const transferPromise = this.clusterApi.transferLeadership(
        this.data.targetNodeId,
        destinationUrl || undefined,
      );

      if (delay > 0) {
        await new Promise((r) => setTimeout(r, delay));
      }
      this.updateStepStatus('drain', 'completed');
      this.updateStepStatus('snapshot', 'active');

      const response = await new Promise<{
        readonly success: boolean;
        readonly message: string;
        readonly newMasterUrl: string | null;
      }>((resolve, reject) => {
        transferPromise.subscribe({
          next: resolve,
          error: reject,
        });
      });

      if (!response.success) {
        throw new Error(response.message || 'Le serveur a rejeté la passation.');
      }

      this.resolvedMasterUrl = response.newMasterUrl || destinationUrl || window.location.origin;

      this.updateStepStatus('snapshot', 'completed');
      this.updateStepStatus('receive', 'completed');
      this.updateStepStatus('switchover', 'active');

      // 2. Bascule en cours
      if (delay > 0) {
        await new Promise((r) => setTimeout(r, delay));
      }
      this.updateStepStatus('switchover', 'completed');
      this.updateStepStatus('health', 'active');

      // 3. Sonde de santé sur le nouveau Maître
      this.abortController = new AbortController();
      const healthy = await this.probeHealth(this.resolvedMasterUrl, this.abortController.signal);

      if (healthy) {
        this.updateStepStatus('health', 'completed');
        this.mode.set('success');
        this.startCountdown();
      } else {
        this.updateStepStatus('health', 'failed', 'Délai d’attente dépassé');
        this.errorMessage.set(
          `Le transfert a été engagé, mais le nouveau Maître (${this.resolvedMasterUrl}) ne répond pas encore sur /health/ready. Vérifiez le conteneur.`,
        );
        this.mode.set('failed');
      }
    } catch (err: unknown) {
      const errorMsg =
        (err as { error?: { message?: string; title?: string } })?.error?.message ??
        (err as { error?: { message?: string; title?: string } })?.error?.title ??
        (err as Error)?.message ??
        'Une erreur est survenue lors de la passation.';

      const currentActive = this.steps().find((s) => s.status === 'active');
      if (currentActive) {
        this.updateStepStatus(currentActive.id, 'failed');
      }
      this.errorMessage.set(errorMsg);
      this.mode.set('failed');
    }
  }

  protected retry(): void {
    this.steps.set(INITIAL_STEPS.map((s) => ({ ...s, status: 'pending' })));
    this.startTransfer();
  }

  protected redirectToNewMaster(): void {
    this.stopCountdown();
    const url = this.resolvedMasterUrl || this.targetUrl() || window.location.origin;
    window.location.href = url;
  }

  protected stopCountdown(): void {
    if (this.countdownTimer) {
      clearInterval(this.countdownTimer);
      this.countdownTimer = null;
    }
  }

  private startCountdown(): void {
    this.countdown.set(5);
    this.countdownTimer = setInterval(() => {
      const next = this.countdown() - 1;
      if (next <= 0) {
        this.redirectToNewMaster();
      } else {
        this.countdown.set(next);
      }
    }, 1000);
  }

  private async probeHealth(baseUrl: string, signal: AbortSignal): Promise<boolean> {
    const target = baseUrl.replace(/\/+$/, '');
    const readyUrl = `${target}/health/ready`;

    for (let attempt = 0; attempt < 35; attempt++) {
      if (signal.aborted) return false;
      try {
        const response = await fetch(readyUrl, {
          method: 'GET',
          cache: 'no-cache',
          signal: AbortSignal.timeout(2000),
        });
        if (response.ok) {
          return true;
        }
      } catch {
        // En cas de politique CORS stricte ou serveur en cours de démarrage, fallback no-cors
        try {
          await fetch(readyUrl, {
            method: 'GET',
            mode: 'no-cors',
            cache: 'no-cache',
            signal: AbortSignal.timeout(2000),
          });
          return true;
        } catch {
          // Serveur pas encore en écoute
        }
      }
      await new Promise((r) => setTimeout(r, 1200));
    }
    return false;
  }

  private updateStepStatus(stepId: string, status: StepStatus, detail?: string): void {
    this.steps.update((steps) =>
      steps.map((s) => (s.id === stepId ? { ...s, status, detail: detail ?? s.detail } : s)),
    );
  }
}
