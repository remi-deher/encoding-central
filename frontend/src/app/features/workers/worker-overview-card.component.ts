import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatMenuModule } from '@angular/material/menu';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { IconComponent } from '../../shared/ui/icon.component';
import {
  ResourceSheetNotice,
  ResourceSheetSection,
} from '../../shared/ui/resource-sheet.component';
import { ResourceCardAction } from '../../shared/ui/resource-card.component';
import { StatusBadgeComponent, StatusTone } from '../../shared/ui/status-badge.component';

export interface WorkerOverviewLibrary {
  readonly id: string;
  readonly name: string;
  readonly path: string;
  readonly stateLabel: string;
  readonly stateTone: StatusTone;
  readonly modeLabel: string;
  readonly accessLabel: string;
}

export interface WorkerOverviewTaskCapacity {
  readonly accepted: number | null;
  readonly remaining: number | null;
  readonly maximum: number | null;
  readonly note: string;
}

export type WorkerOverviewPlatform = 'docker' | 'linux' | 'windows' | 'generic';

export interface WorkerOverviewHardware {
  readonly cpu: string;
  readonly memory: string;
  readonly gpu: string;
  readonly platform: WorkerOverviewPlatform;
  readonly platformLabel: string;
}

@Component({
  selector: 'app-worker-overview-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatButtonModule,
    MatCheckboxModule,
    MatMenuModule,
    MatSlideToggleModule,
    MatTooltipModule,
    IconComponent,
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
            <app-icon name="server" />
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
                  title="Worker Maître (Coordinateur actif du central)"
                >
                  <app-icon class="node-overview-master-icon" name="diamond" />
                  <span>Maître</span>
                </span>
              } @else {
                <span
                  class="node-overview-worker-badge"
                  title="Rôle worker : exécution des encodages"
                >
                  <app-icon class="node-overview-worker-icon" name="server" />
                  <span>Worker</span>
                </span>
              }
              <app-status-badge [label]="statusLabel()" [tone]="statusTone()" />
            </div>

            @if (roles().length) {
              <div class="node-overview-roles-line" role="group" aria-label="Rôles du worker">
                @for (role of roles(); track role) {
                  <span class="node-overview-role-pill">{{ role }}</span>
                }
              </div>
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
                    <app-icon name="settings" />
                  </span>
                  <span class="node-overview-hw-tag">CPU</span>
                  <span class="node-overview-hw-value">{{ resources.cpu }}</span>
                </div>
                <div
                  class="node-overview-hw-row node-overview-hw-row_ram"
                  [attr.title]="resources.memory"
                >
                  <span class="node-overview-hw-icon" aria-hidden="true">
                    <app-icon name="server" />
                  </span>
                  <span class="node-overview-hw-tag">RAM</span>
                  <span class="node-overview-hw-value">{{ resources.memory }}</span>
                </div>
                <div
                  class="node-overview-hw-row node-overview-hw-row_gpu"
                  [attr.title]="resources.gpu"
                >
                  <span class="node-overview-hw-icon" aria-hidden="true">
                    <app-icon name="flow" />
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
            matIconButton
            type="button"
            class="node-overview-menu-button"
            [matMenuTriggerFor]="menu"
            [attr.aria-label]="'Autres actions pour ' + title()"
            [matTooltip]="'Autres actions pour ' + title()"
          >
            <app-icon name="more_vert" />
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

      @if (showLibraries()) {
        <section class="node-overview-libraries" aria-label="Bibliothèques et chemins configurés">
          <header class="node-overview-libraries-heading">
            <div>
              <h3>Bibliothèques et chemins configurés</h3>
              <p>Chemins vus depuis ce worker et mode d’accès associé.</p>
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
                    <span class="node-overview-library-symbol" aria-hidden="true">
                      <app-icon name="library" />
                    </span>
                    <span class="node-overview-library-name">
                      <strong>{{ library.name }}</strong>
                      <small>Bibliothèque</small>
                    </span>
                    <app-status-badge [label]="library.stateLabel" [tone]="library.stateTone" />
                  </header>
                  <div class="node-overview-path-block">
                    <span>Chemin configuré sur ce worker</span>
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
            <p class="node-overview-empty">Aucune bibliothèque configurée pour ce worker.</p>
          }
        </section>
      }
    </article>
  `,
  styleUrls: ['./worker-overview-card.component.scss', './worker-overview-card.header.scss'],
})
export class WorkerOverviewCardComponent {
  readonly ariaLabel = input('Informations du worker');
  readonly title = input.required<string>();
  readonly subtitle = input('');
  readonly isMaster = input(false);
  readonly statusLabel = input.required<string>();
  readonly statusTone = input<StatusTone>('neutral');
  readonly layout = input<'standard' | 'local'>('standard');
  readonly nodeId = input('node-overview');
  readonly roles = input<readonly string[]>([]);
  readonly admissionEnabled = input<boolean | null>(null);
  readonly admissionDisabled = input(false);
  readonly hardware = input<WorkerOverviewHardware | null>(null);
  readonly taskCapacity = input<WorkerOverviewTaskCapacity | null>(null);
  readonly meta = input('');
  readonly notices = input<readonly ResourceSheetNotice[]>([]);
  readonly sections = input<readonly ResourceSheetSection[]>([]);
  readonly showLibraries = input(true);
  readonly libraries = input<readonly WorkerOverviewLibrary[]>([]);
  readonly libraryAccessState = input<'loading' | 'ready' | 'error'>('ready');
  readonly actions = input<readonly ResourceCardAction[]>([]);
  readonly selectable = input(false);
  readonly selected = input(false);
  readonly active = input(false);
  readonly openable = input(false);

  readonly open = output<void>();
  readonly selectedChange = output<boolean>();
  readonly actionSelected = output<string>();
  readonly admissionChange = output<boolean>();
}
