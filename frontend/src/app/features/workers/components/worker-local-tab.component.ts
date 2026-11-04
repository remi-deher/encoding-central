import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, exhaustMap, of, switchMap } from 'rxjs';
import { LiveUpdates } from '../../../shared/services/live-updates';
import { workerLifecycle, NODE_LIFECYCLE_LABELS, NODE_LIFECYCLE_TONES } from '../../../shared/models/central-health';
import { formatLastSignal, localWorkerSheet } from '../../../shared/models/worker-management';
import { WorkerActivityResponse, NODE_ROLE_LABELS } from '../../../shared/models/central';
import { AuthService } from '../../../core/auth/auth.service';
import { WorkerDataStore } from '../worker-data.store';
import { WorkerMembersStore } from '../worker-members.store';
import { WorkerOverviewCardComponent } from '../worker-overview-card.component';
import { nodeOverviewHardware, nodeOverviewLibraries } from '../worker-catalog.utils';

/** Onglet « Ce worker » : fiche d’aperçu du worker local. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-worker-local-tab',
  imports: [WorkerOverviewCardComponent],
  template: `
    <app-worker-overview-card
      ariaLabel="Fiche du worker local"
      layout="local"
      [isMaster]="(localNode()?.roles ?? []).includes('server')"
      [title]="localSheet().title"
      [subtitle]="
        (localNode()?.roles ?? []).includes('server')
          ? 'Central de ' + clusterName()
          : localSheet().subtitle
      "
      [hardware]="localOverviewHardware()"
      [statusLabel]="localOverviewStatus().label"
      [statusTone]="localOverviewStatus().tone"
      [nodeId]="localNode()?.id ?? 'local-node'"
      [roles]="localOverviewRoles()"
      [admissionEnabled]="localAdmissionEnabled()"
      [admissionDisabled]="localAdmissionDisabled()"
      [taskCapacity]="localTaskCapacity()"
      [meta]="localOverviewMeta()"
      [notices]="localSheet().notices"
      [sections]="localOverviewSections()"
      [libraries]="localOverviewLibraries()"
      [libraryAccessState]="libraryAccessState()"
      (actionSelected)="onLocalSheetAction($event)"
      (admissionChange)="members.setLocalAdmission($event)"
    />
  `,
})
export class WorkerLocalTabComponent {
  private readonly http = inject(HttpClient);
  private readonly live = inject(LiveUpdates);
  readonly data = inject(WorkerDataStore);
  readonly members = inject(WorkerMembersStore);
  private readonly isAdmin = inject(AuthService).isAdmin;

  protected readonly localNode = this.data.localNode;
  protected readonly clusterName = this.members.clusterName;
  protected readonly libraryAccessState = this.data.libraryAccessState;

  protected readonly localActivity = toSignal(
    toObservable(computed(() => this.localNode()?.id ?? null)).pipe(
      switchMap((id) =>
        id
          ? this.live.drive(['executions', 'workers']).pipe(
              exhaustMap(() =>
                this.http
                  .get<WorkerActivityResponse>('/api/v1/workers/' + id + '/activity')
                  .pipe(catchError(() => of(null))),
              ),
            )
          : of(null),
      ),
    ),
    { initialValue: null },
  );

  protected readonly localOverviewHardware = computed(() =>
    nodeOverviewHardware(
      this.localNode()?.inventory ?? this.data.local.status()?.inventory ?? null,
    ),
  );

  protected readonly localTaskCapacity = computed(() => {
    const node = this.localNode();
    const status = this.data.local.status();
    const roles = node?.roles ?? status?.roles ?? null;
    const workerEnabled = roles?.includes('worker') ?? null;
    const accepted = this.localActivity()?.active ?? null;
    const maximum = node?.maxConcurrentTasks ?? status?.maxConcurrentTasks ?? null;
    const remaining =
      workerEnabled === true && accepted !== null && maximum !== null
        ? Math.max(0, maximum - accepted)
        : null;
    let note = '';
    if (workerEnabled === false) note = 'Fonction Worker désactivée.';
    else if (workerEnabled === null) note = 'État du Worker indisponible.';
    else if (accepted === null) note = 'Comptage des tâches indisponible.';
    else if (maximum === null) note = 'Capacité non déclarée.';
    else if (node && workerLifecycle(node) === 'draining')
      note = 'Admission suspendue : les places libres ne sont pas attribuées.';

    return {
      accepted,
      remaining,
      maximum: workerEnabled === true ? maximum : null,
      note,
    };
  });

  protected readonly localSheet = computed(() =>
    localWorkerSheet({
      status: this.data.local.status(),
      node: this.localNode(),
      clusterName: this.clusterName(),
      agentUnavailable: this.data.local.agentUnavailable(),
      activeTasks: this.localActivity()?.active ?? null,
    }),
  );

  protected readonly localOverviewSections = computed(() =>
    this.localSheet().sections.filter(
      (section) => section.id !== 'state' && section.id !== 'hardware',
    ),
  );

  protected readonly localOverviewStatus = computed(() => {
    const node = this.localNode();
    return node
      ? {
          label: NODE_LIFECYCLE_LABELS[workerLifecycle(node)],
          tone: NODE_LIFECYCLE_TONES[workerLifecycle(node)],
        }
      : {
          label: this.data.local.agentUnavailable() ? 'Agent injoignable' : 'En ligne',
          tone: this.data.local.agentUnavailable() ? ('warning' as const) : ('success' as const),
        };
  });

  protected readonly localOverviewRoles = computed(() =>
    (this.localNode()?.roles ?? this.data.local.status()?.roles ?? []).map(
      (role) => NODE_ROLE_LABELS[role],
    ),
  );

  protected readonly localAdmissionEnabled = computed(() => {
    const node = this.localNode();
    if (!this.isAdmin() || !node) return null;
    if (!node.roles.includes('worker')) return null;
    const lifecycle = workerLifecycle(node);
    return lifecycle === 'quarantined' || lifecycle === 'incompatible'
      ? null
      : lifecycle !== 'draining';
  });

  protected readonly localAdmissionDisabled = computed(() => {
    const node = this.localNode();
    return !node || this.members.admissionBusy().has(node.id);
  });

  protected readonly localOverviewMeta = computed(() => {
    const lastSignal = formatLastSignal(this.localNode()?.lastSeenAt);
    return lastSignal ?? (this.data.local.agentUnavailable() ? 'Indisponible' : 'En attente');
  });

  protected readonly localOverviewLibraries = computed(() =>
    nodeOverviewLibraries(
      this.data.allLibraries(),
      this.data.nodeAccessByLibrary(),
      this.localNode()?.id ?? this.data.local.status()?.workerId ?? null,
    ),
  );

  protected onLocalSheetAction(actionId: string): void {
    if (actionId === 'capabilities') {
      const node = this.localNode();
      if (node) this.data.openNodeCard(node.id);
    }
  }
}
