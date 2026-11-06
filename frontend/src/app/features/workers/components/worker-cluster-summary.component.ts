import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { IconComponent } from '../../../shared/ui/icon.component';
import { WorkerDataStore } from '../worker-data.store';
import { WorkerMembersStore } from '../worker-members.store';

/** Bandeaux d’alerte et synthèse chiffrée du central (emplacement `catalogue-summary`). */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-worker-cluster-summary',
  imports: [IconComponent],
  template: `
    @if (active() && local.pending()) {
      <div class="node-alert-banner node-alert-banner_warning" role="status">
        <span class="node-alert-icon" aria-hidden="true"><app-icon name="clock" /></span>
        <p><strong>Rattachement en attente</strong> vers {{ local.targetName() }}.</p>
      </div>
    }
    @if (active() && local.agentUnavailable() && !local.loading()) {
      <div class="node-alert-banner node-alert-banner_warning" role="status">
        <span class="node-alert-icon" aria-hidden="true"><app-icon name="warning" /></span>
        <p>
          <strong>L’agent de ce worker ne répond pas.</strong>
          Le suivi du rattachement est indisponible tant que l’agent ne répond pas.
        </p>
      </div>
    }
    @if (local.identityUnavailable() && !local.loading()) {
      <div class="node-alert-banner node-alert-banner_warning" role="alert">
        <span class="node-alert-icon" aria-hidden="true"><app-icon name="warning" /></span>
        <p>
          L’identité du central n’a pas pu être actualisée. Réessayez avant de modifier son
          appartenance.
        </p>
      </div>
    }
    @if (active() && local.status()?.enrollmentFailure; as failure) {
      <div class="node-alert-banner node-alert-banner_danger" role="alert">
        <span class="node-alert-icon" aria-hidden="true"><app-icon name="error" /></span>
        <p>Rattachement annulé : {{ failure }}</p>
      </div>
    }
    @if (active() && clusterIdentity()) {
      <section class="node-cluster-summary" aria-label="Synthèse du central">
        <div class="node-cluster-identity">
          <span class="node-cluster-icon" aria-hidden="true"><app-icon name="server" /></span>
          <span class="node-cluster-name">
            <strong>{{ clusterName() }}</strong>
            <small
              >{{ nodes().length }}
              {{ nodes().length > 1 ? 'workers membres' : 'worker membre' }}</small
            >
          </span>
        </div>
        @for (tile of summaryTiles(); track tile.label) {
          <div
            class="node-cluster-metric"
            [class.node-cluster-metric_attention]="tile.tone === 'attention'"
            [title]="tile.detail ?? tile.label"
          >
            <span>{{ tile.label }}</span>
            <strong>{{ tile.value }}</strong>
          </div>
        }
      </section>
    }
  `,
  styleUrl: './worker-cluster-summary.component.scss',
})
export class WorkerClusterSummaryComponent {
  /** Affiché uniquement quand l’onglet « Central » est actif. */
  readonly active = input(false);
  readonly data = inject(WorkerDataStore);
  readonly members = inject(WorkerMembersStore);
  protected readonly local = this.data.local;
  protected readonly clusterIdentity = this.data.local.identity;
  protected readonly clusterName = this.members.clusterName;
  protected readonly nodes = this.data.nodes;
  protected readonly summaryTiles = this.members.summaryTiles;
}
