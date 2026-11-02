import { ChangeDetectionStrategy, Component, computed, DOCUMENT, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { AuthService } from '../../core/auth/auth.service';
import { formatMemory } from '../../shared/models/node-hardware';
import { LocalNodeController } from '../../shared/services/local-node-controller';
import { AccessTemplateComponent } from '../../shared/templates/access-template.component';
import { DetectedConfigurationComponent } from './detected-configuration.component';
import { SETUP_SURFACE_STYLES } from './setup-surface.styles';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-worker-setup-page',
  providers: [LocalNodeController],
  imports: [AccessTemplateComponent, DetectedConfigurationComponent, FormsModule, MatButtonModule],
  template: `
    <app-access-template
      view="setup"
      layout="single"
      title="Configurer le worker"
      description="Rattachez cette machine au central qui lui distribuera les traitements."
      [busy]="busy()"
      progressLabel="Configuration du worker"
    >
      <div access-content class="setup-stack">
        <p class="progress" aria-live="polite">Étape {{ step() + 1 }} sur 3</p>

        @if (errorMessage() || local.error()) {
          <p class="error" role="alert">{{ errorMessage() || local.error() }}</p>
        }

        @if (step() === 0) {
          <section class="card" aria-labelledby="central-address-heading">
            <div class="heading">
              <div>
                <h2 id="central-address-heading">Choisir le central</h2>
                <p>L’adresse est enregistrée localement et utilisée pour tous les échanges.</p>
              </div>
            </div>
            <label for="central-address">Adresse du central</label>
            <input
              id="central-address"
              type="url"
              [(ngModel)]="centralAddress"
              [disabled]="busy()"
              placeholder="https://encoding.example.net"
              autocomplete="url"
              required
            />
            <div class="actions">
              <button matButton="filled" type="button" [disabled]="busy()" (click)="connect()">
                Vérifier et continuer
              </button>
            </div>
          </section>
        }

        @if (step() === 1) {
          <section class="card" aria-labelledby="worker-login-heading">
            <div class="heading">
              <div>
                <h2 id="worker-login-heading">Se connecter au central</h2>
                <p>Utilisez un compte administrateur du central sélectionné.</p>
              </div>
            </div>
            <label for="worker-username">Nom d’utilisateur</label>
            <input
              id="worker-username"
              type="text"
              [(ngModel)]="username"
              [disabled]="busy()"
              autocomplete="username"
            />
            <label for="worker-password">Mot de passe</label>
            <input
              id="worker-password"
              type="password"
              [(ngModel)]="password"
              [disabled]="busy()"
              autocomplete="current-password"
            />
            <div class="actions">
              <button matButton="outlined" type="button" [disabled]="busy()" (click)="back()">
                Retour
              </button>
              <button matButton="filled" type="button" [disabled]="busy()" (click)="login()">
                Se connecter
              </button>
            </div>
          </section>
        }

        @if (step() === 2) {
          <section class="card" aria-labelledby="worker-enrollment-heading">
            <div class="heading">
              <div>
                <h2 id="worker-enrollment-heading">Rattacher le worker</h2>
                <p>Envoyez la demande d’adhésion, puis autorisez-la depuis le central.</p>
              </div>
            </div>
            @if (!local.target()) {
              <div class="notice">
                Le worker ne gère ni bibliothèques ni flows : il reçoit uniquement les capacités,
                plugins, outils et traitements décidés par le central.
              </div>
              <div class="actions">
                <button matButton="outlined" type="button" [disabled]="busy()" (click)="back()">
                  Retour
                </button>
                <button
                  matButton="filled"
                  type="button"
                  [disabled]="busy() || local.busy() || local.loading() || local.agentUnavailable()"
                  (click)="verifyEnrollment()"
                >
                  Préparer le rattachement
                </button>
              </div>
            } @else if (local.pending()) {
              <div class="notice" role="status">
                Demande envoyée à <strong>{{ local.targetName() }}</strong>. Autorisez ce worker
                depuis le central, puis cette page se mettra à jour automatiquement.
              </div>
            } @else if (local.status()?.isEnrolled) {
              <div class="notice" role="status">
                Ce worker est rattaché à <strong>{{ local.status()?.clusterName }}</strong>.
              </div>
              <div class="actions">
                <button matButton="filled" type="button" (click)="openNodes()">
                  Voir le worker
                </button>
              </div>
            } @else {
              <div class="notice">
                Le central <strong>{{ local.targetName() }}</strong> est prêt à recevoir ce worker.
              </div>
              <div class="actions">
                <button matButton="outlined" type="button" [disabled]="busy()" (click)="back()">
                  Retour
                </button>
                <button
                  matButton="filled"
                  type="button"
                  [disabled]="busy() || local.busy() || !local.canJoin()"
                  (click)="sendEnrollment()"
                >
                  Envoyer la demande
                </button>
              </div>
            }
          </section>
        }

        <app-detected-configuration
          [system]="workerSystem()"
          [cpu]="workerCpu()"
          [memory]="workerMemory()"
          [gpu]="workerGpu()"
          [acceleration]="workerAcceleration()"
          [address]="workerAddress()"
        />
      </div>
    </app-access-template>
  `,
  styles: SETUP_SURFACE_STYLES,
})
export class WorkerSetupPageComponent {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly document = inject(DOCUMENT);
  protected readonly local = inject(LocalNodeController);

  protected centralAddress = '';
  protected username = '';
  protected password = '';
  protected readonly step = signal(0);
  protected readonly busy = signal(false);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly workerSystem = computed(() => {
    const inventory = this.local.status()?.inventory;
    if (!inventory) return 'Détection en cours…';
    const runtime = {
      docker: 'Docker',
      podman: 'Podman',
      container: 'Conteneur',
    }[inventory.runtimeEnvironment ?? ''];
    const platform = `${inventory.operatingSystem} (${inventory.architecture})`;
    return runtime ? `${runtime} · ${platform}` : platform;
  });
  protected readonly workerCpu = computed(() => {
    const inventory = this.local.status()?.inventory;
    if (!inventory) return 'Détection en cours…';
    const model = inventory.processorModel?.trim() || 'Processeur';
    return inventory.processorCount > 0 ? `${model} (${inventory.processorCount} cœurs)` : model;
  });
  protected readonly workerMemory = computed(() => {
    const inventory = this.local.status()?.inventory;
    return inventory ? formatMemory(inventory.memoryMb) : 'Détection en cours…';
  });
  protected readonly workerGpu = computed(() => {
    const gpus = this.local.status()?.inventory?.gpus;
    if (!gpus) return 'Détection en cours…';
    return gpus.length ? gpus.map((gpu) => gpu.name).join(' · ') : 'Aucun GPU dédié détecté';
  });
  protected readonly workerAcceleration = computed(() => {
    const inventory = this.local.status()?.inventory;
    if (!inventory) return 'Détection en cours…';
    if (!inventory.gpus?.length) return 'Aucune (CPU)';
    const labels: Readonly<Record<string, string>> = {
      nvenc: 'NVENC',
      qsv: 'Quick Sync',
      vaapi: 'VAAPI',
      amf: 'AMF',
      videotoolbox: 'VideoToolbox',
    };
    const accelerators = new Set<string>();
    for (const capability of this.local.status()?.capabilities ?? []) {
      for (const accelerator of capability.accelerationTypes) {
        if (labels[accelerator]) accelerators.add(labels[accelerator]);
      }
    }
    return accelerators.size ? [...accelerators].join(' · ') : 'Validation en cours';
  });
  protected readonly workerAddress = computed(() => this.document.location.hostname || 'Indisponible');

  protected async connect(): Promise<void> {
    this.busy.set(true);
    this.errorMessage.set(null);
    try {
      await firstValueFrom(
        this.http.post('/api/v1/worker/clusters', { apiBaseUrl: this.centralAddress.trim() }),
      );
      const status = await this.auth.ensureLoaded(true);
      if (status.setupRequired) {
        this.errorMessage.set('Le central doit être initialisé directement depuis son interface.');
        return;
      }
      this.step.set(1);
    } catch (error) {
      this.errorMessage.set(AuthService.errorMessage(error, 'Le central est inaccessible.'));
    } finally {
      this.busy.set(false);
    }
  }

  protected async login(): Promise<void> {
    this.busy.set(true);
    this.errorMessage.set(null);
    try {
      await this.auth.login(this.username, this.password);
      this.password = '';
      this.local.refresh();
      this.step.set(2);
    } catch (error) {
      this.errorMessage.set(AuthService.errorMessage(error, 'Les identifiants sont invalides.'));
    } finally {
      this.busy.set(false);
    }
  }

  protected async verifyEnrollment(): Promise<void> {
    await this.local.verifyAddress({ address: this.centralAddress.trim() });
  }

  protected async sendEnrollment(): Promise<void> {
    await this.local.sendRequest();
  }

  protected back(): void {
    this.errorMessage.set(null);
    this.step.update((step) => Math.max(0, step - 1));
  }

  protected openNodes(): void {
    void this.router.navigate(['/nodes']);
  }
}
