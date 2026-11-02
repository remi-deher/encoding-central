import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HttpClient } from '@angular/common/http';
import { catchError, exhaustMap, of, timeout, timer } from 'rxjs';
import { SetupNodeSheet } from '../../shared/models/local-node';
import { CommonModule, DOCUMENT } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { AuthService } from '../../core/auth/auth.service';
import { LocalNodeController } from '../../shared/services/local-node-controller';
import { NotificationService } from '../../shared/services/notification.service';
import { formatMemory } from '../../shared/models/node-hardware';
import { AccessTemplateComponent } from '../../shared/templates/access-template.component';

const MINIMUM_PASSWORD_LENGTH = 12;
const MAXIMUM_PASSWORD_LENGTH = 256;
const USERNAME_PATTERN = /^[A-Za-z0-9._@-]{1,64}$/;

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-setup-wizard-page',
  providers: [LocalNodeController],
  imports: [
    CommonModule,
    FormsModule,
    MatButtonModule,
    AccessTemplateComponent,
  ],
  template: `
    <app-access-template
      view="setup"
      layout="single"
      title="Configurer le central"
      description="Sécurisez le central et nommez-le. Les workers s’y rattacheront ensuite."
      [busy]="busy()"
      progressLabel="Action en cours"
    >
      <div access-content class="wizard">
        <p class="progress" aria-live="polite">Étape {{ currentStep() + 1 }} sur 3</p>

        @if (errorMessage() || localController.error()) {
          <p class="setup-error" role="alert">{{ errorMessage() || localController.error() }}</p>
        }

        @switch (currentStep()) {
          @case (0) {
            <section aria-labelledby="step-admin-heading">
              <h2 id="step-admin-heading">Créer l’administrateur</h2>
              <p class="lead">Ce compte protège l’accès au central. Vous pourrez en ajouter d’autres.</p>

              <div class="form-group">
                <label for="admin-username-input" class="field-label">Nom d’utilisateur</label>
                <input
                  id="admin-username-input"
                  [disabled]="busy() || accountCreated()"
                  type="text"
                  class="text-input"
                  [class.invalid]="adminErrors().username"
                  [ngModel]="adminUsername()"
                  (ngModelChange)="adminUsername.set($event)"
                  maxlength="64"
                  autocomplete="username"
                  [attr.aria-invalid]="adminErrors().username ? 'true' : null"
                  aria-describedby="admin-username-error"
                />
                @if (adminErrors().username) {
                  <span id="admin-username-error" class="field-error">{{ adminErrors().username }}</span>
                }
              </div>

              <div class="form-group">
                <label for="admin-password-input" class="field-label">Mot de passe</label>
                <div class="input-with-button">
                  <input
                    id="admin-password-input"
                    [disabled]="busy() || accountCreated()"
                    class="text-input"
                    [class.invalid]="adminErrors().password"
                    [type]="passwordVisible() ? 'text' : 'password'"
                    [ngModel]="adminPassword()"
                    (ngModelChange)="adminPassword.set($event)"
                    autocomplete="new-password"
                    [attr.aria-invalid]="adminErrors().password ? 'true' : null"
                    aria-describedby="admin-password-hint"
                  />
                  <button
                    matButton="text"
                    type="button"
                    [attr.aria-pressed]="passwordVisible()"
                    (click)="passwordVisible.set(!passwordVisible())"
                  >
                    {{ passwordVisible() ? 'Masquer' : 'Afficher' }}
                  </button>
                </div>
                <span
                  id="admin-password-hint"
                  [class]="adminErrors().password ? 'field-error' : 'field-hint'"
                >
                  {{ adminErrors().password || minimumPasswordLength + ' caractères minimum.' }}
                </span>
              </div>

              <div class="form-group">
                <label for="admin-confirm-input" class="field-label">Confirmer le mot de passe</label>
                <input
                  id="admin-confirm-input"
                  [disabled]="busy() || accountCreated()"
                  class="text-input"
                  [class.invalid]="adminErrors().confirm"
                  [type]="passwordVisible() ? 'text' : 'password'"
                  [ngModel]="adminConfirm()"
                  (ngModelChange)="adminConfirm.set($event)"
                  autocomplete="new-password"
                  [attr.aria-invalid]="adminErrors().confirm ? 'true' : null"
                  aria-describedby="admin-confirm-error"
                />
                @if (adminErrors().confirm) {
                  <span id="admin-confirm-error" class="field-error">{{ adminErrors().confirm }}</span>
                }
              </div>

              <div class="workbench-actions">
                <button matButton="filled" type="button" [disabled]="busy()" (click)="nextFromAccount()">
                  Continuer
                </button>
              </div>
            </section>
          }

          @case (1) {
            <section aria-labelledby="step-identity-heading">
              <h2 id="step-identity-heading">Nommer le central</h2>
              <p class="lead">Ces noms s’affichent dans l’interface et chez les workers.</p>

              <div class="form-group">
                <label for="node-name-input" class="field-label">Nom du central</label>
                <input
                  id="node-name-input"
                  [disabled]="busy() || accountCreated()"
                  type="text"
                  class="text-input"
                  [placeholder]="defaultNodeName()"
                  [ngModel]="nodeName()"
                  (ngModelChange)="nodeName.set($event)"
                  maxlength="128"
                  autocomplete="off"
                />
              </div>

              <div class="form-group">
                <label for="cluster-name-input" class="field-label">Nom du cluster</label>
                <input
                  id="cluster-name-input"
                  type="text"
                  class="text-input"
                  [placeholder]="defaultClusterName()"
                  [ngModel]="clusterName()"
                  (ngModelChange)="clusterName.set($event)"
                  maxlength="128"
                  autocomplete="off"
                />
              </div>

              <div class="workbench-actions">
                <button matButton="text" type="button" [disabled]="busy()" (click)="previousStep()">
                  Retour
                </button>
                <button matButton="filled" type="button" [disabled]="busy()" (click)="advanceToReview()">
                  Continuer
                </button>
              </div>
            </section>
          }

          @case (2) {
            <section aria-labelledby="step-review-heading" aria-live="polite">
              <h2 id="step-review-heading">Vérifier le central</h2>
              <dl class="summary">
                <div>
                  <dt>Central</dt>
                  <dd>{{ effectiveNodeName() }}</dd>
                </div>
                <div>
                  <dt>Cluster</dt>
                  <dd>{{ clusterName().trim() || defaultClusterName() }}</dd>
                </div>
                <div>
                  <dt>Administrateur</dt>
                  <dd>{{ adminUsername().trim() }}</dd>
                </div>
                <div>
                  <dt>{{ announcedUrls().length ? 'Adresse annoncée' : 'Adresse utilisée par ce navigateur' }}</dt>
                  <dd>
                    @for (url of networkAccessUrls(); track url; let last = $last) {
                      {{ url }}{{ last ? '' : ' · ' }}
                    }
                  </dd>
                </div>
              </dl>

              <details class="hardware">
                <summary>Matériel détecté · {{ hostPlatformLabel() }}</summary>
                <dl class="summary">
                  <div>
                    <dt>CPU</dt>
                    <dd>{{ cpuInfo() }}</dd>
                  </div>
                  <div>
                    <dt>RAM</dt>
                    <dd>{{ memoryInfo() }}</dd>
                  </div>
                  <div>
                    <dt>GPU</dt>
                    <dd>{{ gpuInfo() }}</dd>
                  </div>
                  <div>
                    <dt>Accélération GPU</dt>
                    <dd>{{ gpuAccelerationInfo() }}</dd>
                  </div>
                  <div>
                    <dt>Interfaces</dt>
                    <dd>
                      @if (setupSheet()?.addresses?.length) {
                        <ul class="network-addresses" aria-label="Adresses des interfaces">
                          @for (item of setupSheet()!.addresses; track item.interface + '/' + item.address) {
                            <li class="mono">{{ item.address }} ({{ item.interface }})</li>
                          }
                        </ul>
                      } @else {
                        {{ setupSheet() ? 'Aucune adresse détectée' : 'Détection en cours…' }}
                      }
                    </dd>
                  </div>
                </dl>
              </details>

              <div class="workbench-actions">
                <button matButton="text" type="button" [disabled]="busy()" (click)="previousStep()">
                  Retour
                </button>
                <button
                  matButton="filled"
                  type="button"
                  [disabled]="busy()"
                  (click)="submitCreateCluster()"
                >
                  Initialiser le central
                </button>
              </div>
            </section>
          }
        }
      </div>
    </app-access-template>
  `,
  styles: `
    :host {
      display: block;
      min-height: 100%;
    }
    .wizard {
      display: grid;
      gap: var(--space-4);
      width: min(100%, 460px);
      margin-inline: auto;
    }
    .progress {
      margin: 0;
      color: var(--text-secondary);
      font-size: var(--font-label);
    }
    h2 {
      margin: 0;
      font-size: var(--font-title-lg);
    }
    .lead {
      margin: var(--space-1) 0 var(--space-4);
      color: var(--text-secondary);
    }
    .form-group {
      display: grid;
      gap: var(--space-1);
      margin-bottom: var(--space-4);
    }
    .field-label {
      font-weight: 600;
    }
    .text-input {
      width: 100%;
      min-height: 44px;
      padding: 0 var(--space-3);
      border: 1px solid var(--outline);
      border-radius: var(--radius-md);
      background: var(--surface-container-lowest);
      color: inherit;
      font: inherit;
    }
    .text-input.invalid {
      border-color: var(--danger);
    }
    .input-with-button {
      display: flex;
      gap: var(--space-2);
      align-items: center;
    }
    .input-with-button .text-input {
      flex: 1;
    }
    .field-hint {
      color: var(--text-secondary);
      font-size: var(--font-label);
    }
    .field-error,
    .setup-error {
      color: var(--danger);
      font-size: var(--font-label);
    }
    .setup-error {
      margin: 0;
      padding: var(--space-3);
      border-radius: var(--radius-md);
      background: var(--danger-soft);
    }
    .summary {
      display: grid;
      gap: var(--space-3);
      margin: var(--space-4) 0;
    }
    .summary > div {
      display: grid;
      grid-template-columns: 9rem 1fr;
      gap: var(--space-3);
    }
    .summary dt {
      color: var(--text-secondary);
    }
    .summary dd {
      margin: 0;
      overflow-wrap: anywhere;
    }
    .hardware {
      margin-bottom: var(--space-4);
      color: var(--text-secondary);
    }
    .hardware summary {
      cursor: pointer;
    }
    .network-addresses {
      display: grid;
      gap: var(--space-1);
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .mono {
      display: block;
      font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace;
    }
    .workbench-actions {
      display: flex;
      justify-content: flex-end;
      gap: var(--space-2);
    }
  `,
})
export class SetupWizardPageComponent {
  private readonly document = inject(DOCUMENT);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly notifications = inject(NotificationService);
  private readonly http = inject(HttpClient, { optional: true });
  private readonly destroyRef = inject(DestroyRef);
  protected readonly localController = inject(LocalNodeController);
  protected readonly setupSheet = signal<SetupNodeSheet | null>(null);

  protected readonly currentStep = signal(0);
  protected readonly busy = signal(false);
  protected readonly errorMessage = signal('');

  protected readonly nodeName = signal('');
  protected readonly clusterName = signal('');
  protected readonly adminUsername = signal('admin');
  protected readonly adminPassword = signal('');
  protected readonly adminConfirm = signal('');
  protected readonly passwordVisible = signal(false);
  protected readonly adminSubmitted = signal(false);
  protected readonly accountCreated = signal(false);
  protected readonly minimumPasswordLength = MINIMUM_PASSWORD_LENGTH;

  constructor() {
    if (this.http) {
      timer(0, 5_000)
        .pipe(
          exhaustMap(() =>
            this.accountCreated()
              ? of(null)
              : this.http!.get<SetupNodeSheet>('/api/v1/auth/setup/node').pipe(
                  timeout(8_000),
                  catchError(() => of(null)),
                ),
          ),
          takeUntilDestroyed(this.destroyRef),
        )
        .subscribe((sheet) => {
          if (sheet) {
            this.setupSheet.set(sheet);
          }
        });
    }
  }
  /** Mêmes règles que le serveur ; les erreurs apparaissent après saisie ou première validation. */
  protected readonly adminErrors = computed(() => {
    if (this.accountCreated()) return { username: '', password: '', confirm: '' };
    const username = this.adminUsername().trim();
    const password = this.adminPassword();
    const confirm = this.adminConfirm();
    const shown = this.adminSubmitted();
    return {
      username: USERNAME_PATTERN.test(username)
        ? ''
        : 'Le nom d’utilisateur compte de 1 à 64 caractères : lettres, chiffres, point, tiret, tiret bas ou @.',
      password:
        password.length > MAXIMUM_PASSWORD_LENGTH
          ? `Le mot de passe ne peut pas dépasser ${MAXIMUM_PASSWORD_LENGTH} caractères.`
          : password.length < MINIMUM_PASSWORD_LENGTH && (shown || password.length > 0)
            ? `Le mot de passe doit contenir au moins ${MINIMUM_PASSWORD_LENGTH} caractères.`
            : '',
      confirm:
        confirm.length > 0 || shown
          ? confirm === password
            ? ''
            : 'Les deux mots de passe sont différents.'
          : '',
    };
  });

  protected nextFromAccount(): void {
    this.adminSubmitted.set(true);
    const errors = this.adminErrors();
    if (errors.username || errors.password || errors.confirm) return;
    this.currentStep.set(1);
  }

  protected previousStep(): void {
    this.currentStep.update((step) => Math.max(0, step - 1));
  }

  protected advanceToReview(): void {
    this.currentStep.set(2);
  }

  protected readonly defaultNodeName = computed(() => {
    const status = this.localController.status();
    const sheet = this.setupSheet();
    return (
      status?.displayName || sheet?.displayName || status?.name || sheet?.name || 'Mon Serveur'
    );
  });

  protected readonly effectiveNodeName = computed(() => {
    return this.nodeName().trim() || this.defaultNodeName();
  });

  protected readonly resolvedInventory = computed(
    () => this.localController.status()?.inventory ?? this.setupSheet()?.inventory ?? null,
  );

  protected readonly hostPlatformLabel = computed(() => {
    const status = this.localController.status();
    const sheet = this.setupSheet();
    const inv = this.resolvedInventory();
    const host = status?.name || sheet?.name || 'localhost';
    if (!inv) return `Hôte : ${host}`;
    return `${host} · ${inv.operatingSystem} (${inv.architecture})`;
  });

  // The browser retains the Docker host and published port, unlike the container's
  // interfaces or default gateway. Configured public/advertised URLs take precedence.
  protected readonly announcedUrls = computed(() => this.setupSheet()?.urls ?? []);

  protected readonly networkAccessUrls = computed(() => {
    const urls = this.setupSheet()?.urls ?? [];
    return urls.length ? urls : [this.document.location.origin];
  });

  protected readonly defaultClusterName = computed(() => {
    return 'Cluster ' + this.effectiveNodeName();
  });

  protected readonly cpuInfo = computed(() => {
    const inv = this.resolvedInventory();
    if (!inv) return 'Détection en cours…';
    const model = inv.processorModel?.trim();
    if (model && inv.processorCount > 0) {
      return `${model} (${inv.processorCount} cœurs)`;
    }
    return model || `${inv.processorCount} cœurs`;
  });

  protected readonly memoryInfo = computed(() => {
    const inv = this.resolvedInventory();
    if (!inv) return 'Détection en cours…';
    return formatMemory(inv.memoryMb);
  });

  protected readonly gpuInfo = computed(() => {
    const inv = this.resolvedInventory();
    if (!inv) return 'Détection en cours…';
    if (!inv.gpus || inv.gpus.length === 0) return 'Aucun GPU dédié détecté';
    return inv.gpus.map((g) => g.name).join(' · ');
  });

  protected readonly gpuAccelerationInfo = computed(() => {
    const status = this.localController.status();
    const sheet = this.setupSheet();
    const caps = status?.capabilities ?? sheet?.tools ?? null;
    if (!caps || caps.length === 0) {
      if (sheet && !sheet.agentAvailable) return 'Agent local indisponible';
      return 'Détection en cours…';
    }

    const ACCEL_DISPLAY: Readonly<Record<string, string>> = {
      nvenc: 'NVENC',
      qsv: 'QSV',
      vaapi: 'VAAPI',
      amf: 'AMF',
      videotoolbox: 'VideoToolbox',
      cuda: 'CUDA',
      d3d11va: 'D3D11VA',
    };

    const supported = new Set<string>();
    for (const tool of caps) {
      for (const check of tool.encoderChecks ?? []) {
        if (check.status === 'usable') {
          const suffix = check.encoderId.split('_').pop() ?? '';
          if (suffix && suffix !== 'cpu' && suffix !== 'process' && suffix !== 'general') {
            supported.add(suffix.toLowerCase());
          }
        }
      }
      for (const accel of tool.accelerationTypes ?? []) {
        if (accel && accel !== 'cpu' && accel !== 'process' && accel !== 'general') {
          supported.add(accel.toLowerCase());
        }
      }
    }

    if (supported.size === 0) return 'Aucune (CPU)';

    const preferredOrder = ['nvenc', 'qsv', 'vaapi', 'amf', 'videotoolbox'];
    const ordered = [
      ...preferredOrder.filter((id) => supported.has(id)),
      ...Array.from(supported).filter((id) => !preferredOrder.includes(id)),
    ];

    return ordered.map((id) => ACCEL_DISPLAY[id] ?? id.toUpperCase()).join(' · ');
  });

  protected goToStep(index: number): void {
    this.errorMessage.set('');
    this.currentStep.set(index);
  }

  protected async submitCreateCluster(): Promise<void> {
    const name = this.clusterName().trim() || this.defaultClusterName();
    await this.completeSetup({
      nodeDisplayName: this.effectiveNodeName(),
      clusterName: name,
    });
  }

  private async completeSetup(
    payload: {
      nodeDisplayName: string;
      clusterName?: string;
    },
  ): Promise<void> {
    if (this.busy() || this.accountCreated()) return;
    this.errorMessage.set('');
    this.adminSubmitted.set(true);
    const errors = this.adminErrors();
    if (errors.username || errors.password || errors.confirm) {
      document.getElementById('step-admin-heading')?.scrollIntoView?.({ block: 'center' });
      return;
    }
    this.busy.set(true);
    try {
      await this.auth.setup({
        ...payload,
        username: this.adminUsername().trim(),
        password: this.adminPassword(),
      });
      this.accountCreated.set(true);
      this.adminPassword.set('');
      this.adminConfirm.set('');
      this.notifications.show('Initialisation terminée avec succès !');
      await this.router.navigate(['/']);
    } catch (error) {
      this.errorMessage.set(
        AuthService.errorMessage(error, 'L’initialisation a échoué. Veuillez réessayer.'),
      );
    } finally {
      this.busy.set(false);
    }
  }
}
