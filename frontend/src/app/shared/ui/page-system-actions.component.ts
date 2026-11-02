import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { OverlayModule, ConnectedPosition } from '@angular/cdk/overlay';
import { A11yModule } from '@angular/cdk/a11y';
import { MatButtonModule } from '@angular/material/button';
import { ActiveJobsPanelComponent } from './active-jobs-panel/active-jobs-panel.component';
import { toSignal, toObservable } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { MatTooltipModule } from '@angular/material/tooltip';
import { of, switchMap } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { ClusterOverview, INITIAL_CLUSTER_OVERVIEW } from '../services/cluster-overview';
import { ExecutionTracking, INITIAL_EXECUTION_TRACKING } from '../services/execution-tracking';

@Component({
  selector: 'app-page-system-actions',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatTooltipModule,
    RouterLink,
    OverlayModule,
    A11yModule,
    MatButtonModule,
    ActiveJobsPanelComponent,
  ],
  template: `
    <div class="page-system-actions" role="group" aria-label="État du système">
      <a
        class="system-pill cluster-pill"
        routerLink="/nodes"
        [queryParams]="{ tab: 'cluster' }"
        [class.is-offline]="!clusterHealthy()"
        [attr.aria-label]="'État du cluster : ' + clusterLabel()"
        [matTooltip]="'État du cluster : ' + clusterLabel()"
      >
        <span class="cluster-dot" [class.is-offline]="!clusterHealthy()" aria-hidden="true"></span>
        <span class="cluster-label">{{ clusterLabel() }}</span>
      </a>
      <button
        matButton="text"
        type="button"
        class="system-pill tasks-pill"
        cdkOverlayOrigin
        #jobsOrigin="cdkOverlayOrigin"
        aria-haspopup="dialog"
        [attr.aria-expanded]="jobsOpen()"
        (click)="jobsOpen.update(toggle)"
        [attr.aria-label]="activeJobsLabel()"
        [matTooltip]="activeJobsLabel()"
        [matTooltipDisabled]="jobsOpen()"
      >
        <span class="tasks-dot" aria-hidden="true"></span>
        <span class="tasks-label">{{ activeJobsLabel() }}</span>
      </button>
      <ng-template
        cdkConnectedOverlay
        [cdkConnectedOverlayOrigin]="jobsOrigin"
        [cdkConnectedOverlayOpen]="jobsOpen()"
        [cdkConnectedOverlayHasBackdrop]="true"
        cdkConnectedOverlayBackdropClass="cdk-overlay-transparent-backdrop"
        [cdkConnectedOverlayPositions]="jobsPositions"
        [cdkConnectedOverlayViewportMargin]="16"
        (backdropClick)="jobsOpen.set(false)"
        (overlayKeydown)="closeJobsOnEscape($event)"
      >
        <div role="dialog" aria-label="Jobs en cours" cdkTrapFocus [cdkTrapFocusAutoCapture]="true">
          <app-active-jobs-panel [state]="activeJobsState()" (closed)="jobsOpen.set(false)" />
        </div>
      </ng-template>
    </div>
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    .page-system-actions {
      display: flex;
      flex: 0 0 auto;
      align-items: center;
      gap: 8px;
    }
    .system-pill {
      min-height: 38px;
      padding: 0 12px;
      display: inline-flex;
      align-items: center;
      gap: 8px;
      border-radius: var(--radius-pill);
      background: var(--surface-container);
      color: var(--text-secondary);
      font-size: var(--font-label);
      font-weight: 680;
      text-decoration: none;
      white-space: nowrap;
      transition:
        background-color 160ms ease,
        color 160ms ease;
    }
    .system-pill:hover {
      background: var(--surface-container-high);
      color: var(--text-primary);
    }
    .system-pill:focus-visible {
      outline: 2px solid var(--accent);
      outline-offset: 2px;
    }
    .cluster-pill.is-offline {
      background: var(--warning-soft);
      color: var(--warning);
    }
    .tasks-pill {
      background: var(--accent-soft);
      color: var(--accent-strong);
    }
    .cluster-dot,
    .tasks-dot {
      width: 8px;
      height: 8px;
      flex: 0 0 8px;
      border-radius: 50%;
      background: var(--success);
    }
    .cluster-dot.is-offline {
      background: var(--warning);
    }
    .tasks-dot {
      width: 7px;
      height: 7px;
      flex-basis: 7px;
      background: var(--accent-strong);
    }
    .cluster-label {
      max-width: 160px;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    @media (max-width: 1100px) and (min-width: 761px) {
      .cluster-label,
      .tasks-pill .tasks-label {
        display: none;
      }
    }
    @media (max-width: 760px) {
      .page-system-actions {
        width: 100%;
        justify-content: flex-end;
        flex-wrap: wrap;
      }
      .cluster-label {
        max-width: 96px;
      }
    }
    @media (max-width: 480px) {
      .page-system-actions {
        gap: 4px;
      }
      .system-pill {
        min-height: 36px;
        padding-inline: 8px;
      }
      .cluster-pill {
        display: none;
      }
    }
  `,
})
export class PageSystemActionsComponent {
  protected readonly jobsOpen = signal(false);
  protected readonly toggle = (value: boolean) => !value;
  protected readonly jobsPositions: ConnectedPosition[] = [
    { originX: 'end', originY: 'bottom', overlayX: 'end', overlayY: 'top', offsetY: 8 },
    { originX: 'end', originY: 'top', overlayX: 'end', overlayY: 'bottom', offsetY: -8 },
  ];
  protected closeJobsOnEscape(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault();
      this.jobsOpen.set(false);
    }
  }
  private readonly auth = inject(AuthService);
  private readonly disabled = computed(() => !this.auth.user());
  private readonly overview = inject(ClusterOverview);
  private readonly clusterState = toSignal(
    toObservable(this.disabled).pipe(
      switchMap((disabled) => (disabled ? of(INITIAL_CLUSTER_OVERVIEW) : this.overview.state$)),
    ),
    { initialValue: INITIAL_CLUSTER_OVERVIEW },
  );
  protected readonly clusterHealthy = computed(() => {
    const nodes = this.clusterState().snapshot?.nodes ?? [];
    return !nodes.length || nodes.some((node) => node.status === 'online');
  });
  protected readonly clusterLabel = computed(() => {
    const state = this.clusterState();
    if (state.status === 'loading') return 'Chargement…';
    if (state.status === 'error') return 'Données indisponibles';
    if (state.snapshot?.identity.standalone) return 'Nœud autonome';
    const nodes = state.snapshot?.nodes ?? [];
    return (
      nodes.filter((node) => node.status === 'online').length + ' / ' + nodes.length + ' en ligne'
    );
  });
  private readonly executionTracking = inject(ExecutionTracking);
  protected readonly activeJobsState = toSignal(
    toObservable(computed(() => this.jobsOpen() && !this.disabled())).pipe(
      switchMap((opened) =>
        opened
          ? this.executionTracking.observeStates(['running'])
          : of(INITIAL_EXECUTION_TRACKING),
      ),
    ),
    { initialValue: INITIAL_EXECUTION_TRACKING },
  );
  private readonly executionState = toSignal(
    toObservable(this.disabled).pipe(
      switchMap((disabled) =>
        disabled ? of(INITIAL_EXECUTION_TRACKING) : this.executionTracking.state$,
      ),
    ),
    { initialValue: INITIAL_EXECUTION_TRACKING },
  );
  protected readonly activeJobsLabel = computed(() => {
    const state = this.executionState();
    if (state.status === 'loading') return 'Jobs : …';
    if (state.status === 'error') return 'Jobs : indisponibles';
    const count = state.data?.activeCount ?? 0;
    return `${count} job${count === 1 ? '' : 's'} en cours`;
  });
}
