import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  DOCUMENT,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HttpClient } from '@angular/common/http';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { AuthService } from '../../core/auth/auth.service';
import { WorkerInventory } from '../../shared/models/central';
import { DiscoveredCentral } from '../../shared/models/local-worker';
import { formatMemory } from '../../shared/models/worker-hardware';
import { LocalWorkerController } from '../../shared/services/local-worker-controller';
import { AccessTemplateComponent } from '../../shared/templates/access-template.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { DetectedConfigurationComponent } from './detected-configuration.component';
import { SETUP_SURFACE_STYLES } from './setup-surface.styles';

/** Matériel détecté, lu sans secret avant le rattachement (`GET /api/v1/worker/hardware`). */
interface WorkerHardware {
  readonly inventory: WorkerInventory;
  readonly tools: readonly {
    readonly toolId: string;
    readonly accelerationTypes: readonly string[];
  }[];
}

const DETECTED_STYLES = `
  .detected { display: grid; gap: 0.5rem; margin-bottom: 1rem; }
  .detected-heading { display: flex; align-items: center; justify-content: space-between; gap: 0.75rem; flex-wrap: wrap; }
  .detected-heading h3 { margin: 0; font-size: 1rem; }
  .detected ul { list-style: none; margin: 0; padding: 0; display: grid; gap: 0.5rem; }
  .detected li button { width: 100%; justify-content: flex-start; gap: 0.75rem; }
  .detected li span { color: var(--text-secondary); }
  .detected p { margin: 0; color: var(--text-secondary); }
`;

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-worker-setup-page',
  providers: [LocalWorkerController],
  imports: [
    AccessTemplateComponent,
    DetectedConfigurationComponent,
    ReactiveFormsModule,
    IconComponent,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
  ],
  template: `
    <app-access-template
      view="setup"
      layout="single"
      title="Configurer le worker"
      description="Rattachez cette machine au central qui lui distribuera les traitements."
      [busy]="busy()"
      progressLabel="Configuration du worker"
    >
      <form access-content class="setup-stack" [formGroup]="form" novalidate>
        @if (!welcome()) {
          <p class="progress" aria-live="polite">Étape {{ step() + 1 }} sur 3</p>
        }

        @if (errorMessage() || local.error()) {
          <p class="error" role="alert">{{ errorMessage() || local.error() }}</p>
        }

        @if (welcome()) {
          <section class="card" aria-labelledby="welcome-heading">
            <div class="heading">
              <h2 id="welcome-heading">Bienvenue</h2>
              <p>
                Ce guide en trois étapes rattache cette machine à un central : choisir le central,
                s’y connecter avec un compte administrateur, puis rattacher le worker. Vérifiez
                d’abord que la machine détectée ci-dessous est la bonne.
              </p>
            </div>
            <div class="actions">
              <button matButton="filled" type="button" (click)="start()">
                Commencer
                <app-icon name="forward" matButtonIcon iconPositionEnd />
              </button>
            </div>
          </section>

          <app-detected-configuration
            [system]="workerSystem()"
            [cpu]="workerCpu()"
            [memory]="workerMemory()"
            [gpu]="workerGpu()"
            [acceleration]="workerAcceleration()"
            [address]="workerAddress()"
          />
        } @else {
          @if (step() === 0) {
            <section class="card" aria-labelledby="central-address-heading">
              <div class="heading">
                <div>
                  <h2 id="central-address-heading">Choisir le central</h2>
                  <p>L’adresse est enregistrée localement et utilisée pour tous les échanges.</p>
                </div>
              </div>
              <div class="detected" aria-live="polite">
                <div class="detected-heading">
                  <h3>Centraux détectés sur le réseau</h3>
                  <button
                    matButton="outlined"
                    type="button"
                    [disabled]="busy() || discovering()"
                    (click)="refreshDetected()"
                  >
                    Actualiser la découverte
                  </button>
                </div>
                @if (detected().length) {
                  <ul>
                    @for (central of detected(); track central.id) {
                      <li>
                        <button
                          matButton="outlined"
                          type="button"
                          [disabled]="busy()"
                          (click)="chooseDetected(central)"
                        >
                          <strong>{{ central.name }}</strong>
                          <span>{{ central.apiBaseUrl }}</span>
                        </button>
                      </li>
                    }
                  </ul>
                } @else {
                  <p>
                    {{
                      discovering()
                        ? 'Recherche en cours…'
                        : 'Aucun central détecté. La découverte DNS-SD ne traverse pas toujours les conteneurs ni les sous-réseaux : saisissez son adresse ci-dessous.'
                    }}
                  </p>
                }
              </div>

              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Adresse du central</mat-label>
                <input
                  matInput
                  id="central-address"
                  type="url"
                  formControlName="centralAddress"
                  [disabled]="busy()"
                  placeholder="https://encoding.example.net"
                  autocomplete="url"
                  required
                />
              </mat-form-field>
              <div class="actions">
                <button matButton="outlined" type="button" [disabled]="busy()" (click)="back()">
                  Retour
                </button>
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
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Nom d’utilisateur</mat-label>
                <input
                  matInput
                  id="worker-username"
                  type="text"
                  formControlName="username"
                  [disabled]="busy()"
                  autocomplete="username"
                  required
                />
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Mot de passe</mat-label>
                <input
                  matInput
                  id="worker-password"
                  type="password"
                  formControlName="password"
                  [disabled]="busy()"
                  autocomplete="current-password"
                  required
                />
              </mat-form-field>
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
                <h2 id="worker-enrollment-heading">Rattacher le worker</h2>
                <p>
                  Votre compte administrateur du central autorise ce rattachement : aucune
                  validation n’est à faire ailleurs.
                </p>
              </div>
              @if (signedInAs(); as account) {
                <div class="notice" role="status">
                  Session déjà ouverte sur ce central en tant que <strong>{{ account }}</strong> :
                  elle sera utilisée pour le rattachement, sans nouvelle saisie d’identifiants.
                </div>
              }
              @if (local.status()?.isEnrolled) {
                <div class="notice" role="status">
                  Ce worker est rattaché à <strong>{{ local.status()?.centralName }}</strong
                  >.
                </div>
                <div class="actions">
                  <button matButton="filled" type="button" (click)="openNodes()">
                    Voir le worker
                  </button>
                </div>
              } @else {
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
                    [disabled]="
                      busy() || local.busy() || local.loading() || local.agentUnavailable()
                    "
                    (click)="attach()"
                  >
                    Rattacher ce worker
                  </button>
                </div>
              }
            </section>
          }
        }
      </form>
    </app-access-template>
  `,
  styles: SETUP_SURFACE_STYLES + DETECTED_STYLES,
})
export class WorkerSetupPageComponent {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly local = inject(LocalWorkerController);

  protected readonly form = new FormGroup({
    centralAddress: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    username: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    password: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
  });
  protected get centralAddress(): string {
    return this.form.controls.centralAddress.value;
  }
  protected set centralAddress(value: string) {
    this.form.controls.centralAddress.setValue(value);
  }
  protected readonly detected = signal<readonly DiscoveredCentral[]>([]);
  protected readonly discovering = signal(false);
  /** Compte administrateur déjà connecté au central choisi (session relayée) : l'étape de connexion est alors inutile. */
  protected readonly signedInAs = signal<string | null>(null);
  protected get username(): string {
    return this.form.controls.username.value;
  }
  protected set username(value: string) {
    this.form.controls.username.setValue(value);
  }
  protected get password(): string {
    return this.form.controls.password.value;
  }
  protected set password(value: string) {
    this.form.controls.password.setValue(value);
  }
  protected readonly step = signal(0);
  /** Accueil : n'est pas une étape, il présente la machine détectée avant de commencer. */
  protected readonly welcome = signal(true);
  private readonly hardware = signal<WorkerHardware | null>(null);
  private readonly inventory = computed(
    () => this.local.status()?.inventory ?? this.hardware()?.inventory ?? null,
  );
  private readonly capabilities = computed(
    () => this.local.status()?.capabilities ?? this.hardware()?.tools ?? [],
  );
  protected readonly busy = signal(false);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly workerSystem = computed(() => {
    const inventory = this.inventory();
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
    const inventory = this.inventory();
    if (!inventory) return 'Détection en cours…';
    const model = inventory.processorModel?.trim() || 'Processeur';
    return inventory.processorCount > 0 ? `${model} (${inventory.processorCount} cœurs)` : model;
  });
  protected readonly workerMemory = computed(() => {
    const inventory = this.inventory();
    return inventory ? formatMemory(inventory.memoryMb) : 'Détection en cours…';
  });
  protected readonly workerGpu = computed(() => {
    const gpus = this.inventory()?.gpus;
    if (!gpus) return 'Détection en cours…';
    return gpus.length ? gpus.map((gpu) => gpu.name).join(' · ') : 'Aucun GPU dédié détecté';
  });
  protected readonly workerAcceleration = computed(() => {
    const inventory = this.inventory();
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
    for (const capability of this.capabilities()) {
      for (const accelerator of capability.accelerationTypes) {
        if (labels[accelerator]) accelerators.add(labels[accelerator]);
      }
    }
    return accelerators.size ? [...accelerators].join(' · ') : 'Validation en cours';
  });
  protected readonly workerAddress = computed(
    () => this.document.location.hostname || 'Indisponible',
  );

  constructor() {
    // Route ouverte tant que le worker n'est pas rattaché : le matériel est lisible avant la connexion au central.
    this.http
      .get<WorkerHardware>('/api/v1/worker/hardware')
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ next: (value) => this.hardware.set(value), error: () => undefined });
    void this.refreshDetected();
  }

  /** Centraux vus par la découverte DNS-SD de l'agent ; la liste est lisible tant qu'aucun central n'est choisi. */
  protected async refreshDetected(): Promise<void> {
    this.discovering.set(true);
    try {
      this.detected.set(
        await firstValueFrom(
          this.http.get<readonly DiscoveredCentral[]>('/api/v1/worker/centrals'),
        ),
      );
    } catch {
      this.detected.set([]);
    } finally {
      this.discovering.set(false);
    }
  }

  protected async chooseDetected(central: DiscoveredCentral): Promise<void> {
    this.centralAddress = central.apiBaseUrl;
    await this.connect();
  }

  protected async connect(): Promise<void> {
    if (this.form.controls.centralAddress.invalid) {
      this.form.controls.centralAddress.markAsTouched();
      return;
    }
    this.busy.set(true);
    this.errorMessage.set(null);
    try {
      await firstValueFrom(
        this.http.post('/api/v1/worker/centrals', { apiBaseUrl: this.centralAddress.trim() }),
      );
      const status = await this.auth.ensureLoaded(true);
      if (status.setupRequired) {
        this.errorMessage.set('Le central doit être initialisé directement depuis son interface.');
        return;
      }
      this.signedInAs.set(null);
      if (status.user?.role === 'admin') {
        this.signedInAs.set(status.user.username);
        this.local.refresh();
        this.step.set(2);
        return;
      }
      if (status.user) {
        this.errorMessage.set(
          `La session ouverte sur ce central (${status.user.username}) n’est pas administratrice : connectez-vous avec un compte administrateur.`,
        );
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

  protected async attach(): Promise<void> {
    this.busy.set(true);
    this.errorMessage.set(null);
    try {
      await this.local.verifyAddress({ address: this.centralAddress.trim() });
      if (this.local.error() || !this.local.target()) return;
      await this.local.enroll();
    } finally {
      this.busy.set(false);
    }
  }

  protected start(): void {
    this.welcome.set(false);
  }

  protected back(): void {
    this.errorMessage.set(null);
    if (this.step() === 0) {
      this.welcome.set(true);
      return;
    }
    this.step.update((step) => (step === 2 && this.signedInAs() ? 0 : step - 1));
  }

  protected openNodes(): void {
    void this.router.navigate(['/workers']);
  }
}
