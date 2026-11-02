import { IconComponent } from '../../shared/ui/icon.component';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { catchError, exhaustMap, firstValueFrom, of, takeWhile, timeout, timer } from 'rxjs';
import { SetupNodeSheet } from '../../shared/models/local-node';
import { CommonModule, DOCUMENT } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { AuthService } from '../../core/auth/auth.service';
import { ShellConfig } from '../../core/shell/shell-config';
import { LocalNodeController } from '../../shared/services/local-node-controller';
import { NotificationService } from '../../shared/services/notification.service';
import { formatMemory } from '../../shared/models/node-hardware';
import { AccessTemplateComponent } from '../../shared/templates/access-template.component';

interface WorkerLinkStatus {
  readonly enrolled: boolean;
  readonly centralName: string | null;
  readonly centralUrl: string | null;
  readonly verificationCode: string | null;
  readonly expiresAt: string | null;
}

function linkError(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    const title = error.error?.title;
    if (typeof title === 'string' && title.trim()) return title;
  }
  return 'Le central n’a pas pu être contacté.';
}

const MINIMUM_PASSWORD_LENGTH = 12;
const MAXIMUM_PASSWORD_LENGTH = 256;
const USERNAME_PATTERN = /^[A-Za-z0-9._@-]{1,64}$/;

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-setup-wizard-page',
  providers: [LocalNodeController],
  imports: [IconComponent, CommonModule, FormsModule, MatButtonModule, AccessTemplateComponent],
  template: `
    <app-access-template
      view="setup"
      [title]="shell.worker() ? 'Worker Encoding' : 'Bienvenue sur Encoding'"
      [description]="
        shell.worker()
          ? 'Nommez cette machine, puis reliez-la avec un compte administrateur du central.'
          : 'Nommez cette machine, créez l’administrateur, puis ouvrez le central.'
      "
      [busy]="busy()"
      progressLabel="Action en cours"
    >
      @if (errorMessage() || localController.error()) {
        <div access-alert role="alert" class="setup-error">
          <svg
            viewBox="0 0 24 24"
            width="20"
            height="20"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            aria-hidden="true"
          >
            <circle cx="12" cy="12" r="10" />
            <path d="M12 8v4m0 4h.01" stroke-linecap="round" />
          </svg>
          <span>{{ errorMessage() || localController.error() }}</span>
        </div>
      }

      <aside access-context class="machine-facts" aria-label="Identité et matériel détecté">
        <div>
          <span class="fact-label">Machine</span>
          <span class="fact-value">{{ defaultNodeName() }}</span>
          <span class="fact-hint">{{ hostPlatformLabel() }}</span>
        </div>
        <div>
          <span class="fact-label">Accès au nœud</span>
          @for (url of networkAccessUrls(); track url) {
            <span class="fact-value fact-value--mono">{{ url }}</span>
          }
          <span class="fact-hint">{{ networkAccessSource() }}</span>
        </div>
        <div>
          <span class="fact-label">Adresses des interfaces</span>
          @if (setupSheet()?.addresses?.length) {
            <ul class="network-addresses" aria-label="Adresses des interfaces">
              @for (
                item of setupSheet()!.addresses;
                track item.interface + '/' + item.address
              ) {
                <li>{{ item.address }} ({{ item.interface }})</li>
              }
            </ul>
          } @else {
            <span class="fact-value">{{
              setupSheet() ? 'Aucune adresse détectée' : 'Détection en cours…'
            }}</span>
          }
        </div>
        <div>
          <span class="fact-label">Processeur</span>
          <span class="fact-value">{{ cpuInfo() }}</span>
        </div>
        <div>
          <span class="fact-label">Mémoire</span>
          <span class="fact-value">{{ memoryInfo() }}</span>
        </div>
        <div>
          <span class="fact-label">Graphique / GPU</span>
          <span class="fact-value">{{ gpuInfo() }}</span>
        </div>
        <div>
          <span class="fact-label">Accélération GPU</span>
          <span class="fact-value">{{ gpuAccelerationInfo() }}</span>
        </div>
      </aside>

      <!-- Colonne droite : Atelier de configuration directe -->
      <div access-content class="setup-workbench">
        <nav class="setup-progress" aria-label="Étapes">
          @for (step of steps(); track step.id; let index = $index; let last = $last) {
            <span [class.is-current]="index === stepIndex()" [class.is-done]="index < stepIndex()">{{
              step.label
            }}</span>
            @if (!last) {
              <span aria-hidden="true">/</span>
            }
          }
        </nav>

        @if (currentStep().id === 'name') {
        <section class="workbench-card" aria-labelledby="step-identity-heading">
          <div class="section-heading">
            <div>
              <h2 id="step-identity-heading">Nommer ce nœud</h2>
              <p>
                {{
                  shell.worker()
                    ? 'Ce nom sera visible sur le central après le rattachement.'
                    : 'Ce nom désigne le central, interface et encodage local compris.'
                }}
              </p>
            </div>
          </div>
          <div class="form-group">
            <label for="node-name-input" class="field-label">Nom de ce nœud</label>
            <input
              id="node-name-input"
              [disabled]="busy()"
              type="text"
              class="text-input"
              [placeholder]="defaultNodeName()"
              [ngModel]="nodeName()"
              (ngModelChange)="nodeName.set($event)"
              maxlength="128"
              autocomplete="off"
            />
            <div class="Quick-suggestions" aria-label="Suggestions de noms">
              <span class="suggestions-label">Suggestions :</span>
              @for (suggestion of nodeNameSuggestions(); track suggestion) {
                <button
                  type="button"
                  class="suggestion-chip"
                  [disabled]="busy()"
                  [class.active]="nodeName() === suggestion"
                  (click)="nodeName.set(suggestion)"
                >
                  {{ suggestion }}
                </button>
              }
            </div>
          </div>
          <div class="workbench-actions">
            <button matButton="filled" type="button" [disabled]="busy()" (click)="nextStep()">
              Continuer
            </button>
          </div>
        </section>
        }

        @if (currentStep().id === 'admin') {
        <section class="workbench-card" aria-labelledby="step-admin-heading">
          <div class="section-heading">
            <div>
              <h2 id="step-admin-heading">
                {{ accountCreated() ? 'Administrateur créé' : 'Créer l’administrateur' }}
              </h2>
              <p>Ce compte protège l’accès au central. Vous pourrez en ajouter d’autres ensuite.</p>
            </div>
          </div>

          @if (accountCreated()) {
            <p>
              Connecté avec le compte <strong>{{ adminUsername() }}</strong
              >.
            </p>
          } @else {
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
                <span id="admin-username-error" class="field-error">{{
                  adminErrors().username
                }}</span>
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
                  matButton="outlined"
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
                <span id="admin-confirm-error" class="field-error">{{
                  adminErrors().confirm
                }}</span>
              }
            </div>
          }
          <div class="workbench-actions">
            <button matButton="outlined" type="button" [disabled]="busy()" (click)="previousStep()">
              Retour
            </button>
            <button matButton="filled" type="button" [disabled]="busy()" (click)="nextStep()">
              Continuer
            </button>
          </div>
        </section>
        }

        @if (currentStep().id === 'central') {
          <section class="workbench-card" aria-labelledby="worker-setup-heading">
            <div class="section-heading">
              <div>
                <h2 id="worker-setup-heading">Relier ce worker au central</h2>
                <p>
                  Indiquez l’adresse du central et un compte administrateur de ce central. Le mot de
                  passe autorise la liaison une seule fois, puis il est oublié.
                </p>
              </div>
            </div>
            <div class="form-group">
              <label for="central-url-input" class="field-label">Adresse du central</label>
              <input
                id="central-url-input"
                [disabled]="busy()"
                type="url"
                class="text-input"
                placeholder="http://192.168.1.50:5080"
                [ngModel]="centralUrl()"
                (ngModelChange)="centralUrl.set($event)"
                autocomplete="off"
              />
              <span class="field-hint">
                Adresse utilisée dans le navigateur, avec le port. Depuis un conteneur sur la même
                machine, 127.0.0.1 et l’adresse IP du PC sont rejoints par le réseau Docker.
              </span>
            </div>
            <div class="form-group">
              <label for="central-username-input" class="field-label">Compte administrateur</label>
              <input
                id="central-username-input"
                [disabled]="busy()"
                class="text-input"
                [ngModel]="centralUsername()"
                (ngModelChange)="centralUsername.set($event)"
                autocomplete="username"
              />
            </div>
            <div class="form-group">
              <label for="central-password-input" class="field-label">Mot de passe</label>
              <input
                id="central-password-input"
                [disabled]="busy()"
                type="password"
                class="text-input"
                [ngModel]="centralPassword()"
                (ngModelChange)="centralPassword.set($event)"
                autocomplete="current-password"
              />
            </div>
            <div class="workbench-actions">
              <button matButton="outlined" type="button" [disabled]="busy()" (click)="previousStep()">
                Retour
              </button>
              <button
                matButton="filled"
                type="button"
                [disabled]="
                  busy() ||
                  !centralUrl().trim() ||
                  !centralUsername().trim() ||
                  !centralPassword()
                "
                (click)="linkCentral()"
              >
                Relier ce worker
              </button>
            </div>
          </section>
        }

        @if (currentStep().id === 'ready') {
        <section class="workbench-card" aria-live="polite">
          <div class="contextual-summary">
            <div class="section-heading">
              <div>
                <h3>Prêt à ouvrir le central</h3>
                <p>
                  <strong>{{ effectiveNodeName() }}</strong> sert l’interface et encode sur cette
                  machine. Les autres workers se rattachent depuis leur assistant, avec un compte
                  administrateur de ce central.
                </p>
              </div>
            </div>
            <div class="workbench-actions">
              <button matButton="outlined" type="button" [disabled]="busy()" (click)="previousStep()">
                Retour
              </button>
              <button
                matButton="filled"
                type="button"
                [disabled]="busy()"
                (click)="submitStandalone()"
              >
                Accéder au tableau de bord
                <app-icon name="forward" matButtonIcon iconPositionEnd />
              </button>
            </div>
          </div>
        </section>
        }
      </div>
    </app-access-template>
  `,
  styles: `
    :host {
      display: block;
      min-height: 100%;
    }
    .network-addresses {
      display: grid;
      gap: var(--space-2);
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .setup-error {
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 12px 16px;
      border-radius: var(--radius-md);
      background: var(--danger-soft);
      color: var(--danger);
      font-size: var(--font-body);
    }
    .setup-progress {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 0.4em;
      margin: 0;
      color: var(--text-muted);
      font-size: var(--font-label);
      font-weight: 650;
    }
    .setup-progress .is-current {
      color: var(--text-primary);
    }
    .setup-progress .is-done {
      color: var(--text-secondary);
    }
    .machine-facts {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: var(--space-4) var(--space-6);
      margin: 0;
      padding: var(--space-4);
      border-radius: var(--radius-lg);
      background: var(--surface-container-low);
    }
    .machine-facts > div {
      display: grid;
      gap: 2px;
      min-width: 0;
    }
    .fact-label,
    .suggestions-label,
    .code-label {
      font-size: var(--font-label);
      font-weight: 650;
      color: var(--text-muted);
    }
    .fact-value,
    .network-addresses {
      margin: 0;
      font-size: var(--font-body);
      font-weight: 650;
      color: var(--text-primary);
    }
    .fact-value--mono,
    .network-addresses,
    .hero-digits {
      font-family: ui-monospace, monospace;
      font-weight: 500;
      word-break: break-all;
    }
    .fact-hint,
    .field-hint,
    .code-helper,
    .section-heading p {
      margin: 0;
      font-size: var(--font-body);
      color: var(--text-secondary);
      line-height: 1.5;
    }
    .fact-hint,
    .field-hint,
    .code-helper {
      font-size: var(--font-label);
    }
    .setup-workbench,
    .workbench-card,
    .form-group,
    .contextual-summary {
      display: grid;
      gap: var(--space-4);
    }
    .form-group {
      gap: 8px;
    }
    .section-heading {
      display: grid;
      gap: 4px;
    }
    .section-heading h2,
    .section-heading h3 {
      margin: 0;
      font-size: var(--font-title-md);
      font-weight: 650;
    }
    .field-label {
      font-size: 13px;
      font-weight: 650;
    }
    .text-input {
      min-height: 44px;
      padding: 10px 14px;
      border-radius: var(--radius-md);
      border: 1px solid var(--border-strong);
      background: var(--surface-container-lowest);
      color: var(--text-primary);
      font-size: 16px;
      outline: none;
    }
    .text-input:focus {
      border-color: var(--accent);
      box-shadow: inset 0 0 0 1px var(--accent);
    }
    .text-input.invalid {
      border-color: var(--danger);
      box-shadow: inset 0 0 0 1px var(--danger);
    }
    .field-error {
      margin: 0;
      font-size: var(--font-label);
      color: var(--danger);
    }
    .Quick-suggestions {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: 6px;
    }
    .suggestion-chip {
      min-height: 32px;
      padding: 4px 12px;
      border: 0;
      border-radius: var(--radius-pill);
      background: var(--surface-container-high);
      color: var(--text-secondary);
      font-size: var(--font-label);
      font-weight: 650;
      cursor: pointer;
    }
    .suggestion-chip:hover,
    .suggestion-chip.active {
      background: var(--surface-container-highest);
      color: var(--text-primary);
    }
    .workbench-actions,
    .input-with-button {
      display: flex;
      justify-content: flex-end;
      gap: var(--space-2);
    }
    .input-with-button .text-input {
      flex: 1;
    }
    .workbench-actions button {
      height: auto;
      min-height: var(--mat-button-filled-container-height, 40px);
      padding-block: var(--space-2);
    }
    @media (max-width: 700px) {
      .machine-facts {
        grid-template-columns: 1fr;
      }
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
  protected readonly shell = inject(ShellConfig);
  protected readonly localController = inject(LocalNodeController);
  protected readonly setupSheet = signal<SetupNodeSheet | null>(null);

  protected readonly busy = signal(false);
  protected readonly errorMessage = signal('');
  protected readonly stepIndex = signal(0);
  protected readonly centralUrl = signal('');
  protected readonly centralUsername = signal('admin');
  protected readonly centralPassword = signal('');
  protected readonly workerLink = signal<WorkerLinkStatus | null>(null);
  protected readonly steps = computed(() =>
    this.shell.worker()
      ? [
          { id: 'name', label: 'Nom' },
          { id: 'central', label: 'Central' },
        ]
      : [
          { id: 'name', label: 'Nom' },
          { id: 'admin', label: 'Administrateur' },
          { id: 'ready', label: 'Ouverture' },
        ],
  );
  protected readonly currentStep = computed(
    () => this.steps()[Math.min(this.stepIndex(), this.steps().length - 1)],
  );

  protected readonly nodeName = signal('');
  protected readonly adminUsername = signal('admin');
  protected readonly adminPassword = signal('');
  protected readonly adminConfirm = signal('');
  protected readonly passwordVisible = signal(false);
  protected readonly adminSubmitted = signal(false);
  protected readonly accountCreated = signal(false);
  protected readonly minimumPasswordLength = MINIMUM_PASSWORD_LENGTH;

  constructor() {
    if (!this.http) return;
    if (this.shell.worker()) {
      timer(0, 3_000)
        .pipe(
          exhaustMap(() =>
            this.http!.get<SetupNodeSheet>('/api/v1/worker/link/machine').pipe(
              timeout(8_000),
              catchError(() => of(null)),
            ),
          ),
          takeWhile((sheet) => sheet?.tools == null, true),
          takeUntilDestroyed(this.destroyRef),
        )
        .subscribe((sheet) => {
          if (sheet) this.setupSheet.set(sheet);
        });
      timer(0, 3_000)
        .pipe(
          exhaustMap(() =>
            this.http!.get<WorkerLinkStatus>('/api/v1/worker/link').pipe(
              timeout(8_000),
              catchError(() => of(null)),
            ),
          ),
          takeUntilDestroyed(this.destroyRef),
        )
        .subscribe((link) => {
          if (!link) return;
          this.workerLink.set(link);
          if (link.enrolled) void this.openLogin();
        });
      return;
    }
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
        if (sheet) this.setupSheet.set(sheet);
      });
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

  protected readonly nodeNameSuggestions = computed(() =>
    this.shell.worker()
      ? (['Worker salon', 'Worker GPU', 'NAS Médias'] as const)
      : (['Serveur Salon', 'Central média', 'NAS Médias'] as const),
  );

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
  protected readonly networkAccessUrls = computed(() => {
    const urls = this.setupSheet()?.urls ?? [];
    return urls.length ? urls : [this.document.location.origin];
  });

  protected readonly networkAccessSource = computed(() =>
    this.setupSheet()?.urls?.length ? 'Adresse annoncée' : 'Adresse utilisée par ce navigateur',
  );

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
    const caps = status?.capabilities ?? sheet?.tools;
    if (caps == null) {
      if (sheet && !sheet.agentAvailable) return 'Agent local indisponible';
      return 'Détection en cours…';
    }
    if (caps.length === 0) return 'Aucune (CPU)';
    const ffmpeg = caps.find((tool) => tool.toolId === 'ffmpeg');
    if (!ffmpeg) return 'FFmpeg non détecté';

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

  protected nextStep(): void {
    if (this.currentStep().id === 'admin') {
      this.adminSubmitted.set(true);
      const errors = this.adminErrors();
      if (errors.username || errors.password || errors.confirm) return;
    }
    this.stepIndex.update((index) => Math.min(index + 1, this.steps().length - 1));
  }

  protected previousStep(): void {
    this.errorMessage.set('');
    this.stepIndex.update((index) => Math.max(index - 1, 0));
  }

  private async openLogin(): Promise<void> {
    await this.auth.ensureLoaded(true);
    await this.router.navigate(['/login']);
  }

  protected async linkCentral(): Promise<void> {
    if (!this.shell.worker() || this.busy() || !this.http) return;
    const apiBaseUrl = this.centralUrl().trim();
    const username = this.centralUsername().trim();
    const password = this.centralPassword();
    if (!apiBaseUrl || !username || !password) return;
    this.errorMessage.set('');
    this.busy.set(true);
    try {
      const link = await firstValueFrom(
        this.http.post<WorkerLinkStatus>('/api/v1/worker/link', {
          apiBaseUrl,
          displayName: this.effectiveNodeName(),
          username,
          password,
        }),
      );
      this.centralPassword.set('');
      this.workerLink.set(link);
      if (link.enrolled) await this.openLogin();
    } catch (error) {
      this.errorMessage.set(linkError(error));
    } finally {
      this.centralPassword.set('');
      this.busy.set(false);
    }
  }

  protected async submitStandalone(): Promise<void> {
    await this.completeSetup({
      nodeDisplayName: this.effectiveNodeName(),
      mode: 'standalone',
    });
  }

  private async completeSetup(
    payload: {
      nodeDisplayName: string;
      mode: 'standalone';
      clusterName?: string;
    },
    openApplication = true,
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
      if (openApplication) {
        this.notifications.show('Initialisation terminée avec succès !');
        await this.router.navigate(['/']);
      } else {
        this.localController.refresh();
      }
    } catch (error) {
      this.errorMessage.set(
        AuthService.errorMessage(error, 'L’initialisation a échoué. Veuillez réessayer.'),
      );
    } finally {
      this.busy.set(false);
    }
  }
}
