import { HttpClient } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { firstValueFrom, Observable, of, catchError, EMPTY, finalize, forkJoin, map } from 'rxjs';
import { CentralApi } from '../../shared/services/central-api';
import { NotificationService } from '../../shared/services/notification.service';
import { AuthService } from '../../core/auth/auth.service';
import { ConfirmationDialogComponent } from '../../shared/ui/confirmation-dialog.component';
import {
  DataTableActionEvent,
  DataTableColumn,
  DataTableRowAction,
  DataTableSelectionAction,
  DataTableSelectionActionEvent,
} from '../../shared/ui/data-table.component';
import { CategoryTabOption } from '../../shared/ui/category-tabs.component';
import type { ResourceCardAction } from '../../shared/ui/resource-card.component';
import type { SummaryTile } from '../../shared/ui/summary-tiles.component';
import { workerLifecycle, NODE_LIFECYCLE_LABELS, NODE_LIFECYCLE_TONES } from '../../shared/models/central-health';
import {
  formatWorkerLabel,
  WorkerRole,
  WorkerResponse,
  NODE_ROLE_LABELS,
} from '../../shared/models/central';
import { memberWorkerOverviewSections } from '../../shared/models/worker-management';
import { WorkerDataStore } from './worker-data.store';
import { WorkerDetailStore } from './worker-detail.store';
import {
  MemberCard,
  NODE_ROLE_OPTIONS,
  memberTaskCapacity,
  nodeOverviewHardware,
  nodeOverviewLibraries,
  workerStateGroup,
} from './worker-catalog.utils';

/**
 * Onglet « Central » : liste des membres, filtres, sélection, actions groupées,
 * admission des tâches, cycle de vie et identité (nom/étiquettes).
 */
@Injectable()
export class WorkerMembersStore {
  private readonly http = inject(HttpClient);
  private readonly clusterApi = inject(CentralApi);
  private readonly dialog = inject(MatDialog);
  private readonly notifications = inject(NotificationService);
  readonly data = inject(WorkerDataStore);
  readonly detail = inject(WorkerDetailStore);
  readonly isAdmin = inject(AuthService).isAdmin;

  readonly selectedRole = signal('all');
  readonly selectedState = signal('all');
  readonly nodeSearchQuery = signal('');
  readonly selectedMemberIds = signal<ReadonlySet<string>>(new Set());
  readonly bulkBusy = signal(false);
  readonly admissionBusy = signal<ReadonlySet<string>>(new Set());
  readonly removalBusy = signal(false);
  readonly lifecycleBusy = signal(false);

  readonly columns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Nom du worker', sortable: true },
    { key: 'roleLabel', label: 'Rôle', sortable: true },
    { key: 'libraryAccess', label: 'Accès aux bibliothèques', sortable: true },
    { key: 'workerLoad', label: 'Traitements', sortable: true },
    { key: 'state', label: 'État', sortable: true },
    { key: 'lastSignal', label: 'Dernier signal', sortable: true },
  ];

  readonly roleOptions = computed<readonly CategoryTabOption[]>(() => {
    const nodes = this.data.nodes();
    return [
      { value: 'all', label: 'Tous', count: nodes.length },
      ...NODE_ROLE_OPTIONS.map((role) => ({
        ...role,
        count: nodes.filter((node) => node.roles.includes(role.value)).length,
      })),
    ];
  });

  readonly stateOptions = computed<readonly CategoryTabOption[]>(() => {
    const nodes = this.data.nodes();
    const count = (group: string) => nodes.filter((node) => workerStateGroup(node) === group).length;
    return [
      { value: 'all', label: 'Tous', count: nodes.length },
      { value: 'online', label: 'En ligne', count: count('online') },
      { value: 'attention', label: 'À traiter', count: count('attention') },
      { value: 'offline', label: 'Hors ligne', count: count('offline') },
    ];
  });

  readonly activeFilters = computed(() => {
    const filters: { id: string; label: string; clear: () => void }[] = [];
    const role = this.selectedRole();
    if (role !== 'all') {
      const label = this.roleOptions().find((option) => option.value === role)?.label ?? role;
      filters.push({ id: 'role', label, clear: () => this.selectedRole.set('all') });
    }
    const state = this.selectedState();
    if (state !== 'all') {
      const label = this.stateOptions().find((option) => option.value === state)?.label ?? state;
      filters.push({ id: 'state', label, clear: () => this.selectedState.set('all') });
    }
    return filters;
  });

  private readonly filteredRawNodes = computed(() => {
    const role = this.selectedRole();
    const query = this.nodeSearchQuery().trim().toLocaleLowerCase('fr');
    return this.data
      .nodes()
      .filter((node) => role === 'all' || node.roles.includes(role as WorkerRole))
      .filter(
        (node) =>
          !query ||
          [node.name, node.displayName ?? '', node.id].some((value) =>
            value.toLocaleLowerCase('fr').includes(query),
          ),
      )
      .filter(
        (node) => this.selectedState() === 'all' || workerStateGroup(node) === this.selectedState(),
      )
      .sort(
        (a, b) =>
          (workerStateGroup(a) === 'attention' ? -1 : 0) -
            (workerStateGroup(b) === 'attention' ? -1 : 0) || a.name.localeCompare(b.name, 'fr'),
      );
  });

  readonly filteredNodes = computed(() =>
    this.filteredRawNodes().map((node) => this.data.toRecord(node)),
  );

  /** Cartes de synthèse des membres du central, avec actions secondaires dans le menu. */
  readonly memberCards = computed<readonly MemberCard[]>(() =>
    this.filteredRawNodes().map((node) => {
      const lifecycle = workerLifecycle(node);
      const record = this.data.toRecord(node);
      const isMaster = node.roles.includes('server');
      return {
        id: node.id,
        title: formatWorkerLabel(node.name, node.displayName),
        subtitle: isMaster
          ? `Central de ${this.clusterName()}`
          : `Worker de ${this.clusterName()}`,
        statusLabel: NODE_LIFECYCLE_LABELS[lifecycle],
        statusTone: NODE_LIFECYCLE_TONES[lifecycle],
        isMaster,
        roles: node.roles.map((role) => NODE_ROLE_LABELS[role]),
        admissionEnabled: this.admissionEnabledFor(node),
        admissionDisabled: this.admissionBusy().has(node.id),
        hardware: nodeOverviewHardware(node.inventory ?? null),
        taskCapacity: memberTaskCapacity(node, this.data.activityKnown(), this.data.activeTasks()),
        meta: record.lastSignal,
        sections: memberWorkerOverviewSections(
          node,
          this.data.activityKnown() && node.status === 'online'
            ? (this.data.activeTasks()[node.id] ?? 0)
            : null,
          this.clusterName(),
        ).filter((section) => section.id !== 'state' && section.id !== 'hardware'),
        libraries: nodeOverviewLibraries(
          this.data.allLibraries(),
          this.data.nodeAccessByLibrary(),
          node.id,
        ),
      };
    }),
  );

  readonly summaryTiles = computed<readonly SummaryTile[]>(() => {
    const nodes = this.data.nodes();
    const online = nodes.filter((node) => workerLifecycle(node) === 'online');
    const capacity = online
      .filter((node) => node.roles.includes('worker'))
      .reduce((total, node) => total + node.maxConcurrentTasks, 0);
    const pending = this.data.pendingEnrollments().length;
    return [
      {
        label: 'Workers en ligne',
        value: `${online.length} / ${nodes.length}`,
        detail: 'selon le dernier signal reçu',
      },
      {
        label: 'Capacité simultanée',
        value: String(capacity),
        detail: 'tâches en parallèle sur les workers en ligne',
      },
      {
        label: 'Hors service',
        value: String(nodes.length - online.length),
        detail: 'hors ligne, en vidange, incompatibles ou en quarantaine',
      },
      {
        label: 'Demandes de rattachement',
        value: String(pending),
        detail: pending ? 'à examiner dans cet onglet' : 'aucune demande en attente',
        tone: pending ? 'attention' : 'default',
      },
    ];
  });

  readonly clusterName = computed(
    () =>
      this.data.local.identity()?.name ?? this.data.local.status()?.centralName ?? 'Central',
  );

  readonly clusterAddress = computed(
    () =>
      this.data.local.target()?.apiBaseUrl ??
      this.data.local
        .clusters()
        .find((cluster) => cluster.id === this.data.local.status()?.centralId)?.apiBaseUrl ??
      null,
  );

  readonly memberSelectionActions: readonly DataTableSelectionAction[] = [
    { id: 'drain', label: 'Vidanger', visibleWhen: () => !this.bulkBusy() },
    { id: 'resume', label: 'Reprendre le travail', visibleWhen: () => !this.bulkBusy() },
    {
      id: 'delete',
      label: 'Supprimer',
      visibleWhen: () => !this.bulkBusy(),
      confirmation: (rows) => ({
        title: 'Supprimer ' + rows.length + ' worker(s) ?',
        message:
          'Les accès seront révoqués ; les fichiers et l’historique sont conservés. Les workers avec des traitements actifs peuvent refuser le retrait.',
        confirmLabel: 'Supprimer les workers',
      }),
    },
  ];

  readonly memberActions = computed<readonly ResourceCardAction[]>(() =>
    this.isAdmin()
      ? [
          { id: 'identity', label: 'Nom et étiquettes' },
          ...(this.removalBusy()
            ? []
            : [{ id: 'delete', label: 'Supprimer', tone: 'danger' as const }]),
        ]
      : [],
  );

  readonly nodeActions: readonly DataTableRowAction[] = [
    { id: 'identity', label: 'Nom et étiquettes', visibleWhen: () => this.isAdmin() },
    {
      id: 'delete',
      label: 'Supprimer',
      tone: 'danger',
      visibleWhen: () => !this.removalBusy(),
      confirmation: (row) => ({
        title: 'Supprimer ce worker ?',
        message:
          'Le worker « ' +
          row['name'] +
          ' » sera retiré du central et ses accès révoqués. Les fichiers et l’historique seront conservés. Pour le rattacher de nouveau, son identité locale devra être renouvelée.',
        confirmLabel: 'Supprimer le worker',
      }),
    },
  ];

  onRoleChange(role: string): void {
    this.selectedRole.set(role);
  }

  clearNodeFilters(): void {
    this.selectedRole.set('all');
    this.selectedState.set('all');
  }

  toggleMember(id: string, checked: boolean): void {
    this.selectedMemberIds.update((current) => {
      const next = new Set(current);
      if (checked) next.add(id);
      else next.delete(id);
      return next;
    });
  }

  onMemberCardAction(id: string, actionId: string): void {
    const row = this.filteredNodes().find((item) => item.id === id);
    if (!row) return;
    if (actionId !== 'delete') {
      this.onNodeAction({ actionId, row });
      return;
    }
    this.dialog
      .open(ConfirmationDialogComponent, {
        data: this.nodeActions.find((action) => action.id === 'delete')!.confirmation!(row),
      })
      .afterClosed()
      .subscribe((confirmed) => {
        if (confirmed) this.onNodeAction({ actionId: 'delete', row });
      });
  }

  runMemberBulk(actionId: 'drain' | 'resume' | 'delete'): void {
    const rows = this.filteredNodes().filter((row) => this.selectedMemberIds().has(row.id));
    if (!rows.length) return;
    const action = this.memberSelectionActions.find((item) => item.id === actionId)!;
    const finish = () => {
      this.onMemberSelectionAction({ actionId, rows });
      this.selectedMemberIds.set(new Set());
    };
    if (!action.confirmation) return finish();
    this.dialog
      .open(ConfirmationDialogComponent, { data: action.confirmation(rows) })
      .afterClosed()
      .subscribe((confirmed) => {
        if (confirmed) finish();
      });
  }

  onMemberSelectionAction(event: DataTableSelectionActionEvent): void {
    if (!this.isAdmin() || this.bulkBusy()) return;
    const ids = event.rows.map((row) => String(row['id']));
    if (event.actionId === 'delete')
      this.runBulk(ids, (id) => this.http.delete<void>('/api/v1/workers/' + id), 'suppression');
    else if (event.actionId === 'drain' || event.actionId === 'resume')
      this.runBulk(
        ids,
        (id) =>
          this.http.put<void>('/api/v1/workers/' + id + '/draining', {
            draining: event.actionId === 'drain',
          }),
        event.actionId === 'drain' ? 'vidange' : 'reprise',
      );
  }

  private runBulk(
    ids: readonly string[],
    call: (id: string) => Observable<void>,
    label: string,
  ): void {
    this.bulkBusy.set(true);
    forkJoin(
      ids.map((id) =>
        call(id).pipe(
          map(() => true),
          catchError(() => of(false)),
        ),
      ),
    )
      .pipe(finalize(() => this.bulkBusy.set(false)))
      .subscribe((results) => {
        const failed = results.filter((ok) => !ok).length;
        this.notifications.show(
          failed
            ? `${label} : ${results.length - failed} worker(s) traité(s), ${failed} en échec.`
            : `${label} : ${results.length} worker(s) traité(s).`,
        );
        this.clusterApi
          .listNodes()
          .pipe(catchError(() => EMPTY))
          .subscribe((nodes) => this.data.nodes.set(nodes));
      });
  }

  admissionEnabledFor(node: WorkerResponse): boolean | null {
    if (!this.isAdmin()) return null;
    if (!node.roles.includes('worker')) return null;
    const lifecycle = workerLifecycle(node);
    if (lifecycle === 'quarantined' || lifecycle === 'incompatible') return null;
    return lifecycle !== 'draining';
  }

  admissionDisabledFor(node: WorkerResponse): boolean {
    return this.admissionBusy().has(node.id);
  }

  setMemberAdmission(nodeId: string, enabled: boolean): void {
    const node = this.data.nodes().find((item) => item.id === nodeId);
    if (node) void this.setNodeAdmission(node, enabled);
  }

  setLocalAdmission(enabled: boolean): void {
    const node = this.data.localNode();
    if (node) void this.setNodeAdmission(node, enabled);
  }

  setSelectedNodeAdmission(enabled: boolean): void {
    const node = this.rawNode();
    if (node) void this.setNodeAdmission(node, enabled);
  }

  private rawNode(): WorkerResponse | null {
    return (
      this.data.nodes().find((node) => node.id === this.data.selectedNode()?.id) ?? null
    );
  }

  private async setNodeAdmission(node: WorkerResponse, enabled: boolean): Promise<void> {
    const active = this.admissionEnabledFor(node);
    if (active === null || active === enabled || this.admissionBusy().has(node.id)) return;
    if (!enabled) {
      const confirmed = await firstValueFrom(
        this.dialog
          .open<ConfirmationDialogComponent, unknown, boolean>(ConfirmationDialogComponent, {
            data: {
              compact: true,
              variant: 'danger',
              eyebrow: 'Traitements du worker',
              title: `Suspendre les nouvelles tâches sur « ${node.displayName?.trim() || node.name} » ?`,
              message:
                'Aucune nouvelle tâche ne sera affectée à ce worker. Les traitements déjà en cours ne sont pas interrompus et pourront se terminer normalement.',
              confirmLabel: 'Suspendre les tâches',
            },
          })
          .afterClosed(),
      );
      if (!confirmed) return;
    }
    this.admissionBusy.update((busy) => new Set(busy).add(node.id));
    try {
      await firstValueFrom(
        this.http.put<void>(`/api/v1/workers/${node.id}/draining`, { draining: !enabled }),
      );
      const nodes = await firstValueFrom(this.clusterApi.listNodes());
      this.data.nodes.set(nodes);
      this.notifications.show(
        enabled
          ? `Le worker « ${node.displayName?.trim() || node.name} » peut de nouveau recevoir des tâches.`
          : `Les nouvelles tâches sont suspendues sur « ${node.displayName?.trim() || node.name} ».`,
      );
    } catch (error) {
      this.notifications.show(
        (error as { error?: { title?: string } } | null)?.error?.title ??
          'Impossible de modifier l’admission des tâches sur ce worker.',
      );
    } finally {
      this.admissionBusy.update((busy) => {
        const next = new Set(busy);
        next.delete(node.id);
        return next;
      });
    }
  }

  onNodeAction(event: DataTableActionEvent): void {
    if (event.actionId === 'identity') {
      const node = this.filteredNodes().find((item) => item.id === event.row['id']);
      if (node) void this.detail.editNodeIdentity(node);
      return;
    }
    if (event.actionId !== 'delete' || this.removalBusy()) return;
    const id = String(event.row['id']);
    this.removalBusy.set(true);
    this.http
      .delete<void>('/api/v1/workers/' + id)
      .pipe(
        catchError((error: { error?: { title?: string } }) => {
          this.notifications.show(error.error?.title ?? 'Impossible de supprimer ce worker.');
          return EMPTY;
        }),
        finalize(() => this.removalBusy.set(false)),
      )
      .subscribe(() => {
        this.data.nodes.update((nodes) => nodes.filter((node) => node.id !== id));
        if (this.data.selectedNode()?.id === id) this.data.onDetailClosed();
        this.data.loadLibraryAccessSummary();
        this.notifications.show('Worker supprimé du central.');
      });
  }

  confirmNodeRemoval(): void {
    const row = this.data.selectedNode();
    if (!row || this.removalBusy()) return;
    this.dialog
      .open(ConfirmationDialogComponent, {
        data: this.nodeActions.find((action) => action.id === 'delete')!.confirmation!(row),
      })
      .afterClosed()
      .subscribe((confirmed) => {
        if (confirmed) this.onNodeAction({ actionId: 'delete', row });
      });
  }

  clearQuarantine(): void {
    const node = this.rawNode();
    if (!node || this.lifecycleBusy()) return;
    this.sendLifecycle(this.http.post<void>('/api/v1/workers/' + node.id + '/quarantine/clear', {}));
  }

  private sendLifecycle(request: Observable<void>): void {
    this.lifecycleBusy.set(true);
    request
      .pipe(
        catchError(() => {
          this.notifications.show('Impossible de modifier l’état du worker.');
          return EMPTY;
        }),
        finalize(() => this.lifecycleBusy.set(false)),
      )
      .subscribe(() => {
        this.clusterApi
          .listNodes()
          .pipe(catchError(() => EMPTY))
          .subscribe((nodes) => this.data.nodes.set(nodes));
      });
  }

  lifecycleText = computed(() => {
    const node = this.rawNode();
    switch (node?.lifecycle) {
      case 'draining':
        return 'Vidange en cours : le worker termine ses tâches et n’en reçoit plus de nouvelles.';
      case 'incompatible':
        return 'Version incompatible avec le central : ' + (node.lifecycleReason ?? '');
      case 'quarantined':
        return (
          'Quarantaine : ' +
          (node.lifecycleReason ?? 'identité probablement dupliquée') +
          ' Aucune tâche n’est attribuée tant qu’elle n’est pas levée.'
        );
      default:
        return '';
    }
  });

  rawSelectedNode = computed(
    () => this.data.nodes().find((node) => node.id === this.data.selectedNode()?.id) ?? null,
  );

}
