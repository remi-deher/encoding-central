import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { StatusBadgeComponent } from '../../../shared/ui/status-badge.component';
import { NodeGroupResponse, NodeResponse, formatNodeLabel } from '../../../shared/models/cluster';
import {
  formatGroupCriteriaBadges,
  matchesGroupCriteria,
} from '../../../shared/models/node-group-rules';

@Component({
  selector: 'app-node-group-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatMenuModule, StatusBadgeComponent],
  template: `
    <article class="group-card" [attr.aria-label]="group().name">
      <header class="group-card-head">
        <div class="group-card-identity">
          <div class="group-title-row">
            <button type="button" class="group-title" (click)="open.emit()">
              {{ group().name }}
            </button>
            <app-status-badge
              [label]="
                group().mode === 'manual'
                  ? 'Manuel (' + memberNodes().length + ' nœuds)'
                  : 'Automatique (' + memberNodes().length + ' matchés)'
              "
              [tone]="group().mode === 'manual' ? 'neutral' : 'accent'"
            />
          </div>
          @if (group().description) {
            <p class="group-desc">{{ group().description }}</p>
          }
        </div>

        @if (isAdmin()) {
          <div class="group-card-actions">
            <button
              matIconButton
              type="button"
              class="group-card-menu-btn"
              [matMenuTriggerFor]="cardMenu"
              [attr.aria-label]="'Actions pour le groupe ' + group().name"
            >
              <span aria-hidden="true">⋮</span>
            </button>
            <mat-menu #cardMenu="matMenu">
              <button mat-menu-item (click)="edit.emit()">Modifier</button>
              <button mat-menu-item class="danger-action" (click)="delete.emit()">Supprimer</button>
            </mat-menu>
          </div>
        }
      </header>

      <div class="group-card-sections">
        <!-- Section Membres -->
        <div class="group-card-section">
          <span class="section-title">Membres ({{ memberNodes().length }})</span>
          <div class="pills-list">
            @if (memberNodes().length === 0) {
              <span class="muted-hint">Aucun nœud membre</span>
            } @else {
              @for (node of displayedMembers(); track node.id) {
                <span class="node-pill">
                  <span
                    class="status-dot"
                    [class.online]="node.status === 'online'"
                    [class.offline]="node.status === 'offline'"
                  ></span>
                  {{ formatNode(node) }}
                </span>
              }
              @if (remainingMembersCount() > 0) {
                <span class="node-pill"
                  >+{{ remainingMembersCount() }} autre{{
                    remainingMembersCount() > 1 ? 's' : ''
                  }}</span
                >
              }
            }
          </div>
        </div>

        <!-- Section Critères (Automatique) ou Type (Manuel) -->
        <div class="group-card-section">
          <span class="section-title">
            {{ group().mode === 'automatic' ? 'Critères d’attribution' : 'Mode d’adhésion' }}
          </span>
          <div class="pills-list">
            @if (group().mode === 'manual') {
              <span class="muted-hint">Attribution manuelle directe</span>
            } @else {
              @if (criteriaBadges().length === 0) {
                <span class="muted-hint">Aucun critère spécifique</span>
              } @else {
                @for (badge of criteriaBadges(); track badge) {
                  <span class="criteria-pill">{{ badge }}</span>
                }
              }
            }
          </div>
        </div>

        <!-- Section Étiquettes partagées -->
        <div class="group-card-section">
          <span class="section-title">Étiquettes transmises</span>
          <div class="pills-list">
            @if (labelsList().length === 0) {
              <span class="muted-hint">Aucune étiquette</span>
            } @else {
              @for (label of labelsList(); track label) {
                <span class="label-pill">{{ label }}</span>
              }
            }
          </div>
        </div>
      </div>
    </article>
  `,
  styleUrl: './node-group-card.component.scss',
})
export class NodeGroupCardComponent {
  readonly group = input.required<NodeGroupResponse>();
  readonly nodes = input<readonly NodeResponse[]>([]);
  readonly isAdmin = input(false);

  readonly open = output<void>();
  readonly edit = output<void>();
  readonly delete = output<void>();

  protected readonly memberNodes = computed(() => {
    const g = this.group();
    const all = this.nodes();
    if (g.mode === 'manual') {
      const set = new Set(g.nodeIds);
      return all.filter((n) => set.has(n.id));
    }
    return all.filter((n) => matchesGroupCriteria(g.criteria, n));
  });

  protected readonly displayedMembers = computed(() => {
    return this.memberNodes().slice(0, 4);
  });

  protected readonly remainingMembersCount = computed(() => {
    return Math.max(0, this.memberNodes().length - 4);
  });

  protected readonly criteriaBadges = computed(() => {
    return formatGroupCriteriaBadges(this.group().criteria);
  });

  protected readonly labelsList = computed(() => {
    return Object.entries(this.group().labels).map(([k, v]) => (v == null ? k : `${k}=${v}`));
  });

  protected formatNode(node: NodeResponse): string {
    return formatNodeLabel(node.name, node.displayName);
  }
}
