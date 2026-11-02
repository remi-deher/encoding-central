import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { StatusBadgeComponent } from '../../../shared/ui/status-badge.component';
import { NodeGroupResponse, NodeResponse, formatNodeLabel } from '../../../shared/models/cluster';
import {
  formatGroupCriteriaBadges,
  matchesGroupCriteria,
} from '../../../shared/models/node-group-rules';

@Component({
  selector: 'app-node-group-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, StatusBadgeComponent],
  template: `
    @if (group(); as g) {
      <div class="group-detail">
        <!-- En-tête / Synthèse -->
        <section class="detail-section">
          <div class="section-title-row">
            <h3>{{ g.name }}</h3>
            <app-status-badge
              [label]="
                g.mode === 'manual'
                  ? 'Manuel (' + memberNodes().length + ' nœuds)'
                  : 'Automatique (' + memberNodes().length + ' matchés)'
              "
              [tone]="g.mode === 'manual' ? 'neutral' : 'accent'"
            />
          </div>
          @if (g.description) {
            <p class="section-desc">{{ g.description }}</p>
          }
        </section>

        <!-- Membres réels -->
        <section class="detail-section">
          <div class="section-header">
            <h4>Membres actuels du cluster ({{ memberNodes().length }})</h4>
            <p>
              {{
                g.mode === 'manual'
                  ? 'Nœuds affectés directement à ce groupe manuel.'
                  : 'Nœuds satisfaisant actuellement tous les critères d’inventaire.'
              }}
            </p>
          </div>

          <div class="members-grid">
            @if (memberNodes().length === 0) {
              <p class="empty-hint">Aucun nœud n’appartient actuellement à ce groupe.</p>
            } @else {
              @for (node of memberNodes(); track node.id) {
                <div class="member-card">
                  <div class="member-header">
                    <span class="member-name">{{ formatNode(node) }}</span>
                    <app-status-badge
                      [label]="node.status === 'online' ? 'En ligne' : 'Hors ligne'"
                      [tone]="node.status === 'online' ? 'success' : 'neutral'"
                    />
                  </div>
                  <div class="member-specs">
                    <span
                      >{{ node.inventory?.operatingSystem || 'OS indéfini' }} ·
                      {{ node.inventory?.architecture || '' }}</span
                    >
                    @if (node.inventory?.processorCount) {
                      <span>· {{ node.inventory?.processorCount }} CPU</span>
                    }
                    @if (node.inventory?.memoryMb) {
                      <span>· {{ Math.round(node.inventory!.memoryMb / 1024) }} Go RAM</span>
                    }
                    @if (node.maxConcurrentTasks) {
                      <span>· max {{ node.maxConcurrentTasks }} tâche(s)</span>
                    }
                  </div>
                  @if (node.capabilities.length) {
                    <div class="member-tools">
                      <span class="tools-label">Outils :</span>
                      @for (cap of node.capabilities; track cap.toolId) {
                        <span class="tool-tag">{{ cap.toolId }}</span>
                      }
                    </div>
                  }
                </div>
              }
            }
          </div>
        </section>

        <!-- Critères si automatique -->
        @if (g.mode === 'automatic') {
          <section class="detail-section">
            <div class="section-header">
              <h4>Critères d’attribution automatique</h4>
              <p>Évalués à l’attribution des tâches et à la lecture de l’inventaire.</p>
            </div>
            <div class="criteria-list">
              @if (criteriaBadges().length === 0) {
                <span class="empty-hint">Aucun critère spécifique.</span>
              } @else {
                @for (badge of criteriaBadges(); track badge) {
                  <span class="criteria-badge">{{ badge }}</span>
                }
              }
            </div>
          </section>
        }

        <!-- Étiquettes héritées -->
        <section class="detail-section">
          <div class="section-header">
            <h4>Étiquettes partagées (Labels)</h4>
            <p>
              Transmises automatiquement à tous les membres. Les étiquettes locales du nœud
              prévalent en cas de clé identique.
            </p>
          </div>
          <div class="labels-list">
            @if (labelsList().length === 0) {
              <span class="empty-hint">Aucune étiquette partagée.</span>
            } @else {
              @for (label of labelsList(); track label) {
                <span class="label-badge">{{ label }}</span>
              }
            }
          </div>
        </section>

        <!-- Actions -->
        @if (isAdmin()) {
          <div class="detail-actions">
            <button matButton="filled" type="button" (click)="edit.emit()">
              Modifier ce groupe
            </button>
          </div>
        }
      </div>
    }
  `,
  styles: `
    .group-detail {
      display: flex;
      flex-direction: column;
      gap: var(--space-4);
      padding: var(--space-2) 0;
    }

    .detail-section {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      padding: var(--space-4);
      background: var(--surface-container-low);
      border-radius: var(--radius-lg);
      border: 1px solid var(--border-subtle);
    }

    .section-title-row {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-2);

      h3 {
        margin: 0;
        font-size: var(--font-title-lg);
        font-weight: 700;
        color: var(--text-primary);
      }
    }

    .section-desc {
      margin: 0;
      font-size: var(--font-body);
      color: var(--text-secondary);
      line-height: 1.4;
    }

    .section-header {
      h4 {
        margin: 0;
        font-size: var(--font-title-sm);
        font-weight: 600;
        color: var(--text-primary);
      }
      p {
        margin: 2px 0 0 0;
        font-size: var(--font-label);
        color: var(--text-muted);
      }
    }

    .members-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
      gap: var(--space-2);
      margin-top: var(--space-1);
    }

    .member-card {
      display: flex;
      flex-direction: column;
      gap: var(--space-1);
      padding: var(--space-3);
      background: var(--surface-container);
      border-radius: var(--radius-md);
      border: 1px solid var(--border-subtle);
    }

    .member-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-2);

      .member-name {
        font-weight: 600;
        font-size: var(--font-title-sm);
        color: var(--text-primary);
      }
    }

    .member-specs {
      font-size: var(--font-label);
      color: var(--text-secondary);
    }

    .member-tools {
      display: flex;
      align-items: center;
      gap: var(--space-1);
      flex-wrap: wrap;
      margin-top: var(--space-1);

      .tools-label {
        font-size: var(--font-label);
        color: var(--text-muted);
      }

      .tool-tag {
        font-size: var(--font-label);
        padding: 1px 6px;
        background: var(--surface-container-high);
        border-radius: var(--radius-sm);
        color: var(--text-primary);
      }
    }

    .criteria-list,
    .labels-list {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
      margin-top: var(--space-1);
    }

    .criteria-badge {
      display: inline-flex;
      padding: 3px 10px;
      background: var(--accent-soft);
      color: var(--accent-strong);
      border-radius: var(--radius-pill);
      font-size: var(--font-label);
      font-weight: 600;
    }

    .label-badge {
      display: inline-flex;
      padding: 3px 10px;
      background: var(--surface-container-high);
      color: var(--text-secondary);
      border-radius: var(--radius-pill);
      font-size: var(--font-label);
      font-family: ui-monospace, 'JetBrains Mono', Consolas, monospace;
    }

    .empty-hint {
      margin: 0;
      font-size: var(--font-label);
      color: var(--text-muted);
      font-style: italic;
    }

    .detail-actions {
      display: flex;
      justify-content: flex-end;
      padding-top: var(--space-2);
    }
  `,
})
export class NodeGroupDetailComponent {
  readonly group = input<NodeGroupResponse | null>(null);
  readonly nodes = input<readonly NodeResponse[]>([]);
  readonly isAdmin = input(false);

  readonly edit = output<void>();

  protected readonly Math = Math;

  protected readonly memberNodes = computed(() => {
    const g = this.group();
    if (!g) return [];
    const all = this.nodes();
    if (g.mode === 'manual') {
      const set = new Set(g.nodeIds);
      return all.filter((n) => set.has(n.id));
    }
    return all.filter((n) => matchesGroupCriteria(g.criteria, n));
  });

  protected readonly criteriaBadges = computed(() => {
    const g = this.group();
    return g ? formatGroupCriteriaBadges(g.criteria) : [];
  });

  protected readonly labelsList = computed(() => {
    const g = this.group();
    if (!g) return [];
    return Object.entries(g.labels).map(([k, v]) => (v == null ? k : `${k}=${v}`));
  });

  protected formatNode(node: NodeResponse): string {
    return formatNodeLabel(node.name, node.displayName);
  }
}
