import { JoinUserCollisionsComponent } from '../settings/tabs/join-user-collisions.component';
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

export type SetupOperationMode = 'standalone' | 'createCluster' | 'joinCluster';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-setup-wizard-page',
  providers: [LocalNodeController],
  imports: [
    JoinUserCollisionsComponent,
    IconComponent,
    CommonModule,
    FormsModule,
    MatButtonModule,
    AccessTemplateComponent,
  ],
  template: `
    <app-access-template
      view="setup"
      title="Bienvenue sur Encoding"
      description="Configurez cette machine en un coup d’œil et vérifiez les ressources matérielles détectées localement."
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

      <!-- Colonne gauche : Fiche d'identité live & télémétrie matérielle -->
      <aside access-context class="setup-telemetry" aria-label="Identité et matériel détecté">
        <div class="hardware-bento" role="region" aria-labelledby="hardware-card-title">
          <div class="hardware-bento-header">
            <span class="live-dot" aria-hidden="true"></span>
            <div class="hardware-bento-title-group">
              <span class="hardware-eyebrow">Nœud local détecté</span>
              <h2 id="hardware-card-title" class="hardware-node-name">
                {{ effectiveNodeName() }}
              </h2>
              <span class="hardware-host-meta">{{ hostPlatformLabel() }}</span>
            </div>
          </div>

          <div class="hardware-tiles">
            <div class="hardware-tile hardware-tile--wide">
              <span class="tile-label">Accès au nœud</span>
              @for (url of networkAccessUrls(); track url) {
                <span class="tile-value tile-value--mono">{{ url }}</span>
              }
              <span class="tile-hint">{{ networkAccessSource() }}</span>
            </div>
            <div class="hardware-tile hardware-tile--wide">
              <span class="tile-label">Adresses des interfaces</span>
              @if (setupSheet()?.addresses?.length) {
                <ul class="network-addresses" aria-label="Adresses des interfaces">
                  @for (
                    item of setupSheet()!.addresses;
                    track item.interface + '/' + item.address
                  ) {
                    <li class="tile-value tile-value--mono">
                      {{ item.address }} ({{ item.interface }})
                    </li>
                  }
                </ul>
              } @else {
                <span class="tile-value">{{
                  setupSheet() ? 'Aucune adresse détectée' : 'Détection en cours…'
                }}</span>
              }
            </div>
            <div class="hardware-tile">
              <span class="tile-label">Processeur</span>
              <span class="tile-value">{{ cpuInfo() }}</span>
            </div>
            <div class="hardware-tile">
              <span class="tile-label">Mémoire</span>
              <span class="tile-value">{{ memoryInfo() }}</span>
            </div>
            <div class="hardware-tile hardware-tile--wide">
              <span class="tile-label">Graphique / GPU</span>
              <span class="tile-value">{{ gpuInfo() }}</span>
            </div>
            <div class="hardware-tile hardware-tile--wide hardware-tile--accent">
              <span class="tile-label">Accélération GPU</span>
              <span class="tile-value">{{ gpuAccelerationInfo() }}</span>
            </div>
          </div>
        </div>

        <p class="telemetry-footnote">
          <svg
            viewBox="0 0 24 24"
            width="16"
            height="16"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            aria-hidden="true"
          >
            <circle cx="12" cy="12" r="10" />
            <line x1="12" y1="16" x2="12" y2="12" />
            <line x1="12" y1="8" x2="12.01" y2="8" />
          </svg>
          <span>
            Vous pourrez renommer ce nœud ou basculer entre autonome et cluster à chaud depuis la
            page <strong>Nœuds</strong>.
          </span>
        </p>
      </aside>

      <!-- Colonne droite : Atelier de configuration directe -->
      <div access-content class="setup-workbench">
        <!-- Section 1 : Compte administrateur -->
        <section class="workbench-card" aria-labelledby="step-admin-heading">
          <div class="section-heading">
            <span class="step-pill" aria-hidden="true">1</span>
            <div>
              <h2 id="step-admin-heading">
                {{ accountCreated() ? 'Administrateur créé' : 'Créer l’administrateur' }}
              </h2>
              <p>Ce compte protège l’accès au cluster. Vous pourrez en ajouter d’autres ensuite.</p>
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
        </section>

        <!-- Section 2 : Identité -->
        <section class="workbench-card" aria-labelledby="step-identity-heading">
          <div class="section-heading">
            <span class="step-pill" aria-hidden="true">2</span>
            <div>
              <h2 id="step-identity-heading">Identifier cette machine</h2>
              <p>Donnez un nom convivial pour repérer facilement ce nœud sur votre réseau.</p>
            </div>
          </div>

          <div class="form-group">
            <label for="node-name-input" class="field-label">Nom de ce nœud</label>
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
            <div class="Quick-suggestions" aria-label="Suggestions de noms">
              <span class="suggestions-label">Suggestions :</span>
              @for (suggestion of nodeNameSuggestions; track suggestion) {
                <button
                  type="button"
                  class="suggestion-chip"
                  [disabled]="busy() || accountCreated()"
                  [class.active]="nodeName() === suggestion"
                  (click)="nodeName.set(suggestion)"
                >
                  {{ suggestion }}
                </button>
              }
            </div>
          </div>
        </section>

        <!-- Section 3 : Choix de l'architecture -->
        <section class="workbench-card" aria-labelledby="step-mode-heading">
          <div class="section-heading">
            <span class="step-pill" aria-hidden="true">3</span>
            <div>
              <h2 id="step-mode-heading">Comment souhaitez-vous utiliser cette machine ?</h2>
              <p>Sélectionnez l’organisation initiale de votre installation.</p>
            </div>
          </div>

          <div class="mode-grid" role="radiogroup" aria-labelledby="step-mode-heading">
            <!-- Mode 1 : Autonome -->
            <button
              type="button"
              class="mode-card"
              [disabled]="busy() || accountCreated()"
              [class.selected]="selectedMode() === 'standalone'"
              (click)="selectMode('standalone')"
              role="radio"
              [attr.aria-checked]="selectedMode() === 'standalone'"
            >
              <div class="mode-card-top">
                <span class="mode-icon" aria-hidden="true">
                  <svg
                    viewBox="0 0 24 24"
                    width="20"
                    height="20"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2"
                  >
                    <rect x="2" y="3" width="20" height="14" rx="2" ry="2" />
                    <line x1="8" y1="21" x2="16" y2="21" />
                    <line x1="12" y1="17" x2="12" y2="21" />
                  </svg>
                </span>
                <span class="mode-badge primary">Recommandé</span>
              </div>
              <strong class="mode-title">Machine autonome</strong>
              <p class="mode-description">
                Serveur et worker réunis sur cette machine. Idéal pour démarrer immédiatement sans
                cluster.
              </p>
            </button>

            <!-- Mode 2 : Créer un cluster -->
            <button
              type="button"
              class="mode-card"
              [disabled]="busy() || accountCreated()"
              [class.selected]="selectedMode() === 'createCluster'"
              (click)="selectMode('createCluster')"
              role="radio"
              [attr.aria-checked]="selectedMode() === 'createCluster'"
            >
              <div class="mode-card-top">
                <span class="mode-icon" aria-hidden="true">
                  <svg
                    viewBox="0 0 24 24"
                    width="20"
                    height="20"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2"
                  >
                    <circle cx="18" cy="5" r="3" />
                    <circle cx="6" cy="12" r="3" />
                    <circle cx="18" cy="19" r="3" />
                    <line x1="8.59" y1="13.51" x2="15.42" y2="17.49" />
                    <line x1="15.41" y1="6.51" x2="8.59" y2="10.49" />
                  </svg>
                </span>
                <span class="mode-badge neutral">Multi-machines</span>
              </div>
              <strong class="mode-title">Créer un cluster</strong>
              <p class="mode-description">
                Initialise un nouveau maillage prêt à accueillir d’autres serveurs ou workers.
              </p>
            </button>

            <!-- Mode 3 : Rejoindre un cluster -->
            <button
              type="button"
              class="mode-card"
              [disabled]="busy() || accountCreated()"
              [class.selected]="selectedMode() === 'joinCluster'"
              (click)="selectMode('joinCluster')"
              role="radio"
              [attr.aria-checked]="selectedMode() === 'joinCluster'"
            >
              <div class="mode-card-top">
                <span class="mode-icon" aria-hidden="true">
                  <svg
                    viewBox="0 0 24 24"
                    width="20"
                    height="20"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2"
                  >
                    <path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71" />
                    <path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71" />
                  </svg>
                </span>
                <span class="mode-badge accent">Worker / Pair</span>
              </div>
              <strong class="mode-title">Rejoindre un cluster</strong>
              <p class="mode-description">
                Rattache cette machine à un cluster Encoding déjà actif sur votre réseau.
              </p>
            </button>
          </div>
        </section>

        <!-- Section 4 : Finalisation contextuelle selon le mode choisi -->
        <section class="workbench-card workbench-card--Contextual" aria-live="polite">
          @if (selectedMode() === 'standalone') {
            <div class="contextual-summary">
              <div class="contextual-summary-head">
                <span class="status-check" aria-hidden="true">✓</span>
                <div>
                  <h3>Prêt à démarrer en mode autonome</h3>
                  <p>
                    <strong>{{ effectiveNodeName() }}</strong> assurera l’interface serveur et
                    l’encodage local. Vous vous connecterez avec le compte administrateur créé
                    ci-dessus.
                  </p>
                </div>
              </div>
              <div class="workbench-actions">
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
          }

          @if (selectedMode() === 'createCluster') {
            <div class="section-heading">
              <span class="step-pill" aria-hidden="true">4</span>
              <div>
                <h3>Nommer votre nouveau cluster</h3>
                <p>Ce nom sera affiché lors de l’ajout d’autres serveurs ou workers.</p>
              </div>
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
              <span class="field-hint">
                Premier coordinateur et worker : <strong>{{ effectiveNodeName() }}</strong>
              </span>
            </div>

            <div class="workbench-actions">
              <button
                matButton="filled"
                type="button"
                [disabled]="busy()"
                (click)="submitCreateCluster()"
              >
                Créer le cluster et continuer <app-icon name="forward" />
              </button>
            </div>
          }

          @if (selectedMode() === 'joinCluster') {
            @if (!accountCreated()) {
              <p>
                Créez le compte administrateur de cette machine pour rechercher un cluster et
                envoyer une demande. La jonction devra ensuite être approuvée sur le cluster
                d’accueil.
              </p>
              <div class="workbench-actions">
                <button
                  matButton="filled"
                  type="button"
                  [disabled]="busy()"
                  (click)="prepareClusterJoin()"
                >
                  Créer le compte et continuer
                </button>
              </div>
            } @else {
              <p role="status">
                Compte administrateur local créé. Vous pouvez rejoindre un cluster.
              </p>
            }
            <div class="section-heading">
              <span class="step-pill" aria-hidden="true">4</span>
              <div>
                <h3>Connexion au cluster d’accueil</h3>
                <p>
                  Sélectionnez un cluster découvert sur le réseau local ou indiquez son adresse URL.
                </p>
              </div>
            </div>

            @if (availableClusters().length > 0) {
              <div class="discovered-clusters">
                <span class="list-label">Clusters détectés automatiquement (mDNS) :</span>
                <div class="cluster-cards-list">
                  @for (cluster of availableClusters(); track cluster.id) {
                    <div class="cluster-card">
                      <div class="cluster-info">
                        <strong>{{ cluster.name }}</strong>
                        <span class="cluster-address">{{ cluster.apiBaseUrl }}</span>
                      </div>
                      <button
                        matButton="outlined"
                        type="button"
                        [disabled]="
                          !accountCreated() || busy() || localController.pending() || joinApproved()
                        "
                        (click)="selectClusterAddress(cluster.apiBaseUrl)"
                      >
                        Sélectionner
                      </button>
                    </div>
                  }
                </div>
              </div>
            }

            <div class="form-group">
              <label for="cluster-address-input" class="field-label">
                Adresse du cluster d’accueil
              </label>
              <div class="input-with-button">
                <input
                  id="cluster-address-input"
                  [disabled]="
                    !accountCreated() || busy() || localController.pending() || joinApproved()
                  "
                  type="url"
                  class="text-input"
                  placeholder="http://192.168.1.50:8090"
                  [ngModel]="clusterAddress()"
                  (ngModelChange)="clusterAddress.set($event)"
                />
                <button
                  matButton="outlined"
                  type="button"
                  [disabled]="
                    !accountCreated() ||
                    !clusterAddress() ||
                    busy() ||
                    localController.pending() ||
                    joinApproved()
                  "
                  (click)="verifyClusterAddress()"
                >
                  Vérifier
                </button>
              </div>
              <span class="field-hint">
                Adresse complète avec port (ex. http://192.168.1.50:8090).
              </span>
            </div>

            @if (localController.target(); as target) {
              <div class="target-confirmed-box">
                <div class="target-badge">Cluster joignable</div>
                <h4>{{ target.name }}</h4>
                <p class="target-url">{{ target.apiBaseUrl }} ({{ target.id }})</p>

                @if (joinApproved()) {
                  <p role="status">Ce nœud a rejoint {{ target.name }}.</p>
                } @else if (pairingCode(); as code) {
                  <div class="hero-code-box">
                    <span class="code-label">Code de confirmation à comparer</span>
                    <div class="hero-digits">{{ code }}</div>
                    <p class="code-helper">
                      Un administrateur du cluster <strong>{{ target.name }}</strong> doit valider
                      ce code depuis sa page Nœuds.
                    </p>
                  </div>
                } @else if (localController.pending()) {
                  <p role="status">
                    Demande envoyée. En attente de confirmation du cluster d’accueil.
                  </p>
                } @else {
                  <button
                    matButton="outlined"
                    type="button"
                    [disabled]="busy() || !localController.canJoin()"
                    (click)="requestClusterJoin()"
                  >
                    Envoyer la demande de rattachement
                  </button>
                }
              </div>
            }

            @if (accountCreated()) {
              <app-join-user-collisions />
            }
            <div class="workbench-actions">
              <button
                matButton="filled"
                type="button"
                [disabled]="
                  busy() || !accountCreated() || (!localController.pending() && !joinApproved())
                "
                (click)="submitJoinFinish()"
              >
                {{ joinApproved() ? 'Ouvrir l’application' : 'Suivre la demande dans Nœuds' }}
                <app-icon name="forward" matButtonIcon iconPositionEnd />
              </button>
            </div>
          }
        </section>
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
    .setup-telemetry,
    .workbench-card {
      display: grid;
      gap: var(--space-4);
      padding: var(--space-5);
      border-radius: var(--radius-2xl);
      background: var(--surface-container);
      box-shadow: var(--elevation-2);
    }
    .setup-telemetry {
      position: sticky;
      top: 76px;
    }
    .setup-workbench,
    .hardware-bento {
      display: grid;
      gap: var(--space-3);
    }
    .mode-badge {
      padding: 2px 8px;
      border-radius: var(--radius-pill);
      font-size: 12px;
      font-weight: 700;
      background: var(--accent-soft);
      color: var(--accent);
    }
    .section-heading p,
    .contextual-summary-head p {
      margin: 0;
      font-size: var(--font-body);
      color: var(--text-secondary);
      line-height: 1.5;
    }
    .hardware-bento {
      padding: var(--space-4);
      border-radius: var(--radius-lg);
      background: var(--surface-container-low);
    }
    .hardware-bento-header,
    .section-heading,
    .contextual-summary-head {
      display: flex;
      align-items: flex-start;
      gap: var(--space-3);
    }
    .live-dot {
      width: 10px;
      height: 10px;
      margin-top: 5px;
      border-radius: 50%;
      background: var(--success);
      box-shadow: 0 0 0 4px color-mix(in srgb, var(--success) 20%, transparent);
      flex-shrink: 0;
    }
    .hardware-bento-title-group {
      display: grid;
      gap: 2px;
      min-width: 0;
    }
    .hardware-eyebrow {
      font-size: 12px;
      font-weight: 700;
      text-transform: uppercase;
      color: var(--text-muted);
    }
    .hardware-node-name {
      margin: 0;
      font-size: var(--font-title-md);
      font-weight: 800;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .hardware-host-meta,
    .field-hint,
    .cluster-address,
    .target-url,
    .code-helper {
      font-size: 12px;
      color: var(--text-secondary);
      margin: 0;
    }
    .hardware-tiles {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: var(--space-2);
    }
    .hardware-tile {
      display: grid;
      gap: 3px;
      padding: 10px 12px;
      border-radius: var(--radius-md);
      background: var(--surface-container-high);
    }
    .hardware-tile--wide {
      grid-column: 1 / -1;
    }
    .hardware-tile--accent {
      background: color-mix(in srgb, var(--accent-soft) 55%, var(--surface-container-high));
    }
    .tile-label,
    .suggestions-label {
      font-size: 12px;
      font-weight: 600;
      color: var(--text-muted);
    }
    .tile-value {
      font-size: 13px;
      font-weight: 700;
    }
    .tile-value--mono {
      font-family: ui-monospace, monospace;
      word-break: break-all;
    }
    .tile-hint {
      font-size: 12px;
      color: var(--text-secondary);
      word-break: break-all;
    }
    .telemetry-footnote {
      margin: 0;
      display: flex;
      gap: 8px;
      font-size: 12px;
      line-height: 1.45;
      color: var(--text-secondary);
    }
    .workbench-card--Contextual {
      background: color-mix(in srgb, var(--surface-container) 85%, var(--accent-soft));
    }
    .step-pill,
    .status-check {
      width: 28px;
      height: 28px;
      border-radius: 50%;
      display: grid;
      place-items: center;
      background: var(--accent-soft);
      color: var(--accent);
      font-size: 13px;
      font-weight: 800;
      flex-shrink: 0;
    }
    .status-check {
      background: var(--success-soft);
      color: var(--success);
    }
    .section-heading h2,
    .section-heading h3,
    .contextual-summary-head h3,
    .target-confirmed-box h4 {
      margin: 0 0 4px;
      font-size: var(--font-title-md);
      font-weight: 750;
    }
    .form-group,
    .discovered-clusters,
    .cluster-cards-list {
      display: grid;
      gap: 8px;
    }
    .field-label,
    .list-label {
      font-size: 13px;
      font-weight: 700;
    }
    .text-input {
      min-height: 44px;
      padding: 10px 14px;
      border-radius: 12px;
      border: 1px solid var(--border-strong);
      background: transparent;
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
      font-size: 12px;
      color: var(--danger);
    }
    .Quick-suggestions {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: 6px;
    }
    .suggestion-chip {
      padding: 4px 10px;
      border-radius: var(--radius-pill);
      border: 1px solid var(--border-subtle);
      background: var(--surface-container);
      color: var(--text-secondary);
      font-size: 12px;
      font-weight: 600;
      cursor: pointer;
    }
    .suggestion-chip:hover,
    .suggestion-chip.active {
      background: var(--accent-soft);
      color: var(--accent);
    }
    .mode-grid {
      display: grid;
      grid-template-columns: repeat(3, minmax(0, 1fr));
      gap: var(--space-3);
    }
    .mode-card {
      display: flex;
      flex-direction: column;
      align-items: flex-start;
      gap: 10px;
      text-align: left;
      padding: var(--space-4);
      border-radius: var(--radius-lg);
      background: var(--surface-container-low);
      border: 2px solid transparent;
      color: inherit;
      cursor: pointer;
    }
    .mode-card:focus-visible {
      outline: 3px solid var(--tertiary);
      outline-offset: 2px;
    }
    .mode-card.selected {
      background: var(--accent-soft);
      border-color: var(--accent);
    }
    .mode-card-top,
    .cluster-card {
      width: 100%;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-3);
    }
    .contextual-summary {
      display: grid;
      gap: var(--space-3);
    }
    .mode-icon {
      width: 36px;
      height: 36px;
      border-radius: 10px;
      display: grid;
      place-items: center;
      background: var(--surface-container-high);
      color: var(--accent);
    }
    .mode-badge.primary {
      background: var(--success-soft);
      color: var(--success);
    }
    .mode-badge.neutral {
      background: var(--surface-container-high);
      color: var(--text-secondary);
    }
    .mode-title {
      font-size: var(--font-body);
      font-weight: 750;
    }
    .mode-description {
      margin: 0;
      font-size: 12px;
      color: var(--text-secondary);
      line-height: 1.45;
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
    .cluster-card,
    .target-confirmed-box {
      padding: var(--space-3) var(--space-4);
      background: var(--surface-container-low);
      border-radius: var(--radius-md);
    }
    .target-confirmed-box {
      display: grid;
      gap: 8px;
      outline: 1px solid var(--accent);
    }
    .target-badge,
    .code-label {
      font-size: 12px;
      text-transform: uppercase;
      font-weight: 700;
      color: var(--success);
    }
    .hero-code-box {
      text-align: center;
      padding: var(--space-4);
      border-radius: var(--radius-lg);
      background: var(--accent-soft);
    }
    .hero-digits {
      font-size: 32px;
      font-weight: 800;
      letter-spacing: 0.14em;
      margin: 8px 0;
      font-family: ui-monospace, monospace;
    }
    @media (max-width: 900px) {
      .mode-grid {
        grid-template-columns: 1fr;
      }
      .setup-telemetry {
        position: static;
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
  protected readonly localController = inject(LocalNodeController);
  protected readonly setupSheet = signal<SetupNodeSheet | null>(null);

  protected readonly currentStep = signal(0);
  protected readonly busy = signal(false);
  protected readonly errorMessage = signal('');

  protected readonly nodeName = signal('');
  protected readonly selectedMode = signal<SetupOperationMode>('standalone');
  protected readonly clusterName = signal('');
  protected readonly clusterAddress = signal('');
  protected readonly adminUsername = signal('admin');
  protected readonly adminPassword = signal('');
  protected readonly adminConfirm = signal('');
  protected readonly passwordVisible = signal(false);
  protected readonly adminSubmitted = signal(false);
  protected readonly accountCreated = signal(false);
  protected readonly joinApproved = computed(() => this.localController.joinIndex() === 3);
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

  protected readonly nodeNameSuggestions = ['Serveur Salon', 'Worker GPU', 'NAS Médias'] as const;

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

  protected readonly availableClusters = computed(() => this.localController.joinableClusters());

  protected readonly pairingCode = computed(
    () =>
      this.localController.pairing()?.verificationCode ||
      this.localController.status()?.verificationCode ||
      null,
  );

  protected selectMode(mode: SetupOperationMode): void {
    if (this.busy() || this.accountCreated()) return;
    this.errorMessage.set('');
    this.selectedMode.set(mode);
  }

  protected goToStep(index: number): void {
    this.errorMessage.set('');
    this.currentStep.set(index);
  }

  protected selectClusterAddress(url: string): void {
    this.clusterAddress.set(url);
    const known = this.availableClusters().find((c) => c.apiBaseUrl === url);
    if (known) {
      this.localController.target.set(known);
    }
    void this.verifyClusterAddress();
  }

  protected async verifyClusterAddress(): Promise<void> {
    if (
      !this.accountCreated() ||
      this.busy() ||
      this.localController.pending() ||
      this.joinApproved()
    )
      return;
    const addr = this.clusterAddress().trim();
    if (!addr) return;
    this.errorMessage.set('');
    this.busy.set(true);
    try {
      await this.localController.verifyAddress({ address: addr });
      if (this.localController.error()) {
        this.errorMessage.set(this.localController.error());
      }
    } catch {
      this.errorMessage.set(
        'La vérification de l’adresse a échoué. Vérifiez que le serveur est allumé.',
      );
    } finally {
      this.busy.set(false);
    }
  }

  protected async requestClusterJoin(): Promise<void> {
    if (!this.accountCreated() || this.busy() || !this.localController.canJoin()) return;
    this.errorMessage.set('');
    this.busy.set(true);
    try {
      await this.localController.sendRequest(this.effectiveNodeName());
      if (this.localController.error()) {
        this.errorMessage.set(this.localController.error());
      }
    } catch {
      this.errorMessage.set('L’envoi de la demande a échoué. Réessayez.');
    } finally {
      this.busy.set(false);
    }
  }

  protected async submitStandalone(): Promise<void> {
    await this.completeSetup({
      nodeDisplayName: this.effectiveNodeName(),
      mode: 'standalone',
    });
  }

  protected async submitCreateCluster(): Promise<void> {
    const name = this.clusterName().trim() || this.defaultClusterName();
    await this.completeSetup({
      nodeDisplayName: this.effectiveNodeName(),
      mode: 'createCluster',
      clusterName: name,
    });
  }

  protected async prepareClusterJoin(): Promise<void> {
    await this.completeSetup(
      {
        nodeDisplayName: this.effectiveNodeName(),
        mode: 'joinCluster',
      },
      false,
    );
  }

  protected async submitJoinFinish(): Promise<void> {
    if (
      this.busy() ||
      !this.accountCreated() ||
      (!this.localController.pending() && !this.joinApproved())
    )
      return;
    await this.router.navigate([this.joinApproved() ? '/' : '/nodes']);
  }

  private async completeSetup(
    payload: {
      nodeDisplayName: string;
      mode: SetupOperationMode;
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
