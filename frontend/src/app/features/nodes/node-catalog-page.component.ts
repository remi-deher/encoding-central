import { IconComponent } from '../../shared/ui/icon.component';
import type { SummaryTile } from '../../shared/ui/summary-tiles.component';
import {
  formatMemory,
  nodeAcceleratorBadges,
  nodeHardwareLabel,
  tidyProcessorName,
} from '../../shared/models/node-hardware';
import type { ResourceCardAction } from '../../shared/ui/resource-card.component';
import {
  nodeLifecycle,
  NODE_LIFECYCLE_LABELS,
  NODE_LIFECYCLE_TONES,
} from '../../shared/models/cluster-health';
import { firstValueFrom } from 'rxjs';
import {
  ClusterApi,
  ClusterReconciliationHealthStatus,
  ClusterTaskStatus,
} from '../../shared/services/cluster-api';
import { EnrollmentWindow, FirstLinkAcceptance } from '../../shared/models/auth';
import {
  formatNodeLabel,
  NodeRole,
  NodeResponse,
  NodeInventory,
  LibraryResponse,
  NodeToolCapability,
  NodeActivityResponse,
  NodeProcessingSettings,
  NODE_ROLE_LABELS,
  NodeGroupResponse,
} from '../../shared/models/cluster';
import { NodeSchedule } from '../../shared/models/node-schedule';
import { WeeklyScheduleFormComponent } from '../../shared/ui/weekly-schedule-form.component';
import { MatDialog } from '@angular/material/dialog';
import { ConfirmationDialogComponent } from '../../shared/ui/confirmation-dialog.component';
import { LeadershipTransitionDialogComponent } from './components/leadership-transition-dialog.component';
import { DrawerActionButtonComponent } from '../../shared/ui/drawer-action-button/drawer-action-button.component';
import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { AuthService } from '../../core/auth/auth.service';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  viewChildren,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { LocalNodeController } from '../../shared/services/local-node-controller';
import { GuidedSetupComponent, GuidedStep } from '../../shared/ui/guided-setup.component';
import {
  ResourceSheetComponent,
  ResourceSheetSection,
} from '../../shared/ui/resource-sheet.component';
import { StatusTone } from '../../shared/ui/status-badge.component';
import {
  AUTONOMOUS_CARDS,
  CREATE_CLUSTER_FORM,
  NODE_MANAGEMENT_TABS,
  clusterAddressForm,
  clusterJoinSteps,
  formatLastSignal,
  localNodeSheet,
  memberNodeOverviewSections,
  reconciliationBannerPresentation,
  runtimeEnvironmentLabel,
} from '../../shared/models/node-management';
import { NodeOverviewCardComponent } from './node-overview-card.component';
import type {
  NodeOverviewHardware,
  NodeOverviewPlatform,
  NodeOverviewLibrary,
  NodeOverviewRoleSwitch,
  NodeOverviewTaskCapacity,
} from './node-overview-card.component';
import { MatTabsModule } from '@angular/material/tabs';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { takeUntilDestroyed, toSignal, toObservable } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import {
  catchError,
  EMPTY,
  finalize,
  forkJoin,
  map,
  of,
  switchMap,
  timer,
  startWith,
  exhaustMap,
  Observable,
} from 'rxjs';
import { ClusterMaintenanceTemplateComponent } from '../../shared/templates/cluster-maintenance-template.component';
import {
  DataTableColumn,
  DataTableComponent,
  DataTableActionEvent,
  DataTableRow,
  DataTableRowAction,
  DataTableToggleEvent,
  DataTableSelectionAction,
  DataTableSelectionActionEvent,
  DataTableLayoutOption,
} from '../../shared/ui/data-table.component';
import { CategoryTabOption } from '../../shared/ui/category-tabs.component';
import { DetailFieldListComponent } from '../../shared/ui/detail-field-list.component';
import { nodeCapabilityCards, nodeCapabilityFields } from '../../shared/models/node-capabilities';
import { RecordFormActionsComponent } from '../../shared/ui/record-form-actions.component';
import { RecordFormComponent, RecordFormData } from '../../shared/ui/record-form.component';
import { NodeModeController } from '../../shared/services/node-mode-controller';
import { NotificationService } from '../../shared/services/notification.service';
import { DetailCard, DetailCardGridComponent } from '../../shared/ui/detail-card-grid.component';
import {
  LibraryNodeAccessFormComponent,
  LibraryNodeAccessOption,
  LibraryNodeAccessValue,
} from '../../shared/ui/library-node-access-form.component';
import {
  LibraryNodeAccessMode,
  LibraryNodeAccessResponse,
} from '../../shared/ui/library-node-access-types';
import { NodeGroupCardComponent } from './components/node-group-card.component';
import {
  NodeGroupEditorComponent,
  SaveNodeGroupPayload,
} from './components/node-group-editor.component';
import { NodeGroupDetailComponent } from './components/node-group-detail.component';
import { SystemLogViewerComponent } from '../../shared/ui/system-log-viewer/system-log-viewer.component';
import {
  PageShellContextService,
  PageShellSearchResult,
} from '../../core/layout/page-shell-context';

interface NodeLibraryRecord extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly isEnabled: boolean;
  readonly mode: LibraryNodeAccessMode;
  readonly modeLabel: string;
  readonly pathLabel: string;
}

interface PendingNodeEnrollmentResponse {
  readonly id: string;
  readonly name: string;
  readonly roles: readonly NodeRole[];
  readonly maxConcurrentTasks: number;
  readonly capabilities: readonly NodeToolCapability[];
  readonly createdAt: string;
  readonly expiresAt: string;
  readonly verificationCode?: string | null;
  readonly displayName?: string | null;
}

interface PendingNodeEnrollmentRecord extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly verificationCode: string;
  readonly rolesLabel: string;
  readonly capabilitiesLabel: string;
  readonly expiresAtLabel: string;
}

interface MemberCard {
  readonly id: string;
  readonly title: string;
  readonly subtitle: string;
  readonly statusLabel: string;
  readonly statusTone: StatusTone;
  readonly isMaster: boolean;
  readonly roles: readonly string[];
  readonly roleSwitches: readonly NodeOverviewRoleSwitch[];
  readonly admissionEnabled: boolean | null;
  readonly admissionDisabled: boolean;
  readonly hardware: NodeOverviewHardware;
  readonly taskCapacity: NodeOverviewTaskCapacity;
  readonly meta: string;
  readonly sections: readonly ResourceSheetSection[];
  readonly libraries: readonly NodeOverviewLibrary[];
}

interface NodeRecord extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly roleFilter: string;
  readonly roleLabel: string;
  readonly libraryAccess: string;
  readonly workerLoad: string;
  readonly queueStatus: string;
  readonly state: string;
  readonly lastSignal: string;
}

const NODE_ROLE_OPTIONS: readonly { readonly value: NodeRole; readonly label: string }[] = [
  { value: 'server', label: 'Serveur' },
  { value: 'worker', label: 'Worker' },
];

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-node-catalog-page',
  providers: [LocalNodeController],
  imports: [
    IconComponent,
    DataTableComponent,
    NodeOverviewCardComponent,
    DetailFieldListComponent,
    DetailCardGridComponent,
    ResourceSheetComponent,
    ClusterMaintenanceTemplateComponent,
    MatButtonModule,
    MatTabsModule,
    MatFormFieldModule,
    MatSelectModule,
    MatSlideToggleModule,
    RecordFormComponent,
    RecordFormActionsComponent,
    LibraryNodeAccessFormComponent,
    DrawerActionButtonComponent,
    GuidedSetupComponent,
    MatMenuModule,
    DatePipe,
    WeeklyScheduleFormComponent,
    NodeGroupCardComponent,
    NodeGroupEditorComponent,
    NodeGroupDetailComponent,
    SystemLogViewerComponent,
  ],
  templateUrl: './node-catalog-page.component.html',
  styleUrl: './node-catalog-page.component.scss',
})
export class NodeCatalogPageComponent {
  private readonly recordForms = viewChildren(RecordFormComponent);
  protected readonly actionRecordForm = computed(() =>
    this.recordForms().find((form) => form.embedded()),
  );
  protected readonly actionStep = computed(() => {
    const form = this.actionRecordForm();
    return form ? { current: form.stepIndex() + 1, total: form.stepCount() } : null;
  });

  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);
  private readonly pageShellContext = inject(PageShellContextService);
  private readonly http = inject(HttpClient);
  private readonly clusterApi = inject(ClusterApi);
  private readonly dialog = inject(MatDialog);
  private readonly notifications = inject(NotificationService);
  private readonly nodeMode = inject(NodeModeController);
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });
  private readonly nodes = signal<readonly NodeResponse[]>([]);
  protected readonly reconciliationStatus = signal<ClusterReconciliationHealthStatus | null>(null);
  protected readonly taskStatus = signal<ClusterTaskStatus | null>(null);
  protected readonly masterNodeId = signal<string | null>(null);
  private readonly pendingEnrollments = signal<readonly PendingNodeEnrollmentResponse[]>([]);
  private readonly allLibraries = signal<readonly LibraryResponse[]>([]);
  private readonly nodeAccessByLibrary = signal<
    ReadonlyMap<string, readonly LibraryNodeAccessResponse[]>
  >(new Map());
  private readonly nodeAccessActivationBusy = signal<ReadonlySet<string>>(new Set());
  private readonly libraryAccessLoadState = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly libraryAccessState = computed(() => this.libraryAccessLoadState());

  protected readonly isAdmin = inject(AuthService).isAdmin;
  protected readonly local = inject(LocalNodeController);
  protected readonly clusterIdentity = this.local.identity;
  protected readonly pageTab = signal(
    this.route.snapshot.queryParamMap.get('tab') === 'groups'
      ? 'groups'
      : this.route.snapshot.queryParamMap.get('tab') === 'cluster'
        ? 'cluster'
        : 'node',
  );
  protected readonly filtersOpen = signal(false);
  protected readonly admissionBusy = signal<ReadonlySet<string>>(new Set());
  protected readonly modeBusy = signal<ReadonlySet<string>>(new Set());
  protected readonly pageSearchQuery = computed(() =>
    this.pageTab() === 'groups' ? this.groupSearchQuery() : this.nodeSearchQuery(),
  );
  protected readonly managementTabs = NODE_MANAGEMENT_TABS;
  private readonly groups = signal<readonly NodeGroupResponse[]>([]);
  protected readonly groupState = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly groupMessage = signal('Chargement des groupes…');
  protected readonly groupFormOpen = signal(false);
  protected readonly groupBusy = signal(false);
  private readonly groupEditing = signal<NodeGroupResponse | null>(null);

  protected readonly createClusterForm = CREATE_CLUSTER_FORM;
  protected readonly autonomousCards = AUTONOMOUS_CARDS;
  protected readonly localNode = computed(
    () => this.nodes().find((node) => node.id === this.local.status()?.nodeId) ?? null,
  );
  protected readonly localOverviewHardware = computed(() =>
    this.nodeOverviewHardware(
      this.localNode()?.inventory ?? this.local.status()?.inventory ?? null,
    ),
  );
  protected readonly localName = computed(
    () =>
      this.localNode()?.displayName?.trim() ||
      this.local.status()?.displayName?.trim() ||
      this.local.status()?.name ||
      'Ce nœud',
  );
  /** Tâches actives du nœud local ; inconnues tant que la source n'a pas répondu. */
  protected readonly localActivity = toSignal(
    toObservable(computed(() => this.localNode()?.id ?? null)).pipe(
      switchMap((id) =>
        id
          ? timer(0, 5000).pipe(
              exhaustMap(() =>
                this.http
                  .get<NodeActivityResponse>('/api/v1/nodes/' + id + '/activity')
                  .pipe(catchError(() => of(null))),
              ),
            )
          : of(null),
      ),
    ),
    { initialValue: null },
  );
  protected readonly localTaskCapacity = computed(() => {
    const node = this.localNode();
    const status = this.local.status();
    const roles = node?.desiredRoles ?? node?.roles ?? status?.roles ?? null;
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
    else if (node && nodeLifecycle(node) === 'draining')
      note = 'Admission suspendue : les places libres ne sont pas attribuées.';

    return {
      accepted,
      remaining,
      maximum: workerEnabled === true ? maximum : null,
      note,
    };
  });
  protected readonly localSheet = computed(() =>
    localNodeSheet({
      status: this.local.status(),
      node: this.localNode(),
      mode: this.local.modeUnavailable() ? null : this.local.mode(),
      standalone: this.local.standalone(),
      clusterName: this.local.standalone() ? null : this.clusterName(),
      agentUnavailable: this.local.agentUnavailable(),
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
          label: NODE_LIFECYCLE_LABELS[nodeLifecycle(node)],
          tone: NODE_LIFECYCLE_TONES[nodeLifecycle(node)],
        }
      : {
          label: this.local.agentUnavailable() ? 'Agent injoignable' : 'En ligne',
          tone: this.local.agentUnavailable() ? ('warning' as const) : ('success' as const),
        };
  });
  protected readonly localOverviewRoles = computed(() =>
    (this.localNode()?.roles ?? this.local.status()?.roles ?? []).map(
      (role) => NODE_ROLE_LABELS[role],
    ),
  );
  protected readonly localRoleSwitches = computed<readonly NodeOverviewRoleSwitch[]>(() => {
    const mode = this.local.mode();
    const status =
      mode ??
      (this.local.status()
        ? {
            server: this.local.status()!.roles.includes('server'),
            worker: this.local.status()!.roles.includes('worker'),
            managed: false,
          }
        : null);
    if (!this.isAdmin() || !status || this.local.modeUnavailable()) return [];
    const requested = this.localNode()?.desiredRoles ?? null;
    const applied = this.local.status()?.roles ?? [];
    const current = (role: NodeRole) => requested?.includes(role) ?? status[role];
    return NODE_ROLE_OPTIONS.map(({ value: role, label }) => ({
      role,
      label,
      checked: current(role),
      pending: !!requested && requested.includes(role) !== applied.includes(role),
      disabled:
        !status.managed ||
        this.local.busy() ||
        this.local.modeBusy() ||
        this.local.agentUnavailable() ||
        (current(role) && !current(role === 'server' ? 'worker' : 'server')),
    }));
  });
  protected readonly localRoleControlNote = computed(() => {
    if (!this.isAdmin()) return '';
    if (this.local.modeUnavailable()) return 'État des fonctions indisponible.';
    return this.local.mode()?.managed === false
      ? 'Fonctions gérées par la configuration du déploiement.'
      : '';
  });
  protected readonly localAdmissionEnabled = computed(() => {
    const node = this.localNode();
    if (!this.isAdmin() || !node) return null;
    if (!(node.desiredRoles ?? node.roles).includes('worker')) return null;
    const lifecycle = nodeLifecycle(node);
    return lifecycle === 'quarantined' || lifecycle === 'incompatible'
      ? null
      : lifecycle !== 'draining';
  });
  protected readonly localAdmissionDisabled = computed(() => {
    const node = this.localNode();
    return !node || this.admissionBusy().has(node.id);
  });
  protected readonly localOverviewMeta = computed(() => {
    const lastSignal = formatLastSignal(this.localNode()?.lastSeenAt);
    return lastSignal ?? (this.local.agentUnavailable() ? 'Indisponible' : 'En attente');
  });
  protected readonly localOverviewLibraries = computed(() =>
    this.nodeOverviewLibraries(this.localNode()?.id ?? this.local.status()?.nodeId ?? null),
  );

  protected onLocalSheetAction(actionId: string): void {
    if (actionId === 'capabilities') {
      const node = this.localNode();
      if (node) this.openNodeCard(node.id);
    }
  }
  protected readonly clusterName = computed(
    () => this.clusterIdentity()?.name ?? this.local.status()?.clusterName ?? 'Cluster',
  );
  protected readonly clusterAddress = computed(
    () =>
      this.local.target()?.apiBaseUrl ??
      this.local.clusters().find((cluster) => cluster.id === this.local.status()?.clusterId)
        ?.apiBaseUrl ??
      null,
  );
  private readonly addressForm = computed(() => clusterAddressForm(this.local.address()));
  protected readonly joinSteps = computed(() =>
    clusterJoinSteps({
      addressForm: this.addressForm(),
      target: this.local.target(),
      status: this.local.status(),
      pairing: this.local.pairing(),
      clusters: this.local.joinableClusters(),
      localServerActive:
        this.local.mode()?.server ?? this.local.status()?.roles.includes('server') ?? false,
      canChoose: this.local.canJoin(),
    }),
  );
  protected readonly pageState = computed(() => {
    if (this.pageTab() === 'groups') return this.groupState();
    if (this.local.loading()) return 'loading';
    if (this.pageTab() === 'cluster' && !this.clusterIdentity()) return 'error';
    return this.pageTab() === 'cluster' && !this.local.standalone() ? this.loadState() : 'ready';
  });
  protected onManagementTabChange(tab: string): void {
    if (this.local.busy()) return;
    this.pageTab.set(tab);
    this.actionOpen.set(false);
    this.closeGroupForm();
    this.onDetailClosed();
  }
  protected focusEnrollmentRequests(): void {
    const behavior = window.matchMedia('(prefers-reduced-motion: reduce)').matches
      ? 'auto'
      : 'smooth';
    document
      .getElementById('node-enrollment-requests')
      ?.scrollIntoView({ behavior, block: 'start' });
  }
  protected onSetupAction(id: string): void {
    if (id === 'back') this.local.back();
    if (id === 'request') void this.local.sendRequest();
    if (id === 'later') this.local.closeFlow();
    if (id === 'cancel') void this.local.leave();
    if (id === 'finish') {
      this.local.closeFlow();
      this.pageTab.set('cluster');
    }
  }
  protected readonly memberSelectionActions: readonly DataTableSelectionAction[] = [
    { id: 'drain', label: 'Vidanger', visibleWhen: () => !this.bulkBusy() },
    { id: 'resume', label: 'Reprendre le travail', visibleWhen: () => !this.bulkBusy() },
    {
      id: 'delete',
      label: 'Supprimer',
      visibleWhen: () => !this.bulkBusy(),
      confirmation: (rows) => ({
        title: 'Supprimer ' + rows.length + ' nœud(s) ?',
        message:
          'Les accès seront révoqués ; les fichiers et l’historique sont conservés. Les nœuds avec des traitements actifs peuvent refuser le retrait.',
        confirmLabel: 'Supprimer les nœuds',
      }),
    },
  ];
  protected onMemberSelectionAction(event: DataTableSelectionActionEvent): void {
    if (!this.isAdmin() || this.bulkBusy()) return;
    const ids = event.rows.map((row) => String(row['id']));
    if (event.actionId === 'delete')
      this.runBulk(ids, (id) => this.http.delete<void>('/api/v1/nodes/' + id), 'suppression');
    else if (event.actionId === 'drain' || event.actionId === 'resume')
      this.runBulk(
        ids,
        (id) =>
          this.http.put<void>('/api/v1/nodes/' + id + '/draining', {
            draining: event.actionId === 'drain',
          }),
        event.actionId === 'drain' ? 'vidange' : 'reprise',
      );
  }
  protected readonly summaryTiles = computed<readonly SummaryTile[]>(() => {
    const nodes = this.nodes();
    const online = nodes.filter((node) => nodeLifecycle(node) === 'online');
    const capacity = online
      .filter((node) => node.roles.includes('worker'))
      .reduce((total, node) => total + node.maxConcurrentTasks, 0);
    const pending = this.pendingEnrollments().length;
    return [
      {
        label: 'Nœuds en ligne',
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
  protected readonly enrollmentWindow = signal<EnrollmentWindow>({
    isOpen: false,
    openUntil: null,
  });
  protected readonly firstLink = signal<FirstLinkAcceptance>({ isActive: false, expiresAt: null });
  protected readonly loadState = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly selectedRole = signal('all');
  protected readonly nodeSearchQuery = signal('');
  protected readonly actionOpen = signal(false);
  protected readonly actionType = signal<
    'enrollment' | 'library-access' | 'create-library' | 'identity'
  >('enrollment');
  protected readonly nodeLibraryLoading = signal(false);
  protected readonly accessFormBusy = signal(false);
  protected readonly selectedNodeLibrary = signal<LibraryResponse | null>(null);
  protected readonly selectedAvailableLibraryId = signal('');
  protected readonly enrollmentView = signal<'requests' | 'key' | 'review'>('requests');
  protected readonly selectedEnrollment = signal<PendingNodeEnrollmentResponse | null>(null);
  protected readonly enrollmentStep = signal(0);
  protected readonly enrollmentCodeConfirmed = signal(false);
  protected readonly enrollmentApprovalBusy = signal(false);
  protected readonly selectedNode = signal<NodeRecord | null>(null);
  protected readonly selectedNodeTabIndex = signal(0);
  protected readonly identityForm = signal<RecordFormData | null>(null);
  protected readonly createFormData: RecordFormData = {
    title: 'Ajouter un nœud',
    description:
      'Examinez les demandes des nœuds avant de les accepter. Si un nœud n’apparaît pas, générez une clé depuis sa page Nœuds (« Cluster puis Rejoindre un cluster ») et saisissez-la ici ; elle expire au bout de 10 minutes.',
    submitLabel: 'Accepter le nœud',
    reviewTitle: 'Vérifier le jumelage',
    fields: [
      {
        id: 'pairingKey',
        label: 'Clé générée sur le nœud',
        type: 'text',
        required: true,
        maxLength: 16,
      },
    ],
  };
  protected readonly title = computed(() => {
    if (this.pageTab() === 'groups') {
      return 'Groupes de nœuds';
    }
    if (this.pageTab() === 'cluster') {
      return !this.local.standalone() && this.clusterIdentity()
        ? this.clusterName()
        : ((this.routeData()['title'] as string) ?? 'Nœuds');
    }
    return this.localName() || ((this.routeData()['title'] as string) ?? 'Nœuds');
  });
  protected readonly catalogMessage = computed(() => {
    if (this.pageTab() === 'groups') return this.groupMessage();
    return this.loadState() === 'error'
      ? 'Impossible de joindre l’API du cluster. Vérifiez que le serveur et sa base de données sont disponibles.'
      : 'Chargement des nœuds du cluster…';
  });
  protected readonly reconciliationBanner = computed(() => {
    const serversCount = this.nodes().filter((node) => node.roles.includes('server')).length;
    return reconciliationBannerPresentation(
      this.reconciliationStatus(),
      serversCount,
      this.loadState() === 'ready',
      this.taskStatus(),
    );
  });
  protected readonly detailTitle = computed(() => {
    const raw = this.rawNode();
    return raw ? formatNodeLabel(raw.name, raw.displayName) : 'Détails du nœud';
  });
  protected readonly detailDescription = computed(
    () => this.selectedNode()?.roleLabel ?? 'État observé par le cluster',
  );

  protected readonly removalBusy = signal(false);
  protected readonly lifecycleBusy = signal(false);
  protected readonly lifecycleText = computed(() => {
    const node = this.rawNode();
    switch (node?.lifecycle) {
      case 'draining':
        return 'Vidange en cours : le nœud termine ses tâches et n’en reçoit plus de nouvelles.';
      case 'incompatible':
        return 'Version incompatible avec le cluster : ' + (node.lifecycleReason ?? '');
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
  protected readonly rawNode = computed(
    () => this.nodes().find((node) => node.id === this.selectedNode()?.id) ?? null,
  );
  protected readonly nodeActivity = toSignal(
    toObservable(computed(() => this.selectedNode()?.id ?? null)).pipe(
      switchMap((id) =>
        id
          ? timer(0, 5000).pipe(
              exhaustMap(() =>
                this.http
                  .get<NodeActivityResponse>('/api/v1/nodes/' + id + '/activity')
                  .pipe(catchError(() => of(null))),
              ),
              startWith(null),
            )
          : of(null),
      ),
    ),
    { initialValue: null },
  );
  protected readonly generalSheet = computed<{
    readonly title: string;
    readonly subtitle: string;
    readonly sections: readonly ResourceSheetSection[];
  } | null>(() => {
    const node = this.rawNode();
    if (!node) return null;

    const activity = this.nodeActivity();
    const inventory = node.inventory;
    const gpus = inventory?.gpus ?? [];
    const libraries = this.nodeLibraries().filter(
      (library) => library.isEnabled && library.mode !== 'unsupported',
    );
    const directLibraries = libraries.filter((library) => library.mode === 'direct').length;
    const transferLibraries = libraries.filter((library) => library.mode === 'transfer').length;
    const isWorker = node.roles.includes('worker');
    const lifecycle = nodeLifecycle(node);
    const roleBadges = node.roles.map((role) => ({
      label: NODE_ROLE_LABELS[role],
      tone: 'accent' as const,
    }));
    const labelBadges = Object.entries(node.labels ?? {})
      .filter((entry): entry is [string, string] => Boolean(entry[1]))
      .map(([key, value]) => ({ label: `${key}: ${value}`, tone: 'neutral' as const }));

    const sections: readonly ResourceSheetSection[] = [
      {
        id: 'node-general-identity',
        title: 'Identité et rôles',
        caption: 'Membre du cluster',
        items: [
          {
            label: node.displayName?.trim() || node.name,
            detail: node.desiredRoles?.length
              ? `Mode demandé : ${node.desiredRoles.map((role) => NODE_ROLE_LABELS[role]).join(' · ')} (en attente du prochain signal du nœud)`
              : undefined,
            badges: [
              {
                label: NODE_LIFECYCLE_LABELS[lifecycle],
                tone: NODE_LIFECYCLE_TONES[lifecycle],
              },
              ...roleBadges,
              ...labelBadges,
            ],
          },
        ],
        fields: [
          { label: 'Nom affiché', value: node.displayName?.trim() || node.name },
          { label: 'Nom technique', value: node.name },
          { label: 'Identifiant', value: node.id, mono: true },
        ],
      },
      {
        id: 'node-general-activity',
        title: 'Disponibilité et activité',
        caption: `Dernier signal · ${new Date(node.lastSeenAt).toLocaleString('fr-FR')}`,
        metrics: [
          {
            label: 'Tâches actives / limite',
            value: isWorker
              ? `${activity?.active ?? '—'} / ${this.nodeProcessingState()?.effectiveMaxConcurrentTasks ?? node.maxConcurrentTasks}`
              : 'Fonctions serveur',
            unavailable: isWorker && !activity,
          },
          {
            label: 'Réussies',
            value: activity?.succeeded.toString() ?? 'Indisponible',
            unavailable: !activity,
          },
          {
            label: 'Échecs',
            value: activity?.failed.toString() ?? 'Indisponible',
            unavailable: !activity,
          },
          {
            label: 'Annulations',
            value: activity?.cancelled.toString() ?? 'Indisponible',
            unavailable: !activity,
          },
          {
            label: 'Tâches alimentées par ce nœud',
            value: activity?.sourceTasks.toString() ?? 'Indisponible',
            unavailable: !activity,
          },
          {
            label: 'Dernière activité',
            value: activity
              ? activity.lastActivity
                ? new Date(activity.lastActivity).toLocaleString('fr-FR')
                : 'Aucune activité enregistrée'
              : 'Indisponible',
            unavailable: !activity,
          },
        ],
      },
      {
        id: 'node-general-system',
        title: 'Système et ressources',
        caption: inventory ? nodeHardwareLabel(node) : 'Inventaire non déclaré',
        metrics: inventory
          ? [
              {
                label: 'Processeur',
                value: inventory.processorModel?.trim()
                  ? tidyProcessorName(inventory.processorModel.trim())
                  : `${inventory.processorCount} cœurs`,
                detail: `${inventory.processorCount} cœurs · ${inventory.operatingSystem} · ${inventory.architecture}`,
              },
              {
                label: 'Mémoire totale disponible',
                value: formatMemory(inventory.memoryMb),
                detail: `${inventory.memoryMb} Mo`,
              },
              {
                label: 'Système',
                value: `${inventory.operatingSystem} · ${inventory.architecture}`,
              },
              {
                label: 'Environnement d’exécution',
                value: runtimeEnvironmentLabel(inventory.runtimeEnvironment),
              },
              {
                label: gpus.length > 1 ? 'Processeurs graphiques' : 'Processeur graphique',
                value: gpus.length
                  ? gpus.map((gpu) => gpu.name).join(' · ')
                  : inventory.gpus
                    ? 'Aucun détecté'
                    : 'Non déclaré',
                unavailable: !gpus.length,
                detail:
                  gpus
                    .map((gpu) =>
                      [gpu.vendor, gpu.device, gpu.driver ? `pilote ${gpu.driver}` : null]
                        .filter(Boolean)
                        .join(' · '),
                    )
                    .filter(Boolean)
                    .join(' ; ') || undefined,
              },
              {
                label: 'Accélérateurs',
                value: nodeAcceleratorBadges(node).join(' · '),
                detail:
                  nodeAcceleratorBadges(node)[0] === 'CPU'
                    ? 'Aucun accélérateur matériel vérifié : encodage sur CPU.'
                    : undefined,
              },
            ]
          : [
              {
                label: 'Matériel',
                value: 'Non déclaré',
                unavailable: true,
                detail: 'Disponible après le prochain signal du worker.',
              },
            ],
      },
      {
        id: 'node-general-libraries',
        title: 'Accès aux bibliothèques',
        caption: `${libraries.length} autorisée(s)`,
        metrics: [
          { label: 'Accès direct', value: `${directLibraries} bibliothèque(s)` },
          { label: 'Transfert WebDAV', value: `${transferLibraries} bibliothèque(s)` },
        ],
      },
    ];

    return {
      title: node.displayName?.trim() || node.name,
      subtitle: `${node.roles.map((role) => NODE_ROLE_LABELS[role]).join(' · ')} · ${NODE_LIFECYCLE_LABELS[lifecycle]}`,
      sections,
    };
  });
  protected readonly nodeProcessingState = toSignal(
    toObservable(computed(() => this.selectedNode()?.id ?? null)).pipe(
      switchMap((id) =>
        id
          ? timer(0, 5000).pipe(
              exhaustMap(() => this.clusterApi.nodeProcessing(id).pipe(catchError(() => of(null)))),
              startWith(null),
            )
          : of(null),
      ),
    ),
    { initialValue: null },
  );
  protected readonly processingCards = computed<readonly DetailCard[]>(() => {
    const node = this.rawNode();
    if (!node) return [];
    const settings = this.nodeProcessingState();
    const maximum = settings?.maxConcurrentTasks ?? null;
    const effectiveMaximum = settings?.effectiveMaxConcurrentTasks ?? node.maxConcurrentTasks;
    const schedule = settings?.schedule;
    const allowedPercent = schedule?.enabled
      ? Math.round(((schedule.slots.split('1').length - 1) * 100) / schedule.slots.length)
      : null;
    return [
      {
        id: 'node-processing-limit',
        title: 'Limite globale',
        subtitle: 'Traitements simultanés, toutes bibliothèques',
        icon: 'activity',
        status: {
          label: maximum == null ? `${effectiveMaximum} · annoncée` : `${maximum} · définie`,
          tone: maximum == null ? 'neutral' : 'accent',
        },
        tags: [],
        sections: [],
      },
      {
        id: 'node-processing-calendar',
        title: 'Calendrier',
        subtitle: 'Plages autorisées de démarrage',
        icon: 'system',
        status: {
          label: !schedule ? 'Chargement' : schedule.enabled ? 'Actif' : 'Désactivé',
          tone: schedule?.enabled ? 'success' : 'neutral',
        },
        tags: allowedPercent == null ? [] : [{ label: `${allowedPercent} % autorisé` }],
        sections: [],
      },
    ];
  });
  protected readonly processingForm = signal<RecordFormData | null>(null);
  protected readonly processingBusy = signal(false);
  private processingSettings: NodeProcessingSettings | null = null;
  private processingFormNodeId: string | null = null;
  protected readonly scheduleBusy = signal(false);
  private installProcessingSettingsEffect(): void {
    effect(() => {
      const node = this.rawNode();
      const settings = this.nodeProcessingState();
      if (!node || !settings) return;
      this.processingSettings = settings;
      if (this.processingFormNodeId === node.id) return;
      this.processingFormNodeId = node.id;
      this.processingForm.set(this.processingLimitForm(node, settings));
    });
  }

  private processingLimitForm(
    node: NodeResponse,
    settings: NodeProcessingSettings,
  ): RecordFormData {
    return {
      title: 'Limite globale du nœud',
      description:
        'Ce plafond concerne toutes ses bibliothèques. Les limites de bibliothèque et les limites par nœud/bibliothèque s’appliquent aussi.',
      submitLabel: 'Enregistrer',
      review: false,
      cancellable: false,
      fields: [
        {
          id: 'maximum',
          label: 'Fichiers traités simultanément, toutes bibliothèques',
          type: 'number',
          min: 1,
          max: 1000,
          step: 1,
          hint:
            'Vide : utiliser la limite locale annoncée par le worker (' +
            node.maxConcurrentTasks +
            ').',
        },
      ],
      initialValues: {
        maximum: settings.maxConcurrentTasks == null ? '' : String(settings.maxConcurrentTasks),
      },
    };
  }
  protected async saveNodeSchedule(schedule: NodeSchedule): Promise<void> {
    const node = this.rawNode();
    const settings = this.processingSettings;
    if (!node || !settings || this.scheduleBusy()) return;
    this.scheduleBusy.set(true);
    try {
      await firstValueFrom(
        this.clusterApi.saveNodeProcessing(
          node.id,
          settings.maxConcurrentTasks,
          settings.revision,
          schedule,
        ),
      );
      this.processingSettings = {
        ...settings,
        schedule,
        revision: settings.revision + 1,
      };
      this.notifications.show('Calendrier du nœud enregistré.');
    } catch {
      this.notifications.show('Calendrier invalide ou configuration modifiée ailleurs.');
    } finally {
      this.scheduleBusy.set(false);
    }
  }
  protected async saveNodeProcessing(values: Record<string, string>): Promise<void> {
    const node = this.rawNode();
    const settings = this.processingSettings;
    if (!node || !settings || this.processingBusy()) return;
    this.processingBusy.set(true);
    try {
      await firstValueFrom(
        this.clusterApi.saveNodeProcessing(
          node.id,
          values['maximum'] ? Number(values['maximum']) : null,
          settings.revision,
          settings.schedule,
        ),
      );
      const maximum = values['maximum'] ? Number(values['maximum']) : null;
      const updated: NodeProcessingSettings = {
        ...settings,
        maxConcurrentTasks: maximum,
        effectiveMaxConcurrentTasks: maximum ?? node.maxConcurrentTasks,
        revision: settings.revision + 1,
      };
      this.processingSettings = updated;
      this.processingForm.set(this.processingLimitForm(node, updated));
      this.notifications.show('Limite globale du nœud enregistrée.');
    } catch {
      this.notifications.show('Limite invalide ou configuration modifiée ailleurs.');
    } finally {
      this.processingBusy.set(false);
    }
  }

  private async editNodeIdentity(node: NodeRecord): Promise<void> {
    try {
      const identity = await firstValueFrom(
        this.http.get<{
          displayName: string | null;
          labels: Readonly<Record<string, string | null>>;
          effectiveLabels: Readonly<Record<string, string | null>>;
          groupIds: readonly string[];
        }>(`/api/v1/nodes/${node.id}/identity`),
      );
      if (this.selectedNode()?.id !== node.id) this.selectedNodeTabIndex.set(0);
      this.selectedNode.set(node);
      this.identityForm.set({
        title: 'Nom et étiquettes',
        description: `Groupes actuels : ${identity.groupIds.length}. Étiquettes effectives : ${
          Object.entries(identity.effectiveLabels)
            .map(([key, value]) => (value == null ? key : `${key}=${value}`))
            .join(', ') || 'aucune'
        }.`,
        submitLabel: 'Enregistrer',
        fields: [
          { id: 'displayName', label: 'Nom d’affichage', type: 'text', maxLength: 128 },
          {
            id: 'labels',
            label: 'Étiquettes locales',
            type: 'textarea',
            hint: 'Une par ligne : gpu=nvidia ou production.',
          },
        ],
        initialValues: {
          displayName: identity.displayName ?? '',
          labels: Object.entries(identity.labels)
            .map(([key, value]) => (value == null ? key : `${key}=${value}`))
            .join('\n'),
        },
      });
      this.actionType.set('identity');
      this.actionOpen.set(true);
    } catch {
      this.notifications.show('Impossible de charger l’identité du nœud.');
    }
  }

  protected async saveNodeIdentity(values: Record<string, string>): Promise<void> {
    const node = this.selectedNode();
    if (!node) return;
    const labels: Record<string, string | null> = {};
    for (const line of values['labels']
      .split(/[\n,]+/)
      .map((value) => value.trim())
      .filter(Boolean)) {
      const equals = line.indexOf('=');
      const key = (equals < 0 ? line : line.slice(0, equals)).trim();
      if (!/^[a-zA-Z][a-zA-Z0-9_.-]{0,63}$/.test(key)) {
        this.notifications.show('Clé d’étiquette invalide.');
        return;
      }
      labels[key] = equals < 0 ? null : line.slice(equals + 1).trim();
    }
    try {
      await firstValueFrom(
        this.http.put(`/api/v1/nodes/${node.id}/identity`, {
          displayName: values['displayName'] || null,
          labels,
        }),
      );
      this.nodes.update((nodes) =>
        nodes.map((item) =>
          item.id === node.id
            ? { ...item, displayName: values['displayName'] || null, labels }
            : item,
        ),
      );
      this.actionOpen.set(false);
      this.notifications.show('Identité du nœud enregistrée.');
    } catch {
      this.notifications.show('Les étiquettes ou le nom du nœud sont invalides.');
    }
  }
  protected readonly capabilityFields = computed(() =>
    nodeCapabilityFields(this.rawNode()?.capabilities ?? []),
  );
  protected readonly capabilityCards = computed(() =>
    nodeCapabilityCards(this.rawNode()?.capabilities ?? []),
  );
  protected readonly nodeActions: readonly DataTableRowAction[] = [
    { id: 'identity', label: 'Nom et étiquettes', visibleWhen: () => this.isAdmin() },
    {
      id: 'delete',
      label: 'Supprimer',
      tone: 'danger',
      visibleWhen: () => !this.removalBusy(),
      confirmation: (row) => ({
        title: 'Supprimer ce nœud ?',
        message:
          'Le nœud « ' +
          row['name'] +
          ' » sera retiré du cluster et ses accès révoqués. Les fichiers et l’historique seront conservés. Pour le rattacher de nouveau, son identité locale devra être renouvelée.',
        confirmLabel: 'Supprimer le nœud',
      }),
    },
  ];
  protected onNodeAction(event: DataTableActionEvent): void {
    if (event.actionId === 'identity') {
      const node = this.filteredNodes().find((item) => item.id === event.row['id']);
      if (node) void this.editNodeIdentity(node);
      return;
    }
    if (event.actionId !== 'delete' || this.removalBusy()) return;
    const id = String(event.row['id']);
    this.removalBusy.set(true);
    this.http
      .delete<void>('/api/v1/nodes/' + id)
      .pipe(
        catchError((error: { error?: { title?: string } }) => {
          this.notifications.show(error.error?.title ?? 'Impossible de supprimer ce nœud.');
          return EMPTY;
        }),
        finalize(() => this.removalBusy.set(false)),
      )
      .subscribe(() => {
        this.nodes.update((nodes) => nodes.filter((node) => node.id !== id));
        if (this.selectedNode()?.id === id) this.onDetailClosed();
        this.loadLibraryAccessSummary();
        this.notifications.show('Nœud supprimé du cluster.');
      });
  }
  protected clearQuarantine(): void {
    const node = this.rawNode();
    if (!node || this.lifecycleBusy()) return;
    this.sendLifecycle(this.http.post<void>('/api/v1/nodes/' + node.id + '/quarantine/clear', {}));
  }
  private sendLifecycle(request: Observable<void>): void {
    this.lifecycleBusy.set(true);
    request
      .pipe(
        catchError(() => {
          this.notifications.show('Impossible de modifier l’état du nœud.');
          return EMPTY;
        }),
        finalize(() => this.lifecycleBusy.set(false)),
      )
      .subscribe(() => {
        this.clusterApi
          .listNodes()
          .pipe(catchError(() => EMPTY))
          .subscribe((nodes) => this.nodes.set(nodes));
      });
  }
  protected confirmNodeRemoval(): void {
    const row = this.selectedNode();
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
  protected readonly actionTitle = computed(() =>
    this.actionType() === 'identity'
      ? 'Nom et étiquettes du nœud'
      : this.actionType() === 'library-access'
        ? `Accès à ${this.selectedNodeLibrary()?.name ?? 'la bibliothèque'}`
        : this.actionType() === 'create-library'
          ? 'Déclarer une bibliothèque'
          : this.createFormData.title,
  );
  protected readonly actionDescription = computed(() =>
    this.actionType() === 'identity'
      ? 'Le nom affiché est distinct du nom envoyé par heartbeat. Les étiquettes locales remplacent celles des groupes.'
      : this.actionType() === 'library-access'
        ? 'Renseignez le chemin local de cette bibliothèque pour ce nœud.'
        : this.actionType() === 'create-library'
          ? 'Le serveur crée la définition partagée. Vous pourrez ensuite autoriser les nœuds et configurer leurs chemins.'
          : this.createFormData.description,
  );
  protected readonly createLibraryFormData: RecordFormData = {
    title: 'Déclarer une bibliothèque',
    description:
      'Créez une définition partagée côté serveur. Cette création ne donne pas encore accès aux médias à ce nœud.',
    submitLabel: 'Déclarer la bibliothèque',
    fields: [{ id: 'name', label: 'Nom', type: 'text', required: true, maxLength: 128 }],
  };
  protected readonly nodeLibraryColumns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Bibliothèque', sortable: true },
    {
      key: 'isEnabled',
      label: 'Accès',
      toggle: {
        showStatusLabel: false,
        ariaLabel: (row, checked) =>
          `${checked ? 'Désactiver' : row['mode'] === 'unsupported' ? 'Configurer' : 'Activer'} l’accès à ${row['name']} sur ce nœud`,
        disabledWhen: (row) =>
          this.nodeAccessActivationBusy().has(`${this.selectedNode()?.id}:${row['id']}`),
        confirmation: (row, checked) =>
          checked
            ? undefined
            : {
                title: 'Désactiver cette bibliothèque sur ce nœud ?',
                message: `« ${row['name']} » restera déclarée et ses accès sur les autres nœuds ne changeront pas.`,
                confirmLabel: 'Désactiver sur ce nœud',
              },
      },
    },
    { key: 'modeLabel', label: 'Mode autorisé', sortable: true },
    { key: 'pathLabel', label: 'Chemin sur ce nœud' },
  ];
  protected readonly nodeLibraryActions: readonly DataTableRowAction[] = [
    { id: 'edit', label: 'Modifier' },
    {
      id: 'delete',
      label: 'Supprimer',
      tone: 'danger',
      confirmation: (row) => ({
        title: 'Retirer cette bibliothèque du nœud ?',
        message: `L’accès de ce nœud à « ${row['name']} » sera supprimé. La bibliothèque et ses accès sur les autres nœuds resteront inchangés.`,
        confirmLabel: 'Retirer du nœud',
        variant: 'danger',
      }),
    },
  ];
  private nodeOverviewLibraries(nodeId: string | null): readonly NodeOverviewLibrary[] {
    if (!nodeId) return [];
    return this.allLibraries().flatMap((library) => {
      const access = this.nodeAccessByLibrary()
        .get(library.id)
        ?.find((item) => item.nodeId === nodeId);
      if (!access) return [];

      const stateLabel = !library.isActive
        ? 'Désactivée globalement'
        : access.mode === 'unsupported'
          ? 'Non pris en charge'
          : access.isEnabled
            ? 'Active'
            : 'Désactivée sur ce nœud';
      const stateTone: StatusTone = !library.isActive
        ? 'neutral'
        : access.mode === 'unsupported' || !access.isEnabled
          ? 'warning'
          : 'success';
      const modeLabel =
        access.mode === 'direct'
          ? 'Accès direct'
          : access.mode === 'transfer'
            ? 'Transfert WebDAV'
            : 'Aucun accès configuré';
      const accessLabel =
        [access.canRead ? 'Lecture' : null, access.canWrite ? 'Écriture' : null]
          .filter((item): item is string => item !== null)
          .join(' · ') || 'Aucun droit déclaré';

      return [
        {
          id: library.id,
          name: library.name,
          path:
            access.localPath ??
            (access.mode === 'transfer'
              ? 'Espace temporaire du worker'
              : access.mode === 'unsupported'
                ? 'Aucun accès actif'
                : 'Chemin à configurer'),
          stateLabel,
          stateTone,
          modeLabel,
          accessLabel,
        },
      ];
    });
  }
  protected readonly nodeLibraries = computed<readonly NodeLibraryRecord[]>(() => {
    const node = this.selectedNode();
    if (!node) return [];
    return this.allLibraries().flatMap((library) => {
      const access = this.nodeAccessByLibrary()
        .get(library.id)
        ?.find((item) => item.nodeId === node.id);
      if (!access) return [];
      const localMode =
        access.mode === 'direct'
          ? 'Accès direct'
          : access.mode === 'transfer'
            ? 'Transfert worker'
            : 'Aucun accès configuré';
      return [
        {
          id: library.id,
          name: library.name,
          isEnabled: access.isEnabled,
          mode: access.mode,
          modeLabel: `${localMode}${access.mode !== 'unsupported' && !access.isEnabled ? ' · Désactivée sur ce nœud' : ''}${library.isActive ? '' : ' · Désactivée globalement'}`,
          pathLabel:
            access.localPath ??
            (access.mode === 'transfer'
              ? 'Espace temporaire du worker'
              : access.mode === 'unsupported'
                ? 'Aucun accès actif'
                : 'Chemin à configurer'),
        },
      ];
    });
  });
  protected readonly availableLibraries = computed(() => {
    const allowedIds = new Set(this.nodeLibraries().map((library) => library.id));
    return this.allLibraries().filter((library) => library.isActive && !allowedIds.has(library.id));
  });
  protected readonly nodeLibraryRows = computed<readonly DataTableRow[]>(() =>
    this.nodeLibraries(),
  );
  protected readonly selectedNodeLibraryAccess = computed(() => {
    const nodeId = this.selectedNode()?.id;
    const libraryId = this.selectedNodeLibrary()?.id;
    return nodeId && libraryId
      ? (this.nodeAccessByLibrary()
          .get(libraryId)
          ?.find((item) => item.nodeId === nodeId) ?? null)
      : null;
  });

  protected readonly columns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Nom du nœud', sortable: true },
    { key: 'roleLabel', label: 'Rôle', sortable: true },
    { key: 'libraryAccess', label: 'Accès aux bibliothèques', sortable: true },
    { key: 'workerLoad', label: 'Traitements', sortable: true },
    { key: 'state', label: 'État', sortable: true },
    { key: 'lastSignal', label: 'Dernier signal', sortable: true },
  ];

  protected readonly roleOptions = computed<readonly CategoryTabOption[]>(() => {
    const nodes = this.nodes();
    return [
      { value: 'all', label: 'Tous', count: nodes.length },
      ...NODE_ROLE_OPTIONS.map((role) => ({
        ...role,
        count: nodes.filter((node) => node.roles.includes(role.value)).length,
      })),
    ];
  });

  private readonly filteredRawNodes = computed(() => {
    const role = this.selectedRole();
    const query = this.nodeSearchQuery().trim().toLocaleLowerCase('fr');
    return this.nodes()
      .filter((node) => role === 'all' || node.roles.includes(role as NodeRole))
      .filter(
        (node) =>
          !query ||
          [node.name, node.displayName ?? '', node.id].some((value) =>
            value.toLocaleLowerCase('fr').includes(query),
          ),
      )
      .filter(
        (node) => this.selectedState() === 'all' || this.stateGroup(node) === this.selectedState(),
      )
      .sort(
        (a, b) =>
          (this.stateGroup(a) === 'attention' ? -1 : 0) -
            (this.stateGroup(b) === 'attention' ? -1 : 0) || a.name.localeCompare(b.name, 'fr'),
      );
  });
  protected readonly filteredNodes = computed(() =>
    this.filteredRawNodes().map((node) => this.toRecord(node)),
  );

  /** Cartes de synthèse des membres du cluster, avec actions secondaires dans le menu. */
  protected readonly memberCards = computed<readonly MemberCard[]>(() =>
    this.filteredRawNodes().map((node) => {
      const lifecycle = nodeLifecycle(node);
      const record = this.toRecord(node);
      const isMaster = node.id === this.masterNodeId();
      return {
        id: node.id,
        title: formatNodeLabel(node.name, node.displayName),
        subtitle: isMaster
          ? `Coordinateur actif de ${this.clusterName()}`
          : `Membre de ${this.clusterName()}`,
        statusLabel: NODE_LIFECYCLE_LABELS[lifecycle],
        statusTone: NODE_LIFECYCLE_TONES[lifecycle],
        isMaster,
        roles: node.roles.map((role) => NODE_ROLE_LABELS[role]),
        roleSwitches: this.memberRoleSwitches(node),
        admissionEnabled: this.admissionEnabledFor(node),
        admissionDisabled: this.admissionBusy().has(node.id),
        hardware: this.nodeOverviewHardware(node.inventory ?? null),
        taskCapacity: this.memberTaskCapacity(node),
        meta: record.lastSignal,
        sections: memberNodeOverviewSections(
          node,
          this.activityKnown() && node.status === 'online'
            ? (this.activeTasks()[node.id] ?? 0)
            : null,
          this.clusterName(),
        ).filter((section) => section.id !== 'state' && section.id !== 'hardware'),
        libraries: this.nodeOverviewLibraries(node.id),
      };
    }),
  );
  private nodeOverviewHardware(inventory: NodeInventory | null): NodeOverviewHardware {
    const operatingSystem = inventory?.operatingSystem?.trim() ?? '';
    const runtime = inventory?.runtimeEnvironment?.trim().toLowerCase() ?? '';
    const os = operatingSystem.toLowerCase();
    const platform: NodeOverviewPlatform =
      runtime === 'docker'
        ? 'docker'
        : os.includes('windows')
          ? 'windows'
          : os.includes('linux')
            ? 'linux'
            : 'generic';
    const platformLabel =
      platform === 'docker' ? 'Docker · conteneur' : operatingSystem || 'Plateforme non déclarée';
    const cleanGpuName = (raw: string): string => {
      let g = raw
        .replace(/Advanced Micro Devices, Inc\. \[AMD\/ATI\]/gi, 'AMD')
        .replace(/Advanced Micro Devices, Inc\./gi, 'AMD')
        .replace(/Intel Corporation\s*/gi, 'Intel ')
        .replace(/NVIDIA Corporation\s*/gi, 'NVIDIA ')
        .replace(/\([Rr]\)|\([Tt][Mm]\)/g, '')
        .replace(/\s+/g, ' ')
        .trim();
      const match = /\[(.*?)\]/.exec(g);
      if (match && match[1].length > 3) {
        const brand = g.startsWith('AMD')
          ? 'AMD '
          : g.startsWith('Intel')
            ? 'Intel '
            : g.startsWith('NVIDIA')
              ? 'NVIDIA '
              : '';
        if (!match[1].startsWith(brand.trim())) {
          return `${brand}${match[1]}`.trim();
        }
        return match[1].trim();
      }
      return g;
    };

    const gpu = inventory?.gpus?.length
      ? inventory.gpus.map((item) => cleanGpuName(item.name)).join(' · ')
      : inventory?.gpus
        ? 'Aucun détecté'
        : 'Non déclaré';

    const rawCpu = inventory?.processorModel?.trim() || '';
    const cores = inventory?.processorCount;
    let cpu = 'Non déclaré';
    if (rawCpu) {
      cpu = rawCpu
        .replace(/\([Rr]\)|\([Tt][Mm]\)/g, '')
        .replace(/@\s*[\d.]+\s*GHz/gi, '')
        .replace(/\s+CPU\b/g, '')
        .replace(/\s+/g, ' ')
        .trim();
      if (cores && !cpu.toLowerCase().includes('cœur') && !cpu.toLowerCase().includes('core')) {
        cpu += ` (${cores} cœurs)`;
      }
    } else if (cores) {
      cpu = `${cores} cœurs`;
    }

    return {
      cpu,
      memory: inventory ? formatMemory(inventory.memoryMb) : 'Non déclarée',
      gpu,
      platform,
      platformLabel,
    };
  }
  private memberTaskCapacity(node: NodeResponse): NodeOverviewTaskCapacity {
    const workerEnabled = node.roles.includes('worker');
    const accepted = this.activityKnown() ? (this.activeTasks()[node.id] ?? 0) : null;
    const maximum = workerEnabled ? node.maxConcurrentTasks : null;
    const remaining =
      workerEnabled && accepted !== null && maximum !== null
        ? Math.max(0, maximum - accepted)
        : null;
    let note = '';
    if (!workerEnabled) note = 'Fonction Worker désactivée.';
    else if (accepted === null) note = 'Comptage des tâches indisponible.';
    else if (nodeLifecycle(node) === 'draining')
      note = 'Admission suspendue : les places libres ne sont pas attribuées.';
    else if (nodeLifecycle(node) !== 'online') note = 'Nœud indisponible pour recevoir des tâches.';

    return { accepted, remaining, maximum, note };
  }
  protected memberRoleSwitches(node: NodeResponse): readonly NodeOverviewRoleSwitch[] {
    if (!this.isAdmin()) return [];
    const requested = node.desiredRoles ?? node.roles;
    const roleEnabled = (role: NodeRole) => requested.includes(role);
    return NODE_ROLE_OPTIONS.map(({ value: role, label }) => ({
      role,
      label,
      checked: roleEnabled(role),
      pending:
        !!node.desiredRoles && node.desiredRoles.includes(role) !== node.roles.includes(role),
      disabled:
        this.modeBusy().has(node.id) ||
        (roleEnabled(role) && !roleEnabled(role === 'server' ? 'worker' : 'server')),
    }));
  }
  protected admissionEnabledFor(node: NodeResponse): boolean | null {
    if (!this.isAdmin()) return null;
    if (!(node.desiredRoles ?? node.roles).includes('worker')) return null;
    const lifecycle = nodeLifecycle(node);
    if (lifecycle === 'quarantined' || lifecycle === 'incompatible') return null;
    return lifecycle !== 'draining';
  }
  protected admissionDisabledFor(node: NodeResponse): boolean {
    return this.admissionBusy().has(node.id);
  }
  protected setMemberRole(nodeId: string, role: NodeRole, enabled: boolean): void {
    const node = this.nodes().find((item) => item.id === nodeId);
    if (node) void this.updateMemberRole(node, role, enabled);
  }
  private async updateMemberRole(
    node: NodeResponse,
    role: NodeRole,
    enabled: boolean,
  ): Promise<void> {
    const roles = node.desiredRoles ?? node.roles;
    const current = { server: roles.includes('server'), worker: roles.includes('worker') };
    if (current[role] === enabled || this.modeBusy().has(node.id)) return;
    this.modeBusy.update((busy) => new Set(busy).add(node.id));
    try {
      if (
        await this.nodeMode.requestRole(
          node.id,
          node.displayName?.trim() || node.name,
          current,
          role,
          enabled,
          node.status === 'online',
        )
      ) {
        const nodes = await firstValueFrom(this.clusterApi.listNodes());
        this.nodes.set(nodes);
      }
    } catch {
      this.notifications.show('Impossible d’actualiser les fonctions du nœud.');
    } finally {
      this.modeBusy.update((busy) => {
        const next = new Set(busy);
        next.delete(node.id);
        return next;
      });
    }
  }
  protected setMemberAdmission(nodeId: string, enabled: boolean): void {
    const node = this.nodes().find((item) => item.id === nodeId);
    if (node) void this.setNodeAdmission(node, enabled);
  }
  protected setLocalAdmission(enabled: boolean): void {
    const node = this.localNode();
    if (node) void this.setNodeAdmission(node, enabled);
  }
  protected setSelectedNodeAdmission(enabled: boolean): void {
    const node = this.rawNode();
    if (node) void this.setNodeAdmission(node, enabled);
  }
  private async setNodeAdmission(node: NodeResponse, enabled: boolean): Promise<void> {
    const active = this.admissionEnabledFor(node);
    if (active === null || active === enabled || this.admissionBusy().has(node.id)) return;
    if (!enabled) {
      const confirmed = await firstValueFrom(
        this.dialog
          .open<ConfirmationDialogComponent, unknown, boolean>(ConfirmationDialogComponent, {
            data: {
              compact: true,
              variant: 'danger',
              eyebrow: 'Traitements du nœud',
              title: `Suspendre les nouvelles tâches sur « ${node.displayName?.trim() || node.name} » ?`,
              message:
                'Aucune nouvelle tâche ne sera affectée à ce nœud. Les traitements déjà en cours ne sont pas interrompus et pourront se terminer normalement.',
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
        this.http.put<void>(`/api/v1/nodes/${node.id}/draining`, { draining: !enabled }),
      );
      const nodes = await firstValueFrom(this.clusterApi.listNodes());
      this.nodes.set(nodes);
      this.notifications.show(
        enabled
          ? `Le nœud « ${node.displayName?.trim() || node.name} » peut de nouveau recevoir des tâches.`
          : `Les nouvelles tâches sont suspendues sur « ${node.displayName?.trim() || node.name} ».`,
      );
    } catch (error) {
      this.notifications.show(
        (error as { error?: { title?: string } } | null)?.error?.title ??
          'Impossible de modifier l’admission des tâches sur ce nœud.',
      );
    } finally {
      this.admissionBusy.update((busy) => {
        const next = new Set(busy);
        next.delete(node.id);
        return next;
      });
    }
  }
  protected readonly memberActions = computed<readonly ResourceCardAction[]>(() =>
    this.isAdmin()
      ? [
          { id: 'identity', label: 'Nom et étiquettes' },
          { id: 'promote', label: 'Promouvoir en Maître' },
          ...(this.removalBusy()
            ? []
            : [{ id: 'delete', label: 'Supprimer', tone: 'danger' as const }]),
        ]
      : [],
  );
  protected readonly emptySelection: ReadonlySet<string> = new Set();
  protected readonly selectedMemberIds = signal<ReadonlySet<string>>(new Set());
  protected toggleMember(id: string, checked: boolean): void {
    this.selectedMemberIds.update((current) => {
      const next = new Set(current);
      if (checked) next.add(id);
      else next.delete(id);
      return next;
    });
  }
  protected onMemberCardAction(id: string, actionId: string): void {
    const row = this.filteredNodes().find((item) => item.id === id);
    if (!row) return;
    if (actionId === 'promote') {
      this.promoteNodeToMaster(row);
      return;
    }
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

  protected promoteNodeToMaster(row: NodeRecord): void {
    const fullNode = this.nodes().find((n) => n.id === row.id);
    const localNode = this.localNode();
    this.dialog.open(LeadershipTransitionDialogComponent, {
      data: {
        targetNodeId: row.id,
        targetNodeName: row.name,
        currentNodeName: localNode?.name || 'Ce nœud',
        targetNodeHost: fullNode?.name || row.name,
      },
      disableClose: true,
      width: '540px',
    });
  }
  protected runMemberBulk(actionId: 'drain' | 'resume' | 'delete'): void {
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

  /** Cartes de nœuds : ce qui demande une action d'abord, puis par nom (architecture 0046). */
  /** Tâches actives par nœud (clé : identifiant), rafraîchies avec la liste. */
  private readonly activeTasks = signal<Readonly<Record<string, number>>>({});
  protected readonly bulkBusy = signal(false);
  protected readonly selectedState = signal('all');
  /** Regroupe les états de cycle de vie : en ligne, hors ligne, ou à traiter (vidange, incompatible, quarantaine). */
  private stateGroup(node: NodeResponse): 'online' | 'offline' | 'attention' {
    const lifecycle = node.lifecycle ?? node.status;
    return lifecycle === 'online' ? 'online' : lifecycle === 'offline' ? 'offline' : 'attention';
  }
  protected readonly stateOptions = computed<readonly CategoryTabOption[]>(() => {
    const nodes = this.nodes();
    const count = (group: string) => nodes.filter((node) => this.stateGroup(node) === group).length;
    return [
      { value: 'all', label: 'Tous', count: nodes.length },
      { value: 'online', label: 'En ligne', count: count('online') },
      { value: 'attention', label: 'À traiter', count: count('attention') },
      { value: 'offline', label: 'Hors ligne', count: count('offline') },
    ];
  });

  protected editSelectedNodeIdentity(): void {
    const node = this.selectedNode();
    if (node) void this.editNodeIdentity(node);
  }

  private readonly activityLoaded = signal(false);
  private activityKnown(): boolean {
    return this.activityLoaded();
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
            ? `${label} : ${results.length - failed} nœud(s) traité(s), ${failed} en échec.`
            : `${label} : ${results.length} nœud(s) traité(s).`,
        );
        this.clusterApi
          .listNodes()
          .pipe(catchError(() => EMPTY))
          .subscribe((nodes) => this.nodes.set(nodes));
      });
  }

  protected openNodeCard(id: string): void {
    const node = this.nodes().find((node) => node.id === id);
    this.selectedNode.set(node ? this.toRecord(node) : null);
  }

  protected readonly enrollmentColumns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Nœud détecté', sortable: true },
    { key: 'verificationCode', label: 'Code à comparer', sortable: false },
    { key: 'rolesLabel', label: 'Rôle annoncé', sortable: true },
    { key: 'capabilitiesLabel', label: 'Capacités', sortable: true },
    { key: 'expiresAtLabel', label: 'Expiration', sortable: true },
  ];
  protected readonly enrollmentLayoutOptions: readonly DataTableLayoutOption[] = [
    { value: 'resources', label: 'Cartes' },
    { value: 'table', label: 'Tableau' },
  ];
  protected readonly enrollmentSteps = computed<readonly GuidedStep[]>(() => {
    const request = this.selectedEnrollment();
    if (!request) return [];

    const cardTitle = formatNodeLabel(request.name, request.displayName);
    const card: DetailCard = {
      id: `enrollment-${request.id}`,
      title: cardTitle,
      subtitle: 'Nœud demandeur',
      icon: 'server',
      status: { label: 'En attente', tone: 'warning' },
      tags: request.roles.map((role) => ({ label: NODE_ROLE_LABELS[role], tone: 'accent' })),
      sections: [
        {
          title: 'Configuration annoncée',
          items: [
            ...(request.displayName && request.name && request.displayName !== request.name
              ? [{ label: 'Nom d’hôte', description: request.name }]
              : []),
            {
              label: 'Capacités',
              description: this.capabilitiesLabel(request.capabilities),
            },
            {
              label: 'Exécution simultanée',
              description: `${request.maxConcurrentTasks} tâche(s)`,
            },
            {
              label: 'Demande valable jusqu’au',
              description: this.formatExpiryTime(request.expiresAt),
            },
          ],
        },
      ],
    };
    const codeCard: DetailCard = {
      id: `enrollment-code-${request.id}`,
      title: 'Cluster d’accueil',
      subtitle: 'Adresse de référence annoncée par ce cluster',
      icon: 'server',
      tags: [],
      sections: [
        {
          title: 'Vérification à deux écrans',
          items: [
            {
              label: 'Adresse du cluster',
              description: this.clusterAddress() ?? 'Cluster actuel',
            },
          ],
        },
      ],
    };
    return [
      {
        id: 'request',
        label: 'Demande',
        description: 'Vérifiez le nœud et les fonctions qu’il annonce au cluster.',
        cards: [card],
        actions: [
          { id: 'cancel', label: 'Annuler' },
          { id: 'continue', label: 'Continuer', primary: true },
        ],
      },
      {
        id: 'verification',
        label: 'Vérification',
        description: 'Le code doit être identique sur les deux écrans.',
        heroCode: request.verificationCode
          ? {
              code: request.verificationCode,
              label: 'Code de vérification',
              helper: 'Comparez avec le code affiché sur le nœud demandeur.',
              badge: 'Vérification requise',
              tone: 'warning',
            }
          : undefined,
        cards: [codeCard],
        note: request.verificationCode
          ? 'Confirmez uniquement si le code correspond exactement sur les deux nœuds.'
          : 'Aucun code de vérification n’est disponible. Cette demande ne peut pas être approuvée ; actualisez les demandes et réessayez.',
        acknowledgement: {
          label: 'J’ai comparé les deux écrans et les codes sont identiques.',
        },
        actions: [
          { id: 'back', label: 'Précédent' },
          {
            id: 'continue',
            label: 'Continuer',
            primary: true,
            disabled: !request.verificationCode,
            requiresAcknowledgement: true,
          },
        ],
      },
      {
        id: 'approval',
        label: 'Approbation',
        description: `Le nœud rejoindra le cluster ${this.clusterName()} avec les rôles et capacités annoncés.`,
        cards: [card],
        note: 'Après approbation, le nœud apparaît parmi les membres dès qu’il confirme sa connexion.',
        actions: [
          { id: 'back', label: 'Précédent' },
          { id: 'approve', label: 'Approuver le rattachement', primary: true },
        ],
      },
    ];
  });
  protected readonly enrollmentRows = computed<readonly PendingNodeEnrollmentRecord[]>(() =>
    this.pendingEnrollments().map((request) => ({
      id: request.id,
      name: formatNodeLabel(request.name, request.displayName),
      verificationCode: request.verificationCode ?? '—',
      rolesLabel: request.roles.map((role) => NODE_ROLE_LABELS[role]).join(' · '),
      capabilitiesLabel: request.capabilities.length
        ? request.capabilities.map((item) => `${item.toolId} ${item.version}`).join(' · ')
        : 'Aucun outil déclaré',
      expiresAtLabel: this.formatExpiry(request.expiresAt),
    })),
  );

  constructor() {
    this.destroyRef.onDestroy(
      this.pageShellContext.register({
        searchPlaceholder: 'Rechercher dans les nœuds…',
        query: this.pageSearchQuery,
        onSearchQueryChange: (query) => {
          if (this.pageTab() === 'groups') this.groupSearchQuery.set(query);
          else this.nodeSearchQuery.set(query);
        },
        searchResults: (query) => this.pageSearchResults(query),
        filter: {
          label: 'Filtres',
          visibleWhen: () => this.pageTab() === 'cluster' && !this.local.standalone(),
          activeCount: () => this.activeFilters().length,
          toggle: () => this.filtersOpen.update((open) => !open),
        },
      }),
    );
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe((params) => {
      const tab = params.get('tab');
      if (tab === 'groups' || tab === 'cluster' || tab === 'node') {
        this.pageTab.set(tab);
      }
    });
    void this.loadGroups();
    this.installProcessingSettingsEffect();
    this.loadLibraryAccessSummary();
    timer(0, 10_000)
      .pipe(
        switchMap(() =>
          forkJoin({
            nodes: this.clusterApi.listNodes(),
            reconciliation: this.clusterApi.reconciliationStatus().pipe(catchError(() => of(null))),
            tasks: this.clusterApi.taskStatus().pipe(catchError(() => of(null))),
            leadership: this.clusterApi.leadershipStatus().pipe(catchError(() => of(null))),
            pending: this.http.get<readonly PendingNodeEnrollmentResponse[]>(
              '/api/v1/nodes/enrollment-requests',
            ),
            activity: this.http
              .get<Readonly<Record<string, number>>>('/api/v1/nodes/activity')
              .pipe(catchError(() => of(null))),
          }).pipe(
            catchError(() => {
              this.loadState.set(this.nodes().length ? 'ready' : 'error');
              return EMPTY;
            }),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe(({ nodes, reconciliation, tasks, leadership, pending, activity }) => {
        if (activity) {
          this.activeTasks.set(activity);
          this.activityLoaded.set(true);
        }
        if (leadership?.masterNodeId) {
          this.masterNodeId.set(leadership.masterNodeId);
        }
        this.nodes.set(nodes);
        this.reconciliationStatus.set(reconciliation);
        this.taskStatus.set(tasks);
        this.pendingEnrollments.set(pending);
        this.loadState.set('ready');
      });
  }

  protected readonly activeFilters = computed(() => {
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

  protected clearNodeFilters(): void {
    this.selectedRole.set('all');
    this.selectedState.set('all');
  }

  protected onRoleChange(role: string): void {
    this.selectedRole.set(role);
  }

  private pageSearchResults(query: string): readonly PageShellSearchResult[] {
    const normalized = query.trim().toLocaleLowerCase('fr');
    if (!normalized) return [];
    const matches = (...values: readonly string[]) =>
      values.some((value) => value.toLocaleLowerCase('fr').includes(normalized));
    if (this.pageTab() === 'groups') {
      return this.groups()
        .filter((group) => matches(group.name, group.description))
        .slice(0, 6)
        .map((group) => ({
          id: `node-group:${group.id}`,
          label: group.name,
          description: group.description || 'Groupe de nœuds',
          group: 'Groupes de nœuds',
          glyph: '▦',
          run: () => this.openGroupDetail(group),
        }));
    }
    if (this.pageTab() === 'cluster') {
      return this.nodes()
        .filter((node) =>
          matches(
            node.name,
            node.displayName ?? '',
            node.id,
            node.roles.map((role) => NODE_ROLE_LABELS[role]).join(' '),
          ),
        )
        .slice(0, 6)
        .map((node) => ({
          id: `node:${node.id}`,
          label: formatNodeLabel(node.name, node.displayName),
          description: `${NODE_LIFECYCLE_LABELS[nodeLifecycle(node)]} · ${node.roles.map((role) => NODE_ROLE_LABELS[role]).join(' · ')}`,
          group: 'Nœuds du cluster',
          glyph: '▤',
          run: () => this.openNodeCard(node.id),
        }));
    }
    const local = this.localNode();
    return local && matches(local.name, local.displayName ?? '', local.id)
      ? [
          {
            id: `node:${local.id}`,
            label: formatNodeLabel(local.name, local.displayName),
            description: 'Ce nœud',
            group: 'Nœud local',
            glyph: '▤',
            run: () => this.pageTab.set('node'),
          },
        ]
      : [];
  }

  protected addNode(): void {
    this.actionType.set('enrollment');
    this.enrollmentView.set('requests');
    this.selectedEnrollment.set(null);
    this.enrollmentStep.set(0);
    this.enrollmentCodeConfirmed.set(false);
    this.actionOpen.set(true);
    this.refreshEnrollmentWindow();
  }

  private refreshEnrollmentWindow(): void {
    this.clusterApi
      .enrollmentWindow()
      .pipe(catchError(() => EMPTY))
      .subscribe((window) => this.enrollmentWindow.set(window));
    this.clusterApi
      .firstLinkAcceptance()
      .pipe(catchError(() => EMPTY))
      .subscribe((firstLink) => this.firstLink.set(firstLink));
  }

  protected enableFirstLink(): void {
    this.clusterApi
      .enableFirstLinkAcceptance(10)
      .pipe(
        catchError(() => {
          this.notifications.show('Le mode « première liaison » n’a pas pu être activé.');
          return EMPTY;
        }),
      )
      .subscribe((firstLink) => {
        this.firstLink.set(firstLink);
        this.notifications.show(
          'La prochaine demande de rattachement sera acceptée sans vérification (10 minutes).',
        );
      });
  }

  protected disableFirstLink(): void {
    this.clusterApi
      .disableFirstLinkAcceptance()
      .pipe(
        catchError(() => {
          this.notifications.show('Le mode « première liaison » n’a pas pu être désactivé.');
          return EMPTY;
        }),
      )
      .subscribe(() => {
        this.firstLink.set({ isActive: false, expiresAt: null });
        this.notifications.show('Le mode « première liaison » est désactivé.');
      });
  }

  protected openEnrollmentWindow(): void {
    this.clusterApi
      .openEnrollmentWindow(10)
      .pipe(
        catchError(() => {
          this.notifications.show('La fenêtre d’enrôlement n’a pas pu être ouverte.');
          return EMPTY;
        }),
      )
      .subscribe((window) => {
        this.enrollmentWindow.set(window);
        this.notifications.show('L’enrôlement direct est ouvert pendant 10 minutes.');
      });
  }

  protected closeEnrollmentWindow(): void {
    this.clusterApi
      .closeEnrollmentWindow()
      .pipe(
        catchError(() => {
          this.notifications.show('La fenêtre d’enrôlement n’a pas pu être fermée.');
          return EMPTY;
        }),
      )
      .subscribe(() => {
        this.enrollmentWindow.set({ isOpen: false, openUntil: null });
        this.notifications.show('L’enrôlement direct est fermé.');
      });
  }

  protected closeCreateNode(): void {
    if (this.local.flow()) {
      this.local.closeFlow();
      return;
    }
    this.actionOpen.set(false);
    this.selectedEnrollment.set(null);
    this.enrollmentStep.set(0);
    this.enrollmentCodeConfirmed.set(false);
    this.selectedNodeLibrary.set(null);
  }

  protected showPairingKey(): void {
    this.selectedEnrollment.set(null);
    this.enrollmentView.set('key');
  }

  protected showPendingRequests(): void {
    this.enrollmentView.set('requests');
    this.selectedEnrollment.set(null);
    this.enrollmentStep.set(0);
    this.enrollmentCodeConfirmed.set(false);
  }

  protected onEnrollmentOpen(row: DataTableRow): void {
    const request = this.pendingEnrollments().find((item) => item.id === row['id']) ?? null;
    this.selectedEnrollment.set(request);
    if (request) {
      this.enrollmentView.set('review');
      this.enrollmentStep.set(0);
      this.enrollmentCodeConfirmed.set(false);
      this.actionType.set('enrollment');
      this.actionOpen.set(true);
    }
  }

  protected onEnrollmentSetupAction(id: string): void {
    if (id === 'cancel') {
      this.closeCreateNode();
    } else if (id === 'back') {
      this.enrollmentStep.update((index) => Math.max(0, index - 1));
    } else if (id === 'continue') {
      this.enrollmentStep.update((index) => Math.min(2, index + 1));
    } else if (id === 'approve') {
      this.approveEnrollment();
    }
  }

  protected approveEnrollment(): void {
    const request = this.selectedEnrollment();
    if (
      !request ||
      !request.verificationCode ||
      this.enrollmentApprovalBusy() ||
      !this.enrollmentCodeConfirmed()
    )
      return;
    this.enrollmentApprovalBusy.set(true);
    this.http
      .post<NodeResponse>(`/api/v1/nodes/enrollment-requests/${request.id}/approve`, {})
      .pipe(
        finalize(() => this.enrollmentApprovalBusy.set(false)),
        catchError((error: { status?: number }) => {
          this.notifications.show(
            error.status === 404
              ? 'Cette demande a expiré ou a déjà été traitée. Actualisez la liste.'
              : 'Le nœud n’a pas pu être accepté. Vérifiez que le serveur est disponible.',
          );
          return EMPTY;
        }),
      )
      .subscribe((node) => {
        this.nodes.update((current) => [
          ...current.filter((existing) => existing.id !== node.id),
          node,
        ]);
        this.pendingEnrollments.update((current) => current.filter((item) => item.id !== node.id));
        this.selectedEnrollment.set(null);
        this.enrollmentView.set('requests');
        this.loadState.set('ready');
        this.notifications.show(`Le nœud « ${node.name} » a rejoint le cluster.`);
      });
  }

  protected acceptNode(value: Record<string, string>): void {
    const pairingKey = value['pairingKey']?.trim().toUpperCase();
    if (!pairingKey) return;
    this.actionOpen.set(false);
    this.http
      .post<NodeResponse>('/api/v1/nodes/enrollment-requests/approve', { pairingKey })
      .pipe(
        catchError((error: { status?: number }) => {
          this.notifications.show(
            error.status === 404
              ? 'Cette clé est invalide, expirée ou déjà utilisée. Générez-en une nouvelle depuis le nœud.'
              : 'Le nœud n’a pas pu être accepté. Vérifiez que le serveur est disponible.',
          );
          return EMPTY;
        }),
      )
      .subscribe((node) => {
        this.nodes.update((current) => [
          ...current.filter((existing) => existing.id !== node.id),
          node,
        ]);
        this.pendingEnrollments.update((current) => current.filter((item) => item.id !== node.id));
        this.loadState.set('ready');
        this.notifications.show(`Le nœud « ${node.name} » a rejoint le cluster.`);
      });
  }

  protected formatExpiry(value: string): string {
    const seconds = Math.max(0, Math.ceil((new Date(value).getTime() - Date.now()) / 1000));
    if (seconds < 60) return `Dans ${seconds} s`;
    return `Dans ${Math.ceil(seconds / 60)} min`;
  }

  protected formatExpiryTime(value: string): string {
    return new Intl.DateTimeFormat('fr-FR', { hour: '2-digit', minute: '2-digit' }).format(
      new Date(value),
    );
  }

  protected capabilitiesLabel(capabilities: readonly NodeToolCapability[]): string {
    return capabilities.length
      ? capabilities.map((item) => `${item.toolId} ${item.version}`).join(' · ')
      : 'Aucun outil détecté';
  }

  protected onNodeOpen(row: DataTableRow): void {
    this.selectedNode.set(this.filteredNodes().find((node) => node.id === row['id']) ?? null);
    this.selectedNodeTabIndex.set(0);
    this.loadNodeLibraries();
  }

  protected onDetailClosed(): void {
    this.selectedNode.set(null);
    this.selectedNodeTabIndex.set(0);
    this.processingFormNodeId = null;
    this.processingForm.set(null);
  }

  protected nodeAccessOption(node: NodeRecord): LibraryNodeAccessOption {
    const original = this.nodes().find((item) => item.id === node.id);
    return { id: node.id, name: node.name, roles: original?.roles ?? [] };
  }

  protected openAddLibrary(): void {
    this.actionType.set('create-library');
    this.actionOpen.set(true);
  }

  protected addExistingLibrary(): void {
    const library = this.allLibraries().find(
      (item) => item.id === this.selectedAvailableLibraryId(),
    );
    if (library) this.editNodeLibraryAccess(library);
  }

  protected editNodeLibraryAccess(library: NodeLibraryRecord | LibraryResponse): void {
    this.selectedNodeLibrary.set(
      this.allLibraries().find((item) => item.id === library.id) ?? (library as LibraryResponse),
    );
    this.actionType.set('library-access');
    this.actionOpen.set(true);
  }

  protected onNodeLibraryToggle(event: DataTableToggleEvent): void {
    const node = this.selectedNode();
    const library = this.allLibraries().find((item) => item.id === event.row['id']);
    if (!node || !library) return;
    if (event.checked && event.row['mode'] === 'unsupported') {
      this.editNodeLibraryAccess(library);
      return;
    }
    const busyKey = `${node.id}:${library.id}`;
    this.nodeAccessActivationBusy.update((current) => new Set(current).add(busyKey));
    this.http
      .put<LibraryNodeAccessResponse>(
        `/api/v1/libraries/${library.id}/node-access/${node.id}/activation`,
        { isEnabled: event.checked },
      )
      .pipe(
        catchError(() => {
          this.notifications.show('L’accès de ce nœud n’a pas pu être modifié.');
          return EMPTY;
        }),
        finalize(() =>
          this.nodeAccessActivationBusy.update((current) => {
            const next = new Set(current);
            next.delete(busyKey);
            return next;
          }),
        ),
      )
      .subscribe((updated) => {
        this.nodeAccessByLibrary.update((current) =>
          new Map(current).set(
            library.id,
            (current.get(library.id) ?? []).map((item) =>
              item.nodeId === node.id ? updated : item,
            ),
          ),
        );
        this.notifications.show(
          `Accès à « ${library.name} » ${event.checked ? 'réactivé' : 'désactivé'} pour ${node.name}.`,
        );
      });
  }

  protected onNodeLibraryAction(event: DataTableActionEvent): void {
    const node = this.selectedNode();
    const library = this.allLibraries().find((item) => item.id === event.row['id']);
    if (!node || !library) return;
    if (event.actionId === 'edit') {
      this.editNodeLibraryAccess(library);
      return;
    }

    const existing = this.nodeAccessByLibrary().get(library.id) ?? [];
    const access = existing.find((item) => item.nodeId === node.id);
    if (!access) return;
    const configured = existing
      .filter((item) => event.actionId !== 'delete' || item.nodeId !== node.id)
      .map((item): LibraryNodeAccessValue => ({
        nodeId: item.nodeId,
        mode: item.mode,
        isEnabled: item.isEnabled,
        localPath: item.localPath,
        canRead: item.canRead,
        canWrite: item.canWrite,
        transferSourceNodeId: item.transferSourceNodeId,
      }));
    this.saveNodeLibraryConfiguration(library, node, configured, event.actionId);
  }

  protected createLibraryFromNode(value: Record<string, string>): void {
    this.http
      .post<LibraryResponse>('/api/v1/libraries', value)
      .pipe(
        catchError((error: { status?: number }) => {
          this.notifications.show(
            error.status === 409
              ? 'Une bibliothèque porte déjà ce nom.'
              : 'La bibliothèque n’a pas pu être déclarée sur le serveur.',
          );
          return EMPTY;
        }),
      )
      .subscribe((library) => {
        this.allLibraries.update((current) => [...current, library]);
        this.notifications.show(
          `La bibliothèque « ${library.name} » est déclarée sur le serveur. Autorisez ce nœud pour configurer son accès.`,
        );
        this.editNodeLibraryAccess(library);
      });
  }

  protected saveNodeLibraryAccess(values: readonly LibraryNodeAccessValue[]): void {
    const node = this.selectedNode();
    const library = this.selectedNodeLibrary();
    if (!node || !library || values.length !== 1) return;
    const existing = this.nodeAccessByLibrary().get(library.id) ?? [];
    const merged = [
      ...existing
        .filter((item) => item.nodeId !== node.id)
        .map((item) => ({
          nodeId: item.nodeId,
          mode: item.mode,
          isEnabled: item.isEnabled,
          localPath: item.localPath,
          canRead: item.canRead,
          canWrite: item.canWrite,
          transferSourceNodeId: item.transferSourceNodeId,
        })),
      values[0],
    ];
    this.saveNodeLibraryConfiguration(library, node, merged, 'edit');
  }

  private saveNodeLibraryConfiguration(
    library: LibraryResponse,
    node: NodeRecord,
    configured: readonly LibraryNodeAccessValue[],
    action: string,
  ): void {
    this.accessFormBusy.set(true);
    this.http
      .put<readonly LibraryNodeAccessResponse[]>(`/api/v1/libraries/${library.id}/node-access`, {
        nodes: configured,
      })
      .pipe(
        catchError((error: { error?: { errors?: Record<string, string[]> } }) => {
          this.accessFormBusy.set(false);
          const validation = error.error?.errors
            ? Object.values(error.error.errors).flat()[0]
            : undefined;
          this.notifications.show(
            validation ?? 'La configuration de cette bibliothèque n’a pas pu être enregistrée.',
          );
          return EMPTY;
        }),
      )
      .subscribe((accesses) => {
        this.accessFormBusy.set(false);
        this.nodeAccessByLibrary.update((current) => new Map(current).set(library.id, accesses));
        this.actionOpen.set(false);
        this.notifications.show(
          action === 'delete'
            ? `Bibliothèque « ${library.name} » retirée du nœud ${node.name}.`
            : `Accès à « ${library.name} » enregistré pour ${node.name}.`,
        );
      });
  }

  private loadNodeLibraries(): void {
    const node = this.selectedNode();
    if (!node) return;
    this.nodeLibraryLoading.set(true);
    this.clusterApi
      .listLibraries()
      .pipe(
        switchMap((libraries) =>
          forkJoin({
            libraries: Promise.resolve(libraries),
            accessLists: libraries.length
              ? forkJoin(
                  libraries.map((library) =>
                    this.http.get<readonly LibraryNodeAccessResponse[]>(
                      `/api/v1/libraries/${library.id}/node-access`,
                    ),
                  ),
                )
              : Promise.resolve([] as readonly (readonly LibraryNodeAccessResponse[])[]),
          }),
        ),
        catchError(() => {
          this.nodeLibraryLoading.set(false);
          this.notifications.show(
            'Les bibliothèques autorisées pour ce nœud n’ont pas pu être chargées.',
          );
          return EMPTY;
        }),
      )
      .subscribe(({ libraries, accessLists }) => {
        this.allLibraries.set(libraries);
        this.nodeAccessByLibrary.set(
          new Map(libraries.map((library, index) => [library.id, accessLists[index]])),
        );
        this.libraryAccessLoadState.set('ready');
        this.nodeLibraryLoading.set(false);
      });
  }

  private loadLibraryAccessSummary(): void {
    this.clusterApi
      .listLibraries()
      .pipe(
        switchMap((libraries) =>
          forkJoin({
            libraries: of(libraries),
            accessLists: libraries.length
              ? forkJoin(
                  libraries.map((library) =>
                    this.http.get<readonly LibraryNodeAccessResponse[]>(
                      `/api/v1/libraries/${library.id}/node-access`,
                    ),
                  ),
                )
              : of([] as readonly (readonly LibraryNodeAccessResponse[])[]),
          }),
        ),
        catchError(() => {
          this.libraryAccessLoadState.set('error');
          return EMPTY;
        }),
      )
      .subscribe(({ libraries, accessLists }) => {
        this.allLibraries.set(libraries);
        this.nodeAccessByLibrary.set(
          new Map(libraries.map((library, index) => [library.id, accessLists[index]])),
        );
        this.libraryAccessLoadState.set('ready');
      });
  }

  private toRecord(node: NodeResponse): NodeRecord {
    const roleLabel = node.roles.map((role) => NODE_ROLE_LABELS[role]).join(' · ');
    const worker = node.roles.includes('worker');
    return {
      id: node.id,
      name: formatNodeLabel(node.name, node.displayName),
      roleFilter: `|${node.roles.join('|')}|`,
      roleLabel,
      libraryAccess: this.libraryAccessSummary(node.id),
      workerLoad: worker
        ? this.activityKnown()
          ? String(this.activeTasks()[node.id] ?? 0) + ' / ' + node.maxConcurrentTasks + ' tâche(s)'
          : String(node.maxConcurrentTasks) + ' tâche(s) max. · charge indisponible'
        : 'Non applicable',
      queueStatus: 'Non disponible',
      state: NODE_LIFECYCLE_LABELS[nodeLifecycle(node)],
      lastSignal: this.formatLastSeen(node.lastSeenAt),
    };
  }

  private libraryAccessSummary(nodeId: string): string {
    const loadState = this.libraryAccessLoadState();
    if (loadState === 'loading') return 'Chargement…';
    if (loadState === 'error') return 'Indisponible';
    const accesses = this.allLibraries().flatMap((library) => {
      const access = this.nodeAccessByLibrary()
        .get(library.id)
        ?.find((item) => item.nodeId === nodeId);
      return access && access.isEnabled && access.mode !== 'unsupported'
        ? [{ library, access }]
        : [];
    });
    if (!accesses.length) return 'À configurer';
    const activeCount = accesses.filter(({ library }) => library.isActive).length;
    const inactiveCount = accesses.length - activeCount;
    return inactiveCount
      ? `${activeCount} active(s) · ${inactiveCount} désactivée(s)`
      : `${activeCount} bibliothèque(s) active(s)`;
  }

  private formatLastSeen(value: string): string {
    const elapsedSeconds = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000));
    if (elapsedSeconds < 5) return 'À l’instant';
    if (elapsedSeconds < 60) return `Il y a ${elapsedSeconds} s`;
    const minutes = Math.floor(elapsedSeconds / 60);
    if (minutes < 60) return `Il y a ${minutes} min`;
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `Il y a ${hours} h`;
    return new Intl.DateTimeFormat('fr-FR', { dateStyle: 'medium', timeStyle: 'short' }).format(
      new Date(value),
    );
  }

  protected readonly groupSearchQuery = signal('');
  protected readonly selectedGroupForDetail = signal<NodeGroupResponse | null>(null);

  protected readonly filteredGroups = computed(() => {
    const query = this.groupSearchQuery().trim().toLowerCase();
    const all = this.groups();
    if (!query) return all;
    return all.filter((g) => {
      const matchName = g.name.toLowerCase().includes(query);
      const matchDesc = g.description?.toLowerCase().includes(query) ?? false;
      const matchLabels = Object.entries(g.labels).some(
        ([k, v]) => k.toLowerCase().includes(query) || (v?.toLowerCase().includes(query) ?? false),
      );
      return matchName || matchDesc || matchLabels;
    });
  });

  protected createGroup(): void {
    this.groupEditing.set(null);
    this.selectedGroupForDetail.set(null);
    this.groupFormOpen.set(true);
  }

  protected editGroup(group: NodeGroupResponse): void {
    this.groupEditing.set(group);
    this.selectedGroupForDetail.set(null);
    this.groupFormOpen.set(true);
  }

  protected openGroupDetail(group: NodeGroupResponse): void {
    this.selectedGroupForDetail.set(group);
    this.groupFormOpen.set(false);
  }

  protected closeGroupForm(): void {
    this.groupFormOpen.set(false);
    this.groupEditing.set(null);
    this.groupBusy.set(false);
  }

  protected closeGroupDetail(): void {
    this.selectedGroupForDetail.set(null);
  }

  protected async saveNodeGroup(payload: SaveNodeGroupPayload): Promise<void> {
    if (this.groupBusy()) return;
    this.groupBusy.set(true);
    const current = this.groupEditing();
    try {
      if (current) {
        await firstValueFrom(this.http.put(`/api/v1/node-groups/${current.id}`, payload));
      } else {
        await firstValueFrom(this.http.post('/api/v1/node-groups', payload));
      }
      await this.loadGroups();
      this.closeGroupForm();
      this.notifications.show(
        current ? 'Groupe mis à jour avec succès.' : 'Groupe créé avec succès.',
      );
    } catch {
      this.notifications.show(
        'Le groupe n’a pas pu être enregistré. Vérifiez son nom, ses critères et ses étiquettes.',
      );
    } finally {
      this.groupBusy.set(false);
    }
  }

  protected confirmRemoveGroup(group: NodeGroupResponse): void {
    this.dialog
      .open(ConfirmationDialogComponent, {
        data: {
          title: `Supprimer le groupe « ${group.name} » ?`,
          message:
            'Ce groupe et ses étiquettes partagées seront supprimés. Les nœuds membres et leur inventaire ne sont pas affectés. Attention : un groupe ciblé par un flow ne peut pas être supprimé.',
          confirmLabel: 'Supprimer',
          variant: 'danger',
        },
      })
      .afterClosed()
      .subscribe((confirmed: boolean) => {
        if (confirmed) {
          void this.removeGroup(group);
        }
      });
  }

  private async removeGroup(group: NodeGroupResponse): Promise<void> {
    try {
      await firstValueFrom(this.http.delete(`/api/v1/node-groups/${group.id}`));
      await this.loadGroups();
      if (this.selectedGroupForDetail()?.id === group.id) {
        this.closeGroupDetail();
      }
      this.notifications.show('Groupe supprimé avec succès.');
    } catch (error) {
      const title = (error as { error?: { title?: unknown } }).error?.title;
      this.notifications.show(
        typeof title === 'string'
          ? title
          : 'Le groupe n’a pas pu être supprimé. Vérifiez qu’il n’est pas ciblé par un flow.',
      );
    }
  }

  private async loadGroups(): Promise<void> {
    this.groupState.set('loading');
    try {
      this.groups.set(
        await firstValueFrom(this.http.get<readonly NodeGroupResponse[]>('/api/v1/node-groups')),
      );
      this.groupState.set('ready');
    } catch {
      this.groupState.set('error');
      this.groupMessage.set('Impossible de charger les groupes de nœuds.');
    }
  }
}
