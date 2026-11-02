import { IconComponent } from '../../../shared/ui/icon.component';
import { ChangeDetectionStrategy, Component, input, model, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import { FlowComponentConfiguration, FlowGraphNode } from '../../../shared/models/flow-component';
import { SimulationResult } from '../../../shared/models/flow-simulation';
import { FlowSimulationPanelComponent } from '../../../shared/ui/flow-simulation-panel.component';

export interface FlowValidationIssue {
  readonly path: string;
  readonly message: string;
}

@Component({
  selector: 'app-flow-simulation-tray',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, MatButtonModule, MatTooltipModule, FlowSimulationPanelComponent],
  template: `
    @if (opened()) {
      <section
        class="simulation-tray"
        [class.simulation-tray_expanded]="opened()"
        aria-label="Volet de simulation et diagnostics"
      >
        <header class="tray-header">
          <div class="tabs-group" role="group" aria-label="Simulation et diagnostic">
            <button
              type="button"
              class="tab-btn"
              [class.tab-btn_active]="activeTab() === 'simulation'"
              (click)="selectTab('simulation')"
              [attr.aria-pressed]="activeTab() === 'simulation'"
            >
              <span class="tab-glyph" aria-hidden="true">▷</span>
              <span class="tab-label">Simulation</span>
              @if (result(); as res) {
                <span class="status-indicator-pill" [attr.data-status]="res.status">
                  {{ res.status }}
                </span>
              }
            </button>

            <button
              type="button"
              class="tab-btn"
              [class.tab-btn_active]="activeTab() === 'diagnostics'"
              (click)="selectTab('diagnostics')"
              [attr.aria-pressed]="activeTab() === 'diagnostics'"
            >
              <span class="tab-glyph" aria-hidden="true">✔</span>
              <span class="tab-label">Vérification du flow</span>
              @if (validationIssues().length > 0) {
                <span class="issues-count-badge" role="status">
                  {{ validationIssues().length }}
                </span>
              } @else if (isValid() === true) {
                <span class="valid-badge" aria-hidden="true">✔</span>
              }
            </button>
          </div>

          <div class="tray-status-hint">
            @if (validating()) {
              <span class="hint-text">Vérification en cours…</span>
            } @else if (validationIssues().length > 0) {
              <span class="hint-text hint-text_warning">
                {{ validationIssues().length }}
                {{ validationIssues().length > 1 ? 'anomalies signalées' : 'anomalie signalée' }}
              </span>
            } @else if (isValid() === true) {
              <span class="hint-text hint-text_success">Flow vérifié</span>
            } @else {
              <span class="hint-text">Simulez le parcours ou lancez une vérification</span>
            }
          </div>

          <div class="header-actions">
            <button
              type="button"
              class="toggle-tray-btn"
              (click)="toggleExpanded()"
              [attr.aria-expanded]="opened()"
              [attr.aria-label]="opened() ? 'Réduire' : 'Déplier'"
              matTooltip="{{ opened() ? 'Réduire le volet' : 'Agrandir le volet' }}"
            >
              <span class="chevron-glyph" aria-hidden="true"
                ><app-icon [name]="opened() ? 'down' : 'up'"
              /></span>
              <span class="toggle-btn-label">{{ opened() ? 'Réduire' : 'Déplier' }}</span>
            </button>
          </div>
        </header>

        @if (opened()) {
          <div class="tray-body">
            @if (activeTab() === 'simulation') {
              <div class="simulation-content">
                <app-flow-simulation-panel
                  [nodes]="nodes()"
                  [profile]="profile()"
                  [result]="result()"
                  [busy]="busy()"
                  [error]="error() ?? ''"
                  (runRequested)="runRequested.emit($event)"
                  (continueRequested)="continueRequested.emit($event)"
                  (fileSelected)="fileSelected.emit($event)"
                  (closed)="openedChange.emit(false)"
                />
              </div>
            } @else {
              <div class="diagnostics-content">
                <div class="diagnostics-toolbar">
                  <span class="diag-summary">
                    @if (validating()) {
                      Vérification du flow…
                    } @else if (isValid() === true) {
                      Le backend confirme la validité de l’ensemble des blocs, sorties et
                      reconnecteurs.
                    } @else if (validationIssues().length > 0) {
                      Certains éléments doivent être corrigés avant de pouvoir publier le flow :
                    } @else {
                      Aucune vérification n’a encore été lancée pour cette version.
                    }
                  </span>

                  <button
                    matButton="tonal"
                    type="button"
                    [disabled]="validating()"
                    (click)="revalidateRequested.emit()"
                  >
                    {{ validating() ? 'Vérification en cours…' : 'Relancer la vérification' }}
                  </button>
                </div>

                @if (validationIssues().length > 0) {
                  <div class="issues-list" role="list">
                    @for (issue of validationIssues(); track issue.path + issue.message) {
                      <div class="issue-item" role="listitem">
                        <span class="issue-icon" aria-hidden="true">⚠️</span>
                        <div class="issue-content">
                          <strong class="issue-path">{{ issue.path || 'Graphe' }}</strong>
                          <span class="issue-message">{{ issue.message }}</span>
                        </div>
                      </div>
                    }
                  </div>
                } @else if (isValid() === true) {
                  <div class="valid-banner">
                    <span class="valid-icon" aria-hidden="true">✔</span>
                    <div>
                      <h4 class="valid-title">Flow prêt à être utilisé</h4>
                      <p class="valid-desc">
                        Toutes les connexions, entrées, sorties et configurations requises sont
                        conformes.
                      </p>
                    </div>
                  </div>
                }
              </div>
            }
          </div>
        }
      </section>
    }
  `,
  styles: `
    :host {
      display: block;
      width: 100%;
    }

    .simulation-tray {
      width: 100%;
      background: var(--surface-container-high);
      border-top: 1px solid var(--border-subtle);
      box-shadow: var(--elevation-2);
      display: flex;
      flex-direction: column;
      transition: max-height 0.25s var(--motion-emphasized);

      &_expanded {
        max-height: min(45vh, 560px);
      }
    }

    .tray-header {
      min-height: 44px;
      padding: 4px 16px;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      background: var(--surface-container);
      border-bottom: 1px solid var(--border-subtle);
    }

    .tabs-group {
      display: flex;
      align-items: center;
      gap: 4px;
    }

    .tab-btn {
      display: inline-flex;
      align-items: center;
      gap: 7px;
      padding: 6px 12px;
      background: transparent;
      border: none;
      border-radius: var(--radius-pill);
      font-size: var(--font-label);
      font-weight: 600;
      color: var(--text-secondary);
      cursor: pointer;
      transition: all 0.15s ease;

      &:hover {
        background: var(--surface-container-highest);
        color: var(--text-primary);
      }

      &_active {
        background: var(--surface-container-highest);
        color: var(--accent);
        box-shadow: 0 1px 3px rgba(0, 0, 0, 0.1);
      }
    }

    .tab-glyph {
      font-size: 13px;
    }

    .status-indicator-pill {
      font-size: var(--font-label);
      padding: 1px 6px;
      border-radius: var(--radius-pill);
      background: var(--surface-container-highest);
      text-transform: capitalize;

      &[data-status='completed'] {
        background: var(--success-soft);
        color: var(--success);
      }

      &[data-status='failed'] {
        background: var(--danger-soft);
        color: var(--danger);
      }

      &[data-status='waiting'] {
        background: var(--warning-soft);
        color: var(--warning);
      }
    }

    .issues-count-badge {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      min-width: 18px;
      height: 18px;
      padding: 0 4px;
      border-radius: 999px;
      background: var(--danger);
      color: var(--on-danger);
      font-size: var(--font-label);
      font-weight: 700;
    }

    .valid-badge {
      color: var(--success);
      font-size: 12px;
      font-weight: 700;
    }

    .tray-status-hint {
      flex: 1;
      text-align: center;
    }

    .hint-text {
      font-size: 12px;
      color: var(--text-muted);

      &_warning {
        color: var(--warning);
        font-weight: 600;
      }

      &_success {
        color: var(--success);
        font-weight: 600;
      }
    }

    .toggle-tray-btn {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 4px 10px;
      border-radius: var(--radius-pill);
      border: 1px solid var(--border-subtle);
      background: var(--surface-container-highest);
      color: var(--text-secondary);
      font-size: var(--font-label);
      font-weight: 600;
      cursor: pointer;

      &:hover {
        background: var(--surface-container-highest);
        color: var(--text-primary);
      }
    }

    .chevron-glyph {
      font-size: var(--font-label);
    }

    .tray-body {
      overflow-y: auto;
      max-height: calc(min(45vh, 560px) - 44px);
      padding: 16px 20px 24px;
      background: var(--surface-container-lowest);
      overscroll-behavior: contain;
    }

    .simulation-content {
      width: min(100%, 1120px);
      margin-inline: auto;
    }

    .diagnostics-content {
      display: flex;
      flex-direction: column;
      gap: 14px;
    }

    .diagnostics-toolbar {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      padding-bottom: 12px;
      border-bottom: 1px solid var(--border-subtle);
    }

    .diag-summary {
      font-size: 13px;
      color: var(--text-secondary);
    }

    .issues-list {
      display: flex;
      flex-direction: column;
      gap: 8px;
    }

    .issue-item {
      display: flex;
      align-items: flex-start;
      gap: 10px;
      padding: 10px 14px;
      border-radius: var(--radius-md);
      background: var(--danger-soft);

      font-size: 12px;
    }

    .issue-icon {
      font-size: 15px;
    }

    .issue-content {
      display: flex;
      flex-direction: column;
      gap: 2px;
    }

    .issue-path {
      font-weight: 700;
      color: var(--danger);
    }

    .issue-message {
      color: var(--text-primary);
    }

    .valid-banner {
      display: flex;
      align-items: center;
      gap: 14px;
      padding: 16px;
      border-radius: var(--radius-lg);
      background: var(--success-soft);
    }

    .valid-icon {
      font-size: 24px;
      color: var(--success);
    }

    .valid-title {
      margin: 0;
      font-size: 14px;
      font-weight: 700;
      color: var(--success);
    }

    .valid-desc {
      margin: 4px 0 0;
      font-size: 12px;
      color: var(--text-secondary);
    }

    @media (max-width: 768px) {
      .toggle-btn-label {
        display: none;
      }
      .tray-status-hint {
        display: none;
      }
      .tray-body {
        padding: 12px;
      }
    }
  `,
})
export class FlowSimulationTrayComponent {
  readonly opened = input(false);
  readonly activeTab = model<'simulation' | 'diagnostics'>('simulation');
  readonly nodes = input.required<readonly FlowGraphNode[]>();
  readonly profile = input.required<FlowComponentConfiguration>();
  readonly result = input<SimulationResult | null>(null);
  readonly busy = input(false);
  readonly error = input<string | null>(null);
  readonly validationIssues = input<readonly FlowValidationIssue[]>([]);
  readonly validating = input(false);
  readonly isValid = input<boolean | null>(null);

  readonly openedChange = output<boolean>();
  readonly runRequested = output<FlowComponentConfiguration>();
  readonly continueRequested = output<FlowComponentConfiguration>();
  readonly fileSelected = output<File>();
  readonly revalidateRequested = output<void>();

  protected selectTab(tab: 'simulation' | 'diagnostics'): void {
    this.activeTab.set(tab);
    if (!this.opened()) {
      this.openedChange.emit(true);
    }
  }

  protected toggleExpanded(): void {
    this.openedChange.emit(!this.opened());
  }
}
