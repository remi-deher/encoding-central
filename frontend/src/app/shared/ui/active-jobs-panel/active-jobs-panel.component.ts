import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { EXECUTION_STATE_LABELS } from '../../models/execution';
import { ExecutionTrackingState } from '../../services/execution-tracking';
import { IconComponent } from '../icon.component';
import { StatusBadgeComponent } from '../status-badge.component';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatProgressBarModule, RouterLink, IconComponent, StatusBadgeComponent],
  selector: 'app-active-jobs-panel',
  styles: `
    :host {
      display: block;
      width: min(420px, calc(100vw - var(--space-6)));
      max-height: min(32rem, calc(100dvh - 8rem));
      overflow: auto;
      border-radius: var(--radius-xl);
      background: var(--surface-container);
      color: var(--text-primary);
      box-shadow: var(--elevation-2);
    }
    header,
    footer {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-3);
      padding: var(--space-3) var(--space-4);
    }
    h2 {
      margin: 0;
      font-size: var(--font-title-sm);
    }
    header app-icon {
      width: var(--space-4);
      height: var(--space-4);
    }
    ul {
      display: grid;
      gap: var(--space-1);
      list-style: none;
      padding: 0 var(--space-2);
      margin: 0;
    }
    li {
      display: grid;
      gap: var(--space-2);
      padding: var(--space-3);
      border-radius: var(--radius-md);
      background: var(--surface-container-low);
    }
    .job-heading {
      display: flex;
      justify-content: space-between;
      gap: var(--space-2);
      font-size: var(--font-label);
    }
    strong {
      overflow-wrap: anywhere;
      font-size: var(--font-body);
    }
    p {
      margin: 0;
      color: var(--text-secondary);
      font-size: var(--font-label);
      overflow-wrap: anywhere;
    }
    .feedback {
      padding: var(--space-4);
    }
    footer {
      flex-wrap: wrap;
      font-size: var(--font-label);
      color: var(--text-secondary);
    }
    button {
      flex-shrink: 0;
    }
  `,
  template: `
    <header>
      <h2>Jobs en cours</h2>
      <button
        matIconButton
        type="button"
        aria-label="Fermer les jobs en cours"
        (click)="closed.emit()"
      >
        <app-icon name="close" />
      </button>
    </header>
    @if (state().status === 'loading') {
      <p class="feedback" role="status">Chargement des traitements…</p>
    }
    @if (state().status === 'error') {
      <p class="feedback" role="status">
        Actualisation impossible.
        {{
          state().data
            ? 'Les dernières informations restent affichées.'
            : 'Réessayez dans quelques instants.'
        }}
      </p>
    }
    <ul aria-label="Traitements en cours">
      @for (job of jobs(); track job.id) {
        <li>
          <div class="job-heading">
            <span>{{ job.node ?? 'Nœud non renseigné' }}</span
            ><app-status-badge [label]="labels[job.state]" tone="accent" />
          </div>
          <strong>{{ fileName(job.media) }}</strong>
          <p>{{ job.library }} · {{ job.flow }} · v{{ job.flowVersion }}</p>
          <p>{{ job.detail || 'Étape non renseignée' }}</p>
          @if (job.progress !== null) {
            <mat-progress-bar
              mode="determinate"
              [value]="job.progress"
              [attr.aria-label]="'Progression de ' + fileName(job.media)"
            />
            <p>{{ job.progress }} %</p>
          } @else {
            <p>Progression non disponible</p>
          }
        </li>
      } @empty {
        @if (state().status === 'ready') {
          <li>Aucun traitement en cours.</li>
        }
      }
    </ul>
    @if ((state().data?.activeCount ?? 0) > jobs().length) {
      <p class="feedback">
        {{ jobs().length }} traitements actifs affichés. Retrouvez la suite dans Activité.
      </p>
    }
    <footer>
      <span>{{ state().data?.activeCount ?? '—' }} en cours sur ce serveur</span
      ><a
        matButton="text"
        routerLink="/activity"
        [queryParams]="{ view: 'active' }"
        (click)="closed.emit()"
        >Voir l’activité</a
      >
    </footer>
  `,
})
export class ActiveJobsPanelComponent {
  readonly state = input.required<ExecutionTrackingState>();
  readonly closed = output<void>();
  protected readonly labels = EXECUTION_STATE_LABELS;
  protected readonly jobs = computed(() =>
    (this.state().data?.executions ?? []).filter(
      (job) => job.state === 'running' && job.isActivelyProcessing === true,
    ),
  );
  protected fileName(path: string): string {
    return path.split(/[\\/]/).pop() || path;
  }
}
