import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatMenuModule } from '@angular/material/menu';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { ActivatedRoute } from '@angular/router';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { AuthService } from '../../core/auth/auth.service';
import {
  PageShellContextService,
  PageShellSearchResult,
} from '../../core/layout/page-shell-context';
import { LocalWorkerController } from '../../shared/services/local-worker-controller';
import { CentralMaintenanceTemplateComponent } from '../../shared/templates/central-maintenance-template.component';
import { NODE_MANAGEMENT_TABS } from '../../shared/models/worker-management';
import { formatWorkerLabel, NODE_ROLE_LABELS, WorkerRole } from '../../shared/models/central';
import { workerLifecycle, NODE_LIFECYCLE_LABELS } from '../../shared/models/central-health';
import { DrawerActionButtonComponent } from '../../shared/ui/drawer-action-button/drawer-action-button.component';
import { RecordFormActionsComponent } from '../../shared/ui/record-form-actions.component';
import { WorkerDataStore } from './worker-data.store';
import { WorkerMembersStore } from './worker-members.store';
import { WorkerDetailStore } from './worker-detail.store';
import { WorkerProcessingStore } from './worker-processing.store';
import { WorkerEnrollmentStore } from './worker-enrollment.store';
import { WorkerGroupsStore } from './worker-groups.store';
import { WorkerLocalTabComponent } from './components/worker-local-tab.component';
import { WorkerClusterTabComponent } from './components/worker-cluster-tab.component';
import { WorkerClusterSummaryComponent } from './components/worker-cluster-summary.component';
import { WorkerGroupsTabComponent } from './components/worker-groups-tab.component';
import { WorkerDetailDrawerComponent } from './components/worker-detail-drawer.component';
import { WorkerActionDrawerComponent } from './components/worker-action-drawer.component';
import { WorkerGroupDetailComponent } from './components/worker-group-detail.component';
import { ENROLLMENT_FORM_DATA } from './worker-catalog.utils';
import { IconComponent } from '../../shared/ui/icon.component';

/**
 * Page Workers : onglets « Ce worker », « Central » et « Groupes de workers ».
 * L’état et les actions vivent dans les stores de la feature ; les vues sont
 * les sous-composants du dossier `components/`.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-worker-catalog-page',
  providers: [
    LocalWorkerController,
    WorkerDataStore,
    WorkerMembersStore,
    WorkerDetailStore,
    WorkerProcessingStore,
    WorkerEnrollmentStore,
    WorkerGroupsStore,
  ],
  imports: [
    CentralMaintenanceTemplateComponent,
    IconComponent,
    MatButtonModule,
    MatMenuModule,
    MatFormFieldModule,
    MatSelectModule,
    MatSlideToggleModule,
    DrawerActionButtonComponent,
    RecordFormActionsComponent,
    WorkerGroupDetailComponent,
    WorkerLocalTabComponent,
    WorkerClusterTabComponent,
    WorkerClusterSummaryComponent,
    WorkerGroupsTabComponent,
    WorkerDetailDrawerComponent,
    WorkerActionDrawerComponent,
  ],
  templateUrl: './worker-catalog-page.component.html',
  styleUrl: './worker-catalog-page.component.scss',
})
export class WorkerCatalogPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);
  private readonly pageShellContext = inject(PageShellContextService);
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });

  readonly data = inject(WorkerDataStore);
  readonly members = inject(WorkerMembersStore);
  readonly detail = inject(WorkerDetailStore);
  readonly processing = inject(WorkerProcessingStore);
  readonly enrollment = inject(WorkerEnrollmentStore);
  readonly groups = inject(WorkerGroupsStore);
  protected readonly local = this.data.local;
  protected readonly isAdmin = inject(AuthService).isAdmin;

  protected readonly pageTab = signal(
    this.route.snapshot.queryParamMap.get('tab') === 'groups'
      ? 'groups'
      : this.route.snapshot.queryParamMap.get('tab') === 'cluster'
        ? 'cluster'
        : 'node',
  );
  protected readonly filtersOpen = signal(false);
  protected readonly managementTabs = NODE_MANAGEMENT_TABS;

  private readonly actionDrawer = viewChild(WorkerActionDrawerComponent);
  protected readonly actionRecordForm = computed(
    () => this.actionDrawer()?.actionRecordForm() ?? null,
  );
  protected readonly actionStep = computed(() => this.actionDrawer()?.actionStep() ?? null);

  protected readonly clusterIdentity = this.local.identity;
  protected readonly clusterName = this.members.clusterName;

  protected readonly pageSearchQuery = computed(() =>
    this.pageTab() === 'groups' ? this.groups.groupSearchQuery() : this.members.nodeSearchQuery(),
  );

  protected readonly title = computed(() => {
    if (this.pageTab() === 'groups') {
      return 'Groupes de workers';
    }
    if (this.pageTab() === 'cluster') {
      return this.clusterIdentity()
        ? this.clusterName()
        : ((this.routeData()['title'] as string) ?? 'Workers');
    }
    return this.data.localName() || ((this.routeData()['title'] as string) ?? 'Workers');
  });

  protected readonly catalogMessage = computed(() => {
    if (this.pageTab() === 'groups') return this.groups.groupMessage();
    return this.data.loadState() === 'error'
      ? 'Impossible de joindre l’API du central. Vérifiez que le serveur et sa base de données sont disponibles.'
      : 'Chargement des workers du central…';
  });

  protected readonly pageState = computed(() => {
    if (this.pageTab() === 'groups') return this.groups.groupState();
    if (this.local.loading()) return 'loading';
    if (this.pageTab() === 'cluster' && !this.clusterIdentity()) return 'error';
    return this.pageTab() === 'cluster' ? this.data.loadState() : 'ready';
  });

  protected readonly detailTitle = computed(() => {
    const raw = this.members.rawSelectedNode();
    return raw ? formatWorkerLabel(raw.name, raw.displayName) : 'Détails du worker';
  });
  protected readonly detailDescription = computed(
    () => this.data.selectedNode()?.roleLabel ?? 'État observé par le central',
  );

  protected readonly actionTitle = computed(() => {
    const actionType = this.data.actionType();
    if (actionType === 'identity') return 'Nom et étiquettes du worker';
    if (actionType === 'library-access')
      return `Accès à ${this.detail.selectedNodeLibrary()?.name ?? 'la bibliothèque'}`;
    if (actionType === 'create-library') return 'Déclarer une bibliothèque';
    return ENROLLMENT_FORM_DATA.title;
  });
  protected readonly actionDescription = computed(() => {
    const actionType = this.data.actionType();
    if (actionType === 'identity')
      return 'Le nom affiché est distinct du nom envoyé par heartbeat. Les étiquettes locales remplacent celles des groupes.';
    if (actionType === 'library-access')
      return 'Renseignez le chemin local de cette bibliothèque pour ce worker.';
    if (actionType === 'create-library')
      return 'Le serveur crée la définition partagée. Vous pourrez ensuite autoriser les workers et configurer leurs chemins.';
    return ENROLLMENT_FORM_DATA.description;
  });

  constructor() {
    this.destroyRef.onDestroy(
      this.pageShellContext.register({
        searchPlaceholder: 'Rechercher dans les workers…',
        query: this.pageSearchQuery,
        onSearchQueryChange: (query) => {
          if (this.pageTab() === 'groups') this.groups.groupSearchQuery.set(query);
          else this.members.nodeSearchQuery.set(query);
        },
        searchResults: (query) => this.pageSearchResults(query),
        filter: {
          label: 'Filtres',
          visibleWhen: () => this.pageTab() === 'cluster',
          activeCount: () => this.members.activeFilters().length,
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
    void this.groups.loadGroups();
  }

  protected onManagementTabChange(tab: string): void {
    if (this.local.busy()) return;
    this.pageTab.set(tab);
    this.data.actionOpen.set(false);
    this.groups.closeGroupForm();
    this.onDetailClosed();
  }

  protected onSetupFinished(): void {
    this.pageTab.set('cluster');
  }

  protected onDetailClosed(): void {
    this.data.onDetailClosed();
    this.processing.resetOnDetailClosed();
  }

  protected focusEnrollmentRequests(): void {
    const behavior = window.matchMedia('(prefers-reduced-motion: reduce)').matches
      ? 'auto'
      : 'smooth';
    document
      .getElementById('node-enrollment-requests')
      ?.scrollIntoView({ behavior, block: 'start' });
  }

  private pageSearchResults(query: string): readonly PageShellSearchResult[] {
    const normalized = query.trim().toLocaleLowerCase('fr');
    if (!normalized) return [];
    const matches = (...values: readonly string[]) =>
      values.some((value) => value.toLocaleLowerCase('fr').includes(normalized));
    if (this.pageTab() === 'groups') {
      return this.groups
        .groups()
        .filter((group) => matches(group.name, group.description))
        .slice(0, 6)
        .map((group) => ({
          id: `node-group:${group.id}`,
          label: group.name,
          description: group.description || 'Groupe de workers',
          group: 'Groupes de workers',
          icon: 'folder' as const,
          run: () => this.groups.openGroupDetail(group),
        }));
    }
    if (this.pageTab() === 'cluster') {
      return this.data
        .nodes()
        .filter((node) =>
          matches(
            node.name,
            node.displayName ?? '',
            node.id,
            node.roles.map((role: WorkerRole) => NODE_ROLE_LABELS[role]).join(' '),
          ),
        )
        .slice(0, 6)
        .map((node) => ({
          id: `node:${node.id}`,
          label: formatWorkerLabel(node.name, node.displayName),
          description: `${NODE_LIFECYCLE_LABELS[workerLifecycle(node)]} · ${node.roles.map((role: WorkerRole) => NODE_ROLE_LABELS[role]).join(' · ')}`,
          group: 'Workers du central',
          icon: 'server' as const,
          run: () => this.data.openNodeCard(node.id),
        }));
    }
    const local = this.data.localNode();
    return local && matches(local.name, local.displayName ?? '', local.id)
      ? [
          {
            id: `node:${local.id}`,
            label: formatWorkerLabel(local.name, local.displayName),
            description: 'Ce worker',
            group: 'Worker local',
            icon: 'server' as const,
            run: () => this.pageTab.set('node'),
          },
        ]
      : [];
  }
}
