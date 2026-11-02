import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatMenuModule } from '@angular/material/menu';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import {
  ResourceSheetNotice,
  ResourceSheetSection,
} from '../../shared/ui/resource-sheet.component';
import { ResourceCardAction } from '../../shared/ui/resource-card.component';
import { StatusBadgeComponent, StatusTone } from '../../shared/ui/status-badge.component';

export interface NodeOverviewLibrary {
  readonly id: string;
  readonly name: string;
  readonly path: string;
  readonly stateLabel: string;
  readonly stateTone: StatusTone;
  readonly modeLabel: string;
  readonly accessLabel: string;
}

export interface NodeOverviewRoleSwitch {
  readonly role: 'server' | 'worker';
  readonly label: string;
  readonly checked: boolean;
  readonly pending?: boolean;
  readonly disabled?: boolean;
}

export interface NodeOverviewTaskCapacity {
  readonly accepted: number | null;
  readonly remaining: number | null;
  readonly maximum: number | null;
  readonly note: string;
}

export type NodeOverviewPlatform = 'docker' | 'linux' | 'windows' | 'generic';

export interface NodeOverviewHardware {
  readonly cpu: string;
  readonly memory: string;
  readonly gpu: string;
  readonly platform: NodeOverviewPlatform;
  readonly platformLabel: string;
}

@Component({
  selector: 'app-node-overview-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatButtonModule,
    MatCheckboxModule,
    MatMenuModule,
    MatSlideToggleModule,
    StatusBadgeComponent,
  ],
  template: `
    <article
      class="node-overview-card"
      [class.is-active]="active()"
      [class.is-selected]="selected()"
      [class.is-master]="isMaster()"
      [attr.aria-label]="ariaLabel()"
    >
      @for (notice of notices(); track notice.title) {
        <div class="node-overview-notice-banner" [attr.data-tone]="notice.tone" role="status">
          <strong>{{ notice.title }}</strong>
          <span>{{ notice.description }}</span>
        </div>
      }
      <header
        class="node-overview-head"
        [class.node-overview-head_local]="layout() === 'local'"
        [class.node-overview-head_with-summary]="hardware() !== null"
        [class.has-select]="selectable()"
      >
        @if (selectable()) {
          <mat-checkbox
            class="node-overview-select"
            [checked]="selected()"
            [attr.aria-label]="'Sélectionner ' + title()"
            (change)="selectedChange.emit($event.checked)"
          />
        }
        <div class="node-overview-identity-block">
          <span
            class="node-overview-symbol"
            role="img"
            [attr.aria-label]="hardware()?.platformLabel ?? 'Plateforme non déclarée'"
            [attr.title]="hardware()?.platformLabel ?? 'Plateforme non déclarée'"
          >
            @switch (hardware()?.platform) {
              @case ('docker') {
                <svg viewBox="0 0 24 24" fill="none" aria-hidden="true">
                  <path
                    d="M2.5 13.1h18.3c-.5 4.2-3.1 6.6-7.2 6.6H9.3c-3.8 0-6.1-2.1-6.8-6.6Z"
                    fill="currentColor"
                  />
                  <path
                    d="M5.2 10.1h2.5v2.4H5.2zm3.1 0h2.5v2.4H8.3zm3.1 0h2.5v2.4h-2.5zm3.1 0H17v2.4h-2.5zM8.3 7.2h2.5v2.4H8.3zm3.1 0h2.5v2.4h-2.5z"
                    fill="currentColor"
                  />
                  <path
                    d="M20.8 11.2c.8-.2 1.3-.8 1.5-1.6.8.7 1 1.7.7 2.7-.6.2-1.3.1-2-.1"
                    stroke="currentColor"
                    stroke-width="1.4"
                    stroke-linecap="round"
                  />
                </svg>
              }
              @case ('linux') {
                <svg viewBox="0 0 24 24" fill="none" aria-hidden="true">
                  <ellipse cx="12" cy="12.8" rx="6.4" ry="8" fill="currentColor" />
                  <ellipse cx="12" cy="14" rx="3.6" ry="5.2" fill="var(--accent-strong)" />
                  <circle cx="9.8" cy="9.5" r=".7" fill="var(--surface-container-high)" />
                  <circle cx="14.2" cy="9.5" r=".7" fill="var(--surface-container-high)" />
                  <path
                    d="m10.4 10.8 1.6 1 1.6-1M8.1 19.2l-1.2 1.2m9-1.2 1.2 1.2"
                    stroke="currentColor"
                    stroke-width="1.5"
                    stroke-linecap="round"
                  />
                </svg>
              }
              @case ('windows') {
                <svg viewBox="0 0 24 24" aria-hidden="true">
                  <path
                    fill="currentColor"
                    d="M2 4.1 10.7 2.9v8H2zM12 2.7l10-1.4v9.6H12zM2 12.4h8.7v8L2 19.2zM12 12.4h10v9.7l-10-1.4z"
                  />
                </svg>
              }
              @default {
                <svg
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="1.8"
                  aria-hidden="true"
                >
                  <rect x="3" y="4" width="18" height="6" rx="2" />
                  <rect x="3" y="14" width="18" height="6" rx="2" />
                  <path d="M7 7h.01M7 17h.01M11 7h7M11 17h7" />
                </svg>
              }
            }
          </span>
          <div class="node-overview-identity">
            @if (layout() === 'local') {
              <span class="node-overview-eyebrow">CE NŒUD</span>
            }
            <div class="node-overview-title-line">
              @if (openable()) {
                <button class="node-overview-title" type="button" (click)="open.emit()">
                  {{ title() }}
                </button>
              } @else {
                <h2 class="node-overview-title-static">{{ title() }}</h2>
              }
            </div>
            @if (subtitle()) {
              <p class="node-overview-subtitle">{{ subtitle() }}</p>
            }
            <div class="node-overview-badges">
              @if (isMaster()) {
                <span
                  class="node-overview-master-badge"
                  title="Nœud Maître (Coordinateur actif du cluster)"
                >
                  <svg
                    class="node-overview-master-icon"
                    viewBox="0 0 24 24"
                    fill="currentColor"
                    aria-hidden="true"
                  >
                    <path
                      d="M5 16L3 5l5.5 5L12 4l3.5 6L21 5l-2 11H5zm14 3c0 .6-.4 1-1 1H6c-.6 0-1-.4-1-1v-1h14v1z"
                    />
                  </svg>
                  <span>Maître</span>
                </span>
              } @else {
                <span
                  class="node-overview-worker-badge"
                  title="Nœud Worker (Exécution des encodages)"
                >
                  <svg
                    class="node-overview-worker-icon"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2"
                    aria-hidden="true"
                  >
                    <polygon points="13 2 3 14 12 14 11 22 21 10 12 10 13 2" />
                  </svg>
                  <span>Worker</span>
                </span>
              }
              <app-status-badge [label]="statusLabel()" [tone]="statusTone()" />
            </div>

            @if (roleSwitches().length || roles().length) {
              <div class="node-overview-roles-line" role="group" aria-label="Rôles du nœud">
                @if (roleSwitches().length) {
                  @for (control of roleSwitches(); track control.role) {
                    <button
                      type="button"
                      class="node-overview-role-btn"
                      [class.is-active]="control.checked && !control.pending"
                      [class.is-pending]="control.pending"
                      [disabled]="control.disabled ?? false"
                      [attr.aria-label]="
                        (control.checked ? 'Désactiver' : 'Activer') + ' le rôle ' + control.label
                      "
                      (click)="roleToggle.emit({ role: control.role, enabled: !control.checked })"
                    >
                      <span class="node-overview-role-dot"></span>
                      <span class="node-overview-role-name">{{ control.label }}</span>
                      <span class="node-overview-role-status">{{
                        control.pending ? '…' : control.checked ? 'Actif' : 'Inactif'
                      }}</span>
                    </button>
                  }
                } @else {
                  @for (role of roles(); track role) {
                    <span class="node-overview-role-pill">{{ role }}</span>
                  }
                }
              </div>
            }
            @if (roleControlNote()) {
              <p class="node-overview-role-note">{{ roleControlNote() }}</p>
            }
          </div>
        </div>

        <div class="node-overview-head-controls">
          @if (hardware(); as resources) {
            <section class="node-overview-hardware" aria-label="Ressources matérielles">
              <span class="node-overview-control-label">RESSOURCES MATÉRIELLES</span>
              <div class="node-overview-hw-list">
                <div
                  class="node-overview-hw-row node-overview-hw-row_cpu"
                  [attr.title]="resources.cpu"
                >
                  <span class="node-overview-hw-icon" aria-hidden="true">
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8">
                      <rect x="4" y="4" width="16" height="16" rx="2" />
                      <rect x="9" y="9" width="6" height="6" />
                      <path d="M9 1v3M15 1v3M9 20v3M15 20v3M20 9h3M20 14h3M1 9h3M1 14h3" />
                    </svg>
                  </span>
                  <span class="node-overview-hw-tag">CPU</span>
                  <span class="node-overview-hw-value">{{ resources.cpu }}</span>
                </div>
                <div
                  class="node-overview-hw-row node-overview-hw-row_ram"
                  [attr.title]="resources.memory"
                >
                  <span class="node-overview-hw-icon" aria-hidden="true">
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8">
                      <path d="M6 19v2M10 19v2M14 19v2M18 19v2M6 3v2M10 3v2M14 3v2M18 3v2" />
                      <rect x="3" y="5" width="18" height="14" rx="2" />
                    </svg>
                  </span>
                  <span class="node-overview-hw-tag">RAM</span>
                  <span class="node-overview-hw-value">{{ resources.memory }}</span>
                </div>
                <div
                  class="node-overview-hw-row node-overview-hw-row_gpu"
                  [attr.title]="resources.gpu"
                >
                  <span class="node-overview-hw-icon" aria-hidden="true">
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8">
                      <rect x="2" y="6" width="20" height="12" rx="2" />
                      <circle cx="8" cy="12" r="2.5" />
                      <circle cx="16" cy="12" r="2.5" />
                      <path d="M8 9.5v5M16 9.5v5" />
                    </svg>
                  </span>
                  <span class="node-overview-hw-tag">GPU</span>
                  <span class="node-overview-hw-value">{{ resources.gpu }}</span>
                </div>
              </div>
            </section>
          }

          <div class="node-overview-controls-panel">
            @if (admissionEnabled() !== null) {
              <div
                class="node-overview-admission-control"
                [class.is-disabled]="admissionDisabled()"
              >
                <div class="node-overview-admission-header">
                  <span class="node-overview-control-label">ADMISSION</span>
                  <span
                    class="node-overview-control-state"
                    [class.is-suspended]="!admissionEnabled()"
                  >
                    {{ admissionEnabled() ? 'Acceptées' : 'Suspendues' }}
                  </span>
                </div>
                <mat-slide-toggle
                  class="node-overview-admission-toggle"
                  [checked]="admissionEnabled() ?? false"
                  [disabled]="admissionDisabled()"
                  [aria-label]="
                    (admissionEnabled() ? 'Suspendre' : 'Réactiver') +
                    ' les nouvelles tâches sur ' +
                    title()
                  "
                  (change)="admissionChange.emit($event.checked)"
                >
                  Nouvelles tâches
                </mat-slide-toggle>
              </div>
            }
            @if (taskCapacity(); as capacity) {
              <section
                class="node-overview-task-capacity"
                aria-label="Tâches acceptées et places restantes"
              >
                <span class="node-overview-control-label">CAPACITÉ</span>
                <dl class="node-overview-task-capacity-values">
                  <div>
                    <dt>Tâches</dt>
                    <dd>
                      {{ capacity.accepted ?? '0'
                      }}{{ capacity.maximum !== null ? ' / ' + capacity.maximum : '' }}
                    </dd>
                  </div>
                  <div>
                    <dt>Disponibles</dt>
                    <dd>{{ capacity.remaining ?? '—' }}</dd>
                  </div>
                </dl>
              </section>
            }
            @if (meta()) {
              <div class="node-overview-meta-control" [attr.title]="'Dernier signal : ' + meta()">
                <span class="node-overview-meta-dot"></span>
                <span class="node-overview-meta">{{ meta() }}</span>
              </div>
            }
          </div>
        </div>

        @if (actions().length) {
          <button
            mat-icon-button
            type="button"
            class="node-overview-menu-button"
            [matMenuTriggerFor]="menu"
            [attr.aria-label]="'Autres actions pour ' + title()"
          >
            <span aria-hidden="true">···</span>
          </button>
          <mat-menu #menu="matMenu">
            @for (action of actions(); track action.id) {
              <button
                mat-menu-item
                type="button"
                [class.danger]="action.tone === 'danger'"
                (click)="actionSelected.emit(action.id)"
              >
                {{ action.label }}
              </button>
            }
          </mat-menu>
        }
      </header>

      <div class="node-overview-section-grid">
        @for (section of sections(); track section.id) {
          <section
            class="node-overview-section"
            [attr.aria-labelledby]="nodeId() + '-' + section.id"
          >
            <div class="node-overview-section-heading">
              <h3 [id]="nodeId() + '-' + section.id">{{ section.title }}</h3>
              @if (section.caption) {
                <span>{{ section.caption }}</span>
              }
              @if (section.action; as action) {
                <button matButton="text" type="button" (click)="actionSelected.emit(action.id)">
                  {{ action.label }}
                </button>
              }
            </div>
            @if (section.notice) {
              <p class="node-overview-notice">{{ section.notice }}</p>
            }
            @if (section.items?.length) {
              <div class="node-overview-items">
                @for (item of section.items; track item.label) {
                  <div class="node-overview-item">
                    <div class="node-overview-item-main">
                      <strong>{{ item.label }}</strong>
                      @if (item.badges?.length) {
                        <div class="node-overview-chip-row">
                          @for (badge of item.badges ?? []; track badge.label) {
                            <app-status-badge
                              [label]="badge.label"
                              [tone]="badge.tone ?? 'neutral'"
                            />
                          }
                        </div>
                      }
                      @if (item.detail) {
                        <span class="node-overview-detail">{{ item.detail }}</span>
                      }
                    </div>
                    @if (item.action; as action) {
                      <button
                        matButton="outlined"
                        type="button"
                        [disabled]="action.disabled"
                        (click)="actionSelected.emit(action.id)"
                      >
                        {{ action.label }}
                      </button>
                    }
                  </div>
                }
              </div>
            }
            @if (section.metrics?.length || section.fields?.length) {
              <dl class="node-overview-values">
                @for (metric of section.metrics ?? []; track metric.label) {
                  <div class="node-overview-value">
                    <dt>{{ metric.label }}</dt>
                    <dd [class.is-unavailable]="metric.unavailable">{{ metric.value }}</dd>
                    @if (metric.detail) {
                      <span class="node-overview-detail">{{ metric.detail }}</span>
                    }
                  </div>
                }
                @for (field of section.fields ?? []; track field.label) {
                  <div class="node-overview-value">
                    <dt>{{ field.label }}</dt>
                    <dd [class.is-mono]="field.mono">{{ field.value }}</dd>
                    @if (field.detail) {
                      <span class="node-overview-detail">{{ field.detail }}</span>
                    }
                  </div>
                }
              </dl>
            }
          </section>
        }
      </div>

      <section class="node-overview-libraries" aria-label="Bibliothèques et chemins configurés">
        <header class="node-overview-libraries-heading">
          <div>
            <h3>Bibliothèques et chemins configurés</h3>
            <p>Chemins vus depuis ce nœud et mode d’accès associé.</p>
          </div>
          <span class="node-overview-library-count">
            {{ libraries().length }} autorisée{{ libraries().length > 1 ? 's' : '' }}
          </span>
        </header>
        @if (libraryAccessState() === 'loading') {
          <p class="node-overview-empty" role="status">Chargement des accès configurés…</p>
        } @else if (libraryAccessState() === 'error') {
          <p class="node-overview-empty" role="status">
            Les accès aux bibliothèques n’ont pas pu être chargés.
          </p>
        } @else if (libraries().length) {
          <div class="node-overview-library-grid">
            @for (library of libraries(); track library.id) {
              <article class="node-overview-library-card">
                <header class="node-overview-library-head">
                  <span class="node-overview-library-symbol" aria-hidden="true">▱</span>
                  <span class="node-overview-library-name">
                    <strong>{{ library.name }}</strong>
                    <small>Bibliothèque</small>
                  </span>
                  <app-status-badge [label]="library.stateLabel" [tone]="library.stateTone" />
                </header>
                <div class="node-overview-path-block">
                  <span>Chemin configuré sur ce nœud</span>
                  <code>{{ library.path }}</code>
                </div>
                <footer class="node-overview-library-foot">
                  <span class="node-overview-library-mode">{{ library.modeLabel }}</span>
                  <span>{{ library.accessLabel }}</span>
                </footer>
              </article>
            }
          </div>
        } @else {
          <p class="node-overview-empty">Aucune bibliothèque configurée pour ce nœud.</p>
        }
      </section>
    </article>
  `,
  styleUrls: ['./node-overview-card.component.scss', './node-overview-card.header.scss'],
})
export class NodeOverviewCardComponent {
  readonly ariaLabel = input('Informations du nœud');
  readonly title = input.required<string>();
  readonly subtitle = input('');
  readonly isMaster = input(false);
  readonly statusLabel = input.required<string>();
  readonly statusTone = input<StatusTone>('neutral');
  readonly layout = input<'standard' | 'local'>('standard');
  readonly nodeId = input('node-overview');
  readonly roles = input<readonly string[]>([]);
  readonly roleSwitches = input<readonly NodeOverviewRoleSwitch[]>([]);
  readonly roleControlNote = input('');
  readonly admissionEnabled = input<boolean | null>(null);
  readonly admissionDisabled = input(false);
  readonly hardware = input<NodeOverviewHardware | null>(null);
  readonly taskCapacity = input<NodeOverviewTaskCapacity | null>(null);
  readonly meta = input('');
  readonly notices = input<readonly ResourceSheetNotice[]>([]);
  readonly sections = input<readonly ResourceSheetSection[]>([]);
  readonly libraries = input<readonly NodeOverviewLibrary[]>([]);
  readonly libraryAccessState = input<'loading' | 'ready' | 'error'>('ready');
  readonly actions = input<readonly ResourceCardAction[]>([]);
  readonly selectable = input(false);
  readonly selected = input(false);
  readonly active = input(false);
  readonly openable = input(false);

  readonly open = output<void>();
  readonly selectedChange = output<boolean>();
  readonly actionSelected = output<string>();
  readonly roleToggle = output<{ readonly role: 'server' | 'worker'; readonly enabled: boolean }>();
  readonly admissionChange = output<boolean>();
}
