import { IconComponent } from '../../../shared/ui/icon.component';
import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { ExecutionResponse, executionStateLabel } from '../../../shared/models/execution';
import { StatusBadgeComponent } from '../../../shared/ui/status-badge.component';

@Component({
  selector: 'app-overview-execution',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, DatePipe, MatProgressBarModule, RouterLink, StatusBadgeComponent],
  template: `
    <article class="job">
      <header>
        <div>
          <h3>{{ execution().media }}</h3>
          <p>{{ execution().library }} · {{ execution().flow }} · v{{ execution().flowVersion }}</p>
        </div>
        <app-status-badge
          [label]="stateLabel(execution())"
          [tone]="
            execution().ownership?.state === 'quarantined'
              ? 'warning'
              : execution().state === 'succeeded'
                ? 'success'
                : execution().state === 'failed'
                  ? 'danger'
                  : 'neutral'
          "
        />
      </header>
      <div class="status">
        <span>{{ execution().detail }}</span
        ><strong>{{
          execution().progress == null ? 'Progression indisponible' : execution().progress + ' %'
        }}</strong>
      </div>
      @if (execution().progress != null) {
        <mat-progress-bar
          mode="determinate"
          [value]="execution().progress ?? 0"
          [attr.aria-label]="'Progression de ' + execution().media"
        />
      }
      <footer>
        <span
          >{{ execution().node ?? 'Worker non attribué' }} ·
          {{ execution().updatedAt | date: 'dd/MM HH:mm' }}</span
        ><a
          routerLink="/activity"
          [queryParams]="{
            view:
              execution().state === 'succeeded'
                ? 'history'
                : execution().state === 'failed'
                  ? 'errors'
                  : 'active',
          }"
          >Voir le traitement <app-icon name="outward"
        /></a>
      </footer>
    </article>
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    .job {
      background: var(--surface-container-lowest);
      padding: 22px;
      border-radius: var(--radius-xl);
    }
    header,
    .status,
    footer {
      display: flex;
      align-items: start;
      justify-content: space-between;
      gap: 14px;
    }
    header > div {
      min-width: 0;
    }
    h3 {
      margin: 0;
      font-size: var(--font-body);
      overflow-wrap: anywhere;
    }
    p {
      margin: 6px 0 18px;
      color: var(--text-muted);
      font-size: var(--font-label);
      overflow-wrap: anywhere;
    }
    .status {
      margin-bottom: 10px;
      font-size: var(--font-label);
      line-height: 1.5;
    }
    .status span {
      overflow-wrap: anywhere;
    }
    .status strong {
      flex-shrink: 0;
    }
    footer {
      margin-top: 14px;
      font-size: var(--font-label);
      line-height: 1.5;
      flex-wrap: wrap;
      color: var(--text-muted);
    }
    a {
      color: var(--accent-strong);
      text-underline-offset: 3px;
      padding-block: 4px;
    }
    @media (max-width: 600px) {
      .job {
        padding: 18px;
      }
      header {
        flex-wrap: wrap;
      }
      .status {
        flex-wrap: wrap;
      }
    }
  `,
})
export class OverviewExecutionComponent {
  readonly execution = input.required<ExecutionResponse>();
  protected readonly stateLabel = executionStateLabel;
}
