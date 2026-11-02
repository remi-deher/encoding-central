import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ExecutionResponse, executionStateLabel, executionPhaseLabel } from '../models/execution';
import { StatusBadgeComponent, StatusTone } from './status-badge.component';

@Component({
  selector: 'app-execution-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, MatButtonModule, MatProgressBarModule, StatusBadgeComponent],
  template: `
    <article class="execution-card">
      <header class="execution-card__header">
        <div class="execution-card__title">
          <h3>{{ execution().media }}</h3>
          <p>{{ execution().library }} · {{ execution().flow }} · v{{ execution().flowVersion }}</p>
        </div>
        <app-status-badge
          [label]="stateLabel(execution())"
          [tone]="
            execution().ownership?.state === 'quarantined'
              ? 'warning'
              : stateTone(execution().state)
          "
        />
      </header>

      @if (execution().failure?.publicationConfirmed) {
        <p>Média publié ; une étape ultérieure a échoué.</p>
      }
      @if (phaseLabel(execution().currentPhase); as phase) {
        <p role="status">{{ phase }}</p>
      }
      <p class="execution-card__detail">{{ execution().detail }}</p>

      <div class="execution-card__facts">
        <div>
          <span>Worker</span>
          <strong>{{ execution().node ?? 'Non attribué' }}</strong>
        </div>
        <div>
          <span>Dernière activité</span>
          <strong>{{ execution().updatedAt | date: 'dd/MM/yyyy HH:mm:ss' }}</strong>
        </div>
        <div>
          <span>Tentative</span>
          <strong>{{ execution().attempt }}</strong>
        </div>
        <div>
          <span>Progression</span>
          <strong>{{
            execution().progress === null ? 'Indisponible' : execution().progress + ' %'
          }}</strong>
        </div>
      </div>

      @if (execution().progress !== null) {
        <mat-progress-bar mode="determinate" [value]="execution().progress ?? 0" />
      }

      @if (showPipelineAction()) {
        <footer class="execution-card__footer">
          <button matButton="outlined" type="button" (click)="pipelineRequested.emit()">
            Voir le pipeline
          </button>
        </footer>
      }
    </article>
  `,
  styles: `
    :host {
      display: block;
    }
    .execution-card {
      min-width: 0;
      padding: var(--space-4);
      display: grid;
      gap: 14px;
      border-radius: var(--radius-xl);
      background: var(--surface-container);
      box-shadow: var(--elevation-1);
      transition:
        border-radius 220ms var(--motion-spring-fast),
        background-color 220ms var(--motion-spring-fast),
        box-shadow 220ms var(--motion-spring-fast);
    }
    .execution-card:hover {
      border-radius: var(--radius-2xl);
      background: var(--surface-container-high);
      box-shadow: var(--elevation-2);
    }
    .execution-card__header {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      gap: var(--space-3);
    }
    .execution-card__title {
      min-width: 0;
    }
    .execution-card h3 {
      margin: 0;
      overflow-wrap: anywhere;
      color: var(--text-primary);
      font-size: var(--font-title-md);
      font-weight: 700;
      letter-spacing: -0.015em;
    }
    .execution-card__title p {
      margin: 5px 0 0;
      overflow-wrap: anywhere;
      color: var(--text-secondary);
      font-size: var(--font-body);
      line-height: 1.45;
    }
    .execution-card__detail {
      margin: 0;
      overflow-wrap: anywhere;
      color: var(--text-primary);
      font-size: var(--font-body);
      line-height: 1.5;
    }
    .execution-card__facts {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 150px), 1fr));
      gap: 3px;
      padding: 3px;
      border-radius: var(--radius-lg);
      background: var(--surface-container-low);
    }
    .execution-card__facts div {
      min-width: 0;
      display: grid;
      gap: var(--space-1);
      padding: var(--space-3);
      border-radius: var(--radius-md);
      background: var(--surface-container-lowest);
    }
    .execution-card__facts span {
      color: var(--text-muted);
      font-size: var(--font-label);
      font-weight: 600;
      letter-spacing: 0.04em;
      text-transform: uppercase;
    }
    .execution-card__facts strong {
      overflow-wrap: anywhere;
      color: var(--text-primary);
      font-size: var(--font-body);
      font-weight: 600;
    }
    mat-progress-bar {
      display: block;
      height: 6px;
      border-radius: var(--radius-pill);
      --mdc-linear-progress-track-height: 6px;
      --mdc-linear-progress-active-indicator-height: 6px;
    }
    .execution-card__footer {
      display: flex;
      justify-content: flex-end;
      padding-top: var(--space-3);
    }
    @media (max-width: 480px) {
      .execution-card {
        padding: var(--space-3);
      }
      .execution-card__header {
        flex-direction: column;
      }
    }
  `,
})
export class ExecutionCardComponent {
  readonly execution = input.required<ExecutionResponse>();
  readonly showPipelineAction = input(false);
  readonly pipelineRequested = output<void>();
  protected readonly stateLabel = executionStateLabel;
  protected readonly phaseLabel = executionPhaseLabel;

  protected stateTone(state: ExecutionResponse['state']): StatusTone {
    switch (state) {
      case 'succeeded':
        return 'success';
      case 'failed':
      case 'cancelling':
        return 'danger';
      case 'running':
        return 'accent';
      case 'queued':
      case 'deferred':
      case 'cancelled':
        return 'warning';
    }
  }
}
