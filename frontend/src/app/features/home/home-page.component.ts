import { IconComponent } from '../../shared/ui/icon.component';
import { AiProviderOverviewComponent } from '../ai/ai-provider-overview.component';
import {
  ExecutionTracking,
  INITIAL_EXECUTION_TRACKING,
} from '../../shared/services/execution-tracking';
import { HomeExecutions, INITIAL_HOME_EXECUTIONS } from './home-executions';
import { DatePipe } from '@angular/common';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ClusterOverview, INITIAL_CLUSTER_OVERVIEW } from '../../shared/services/cluster-overview';
import {
  assessClusterHealth,
  NODE_LIFECYCLE_LABELS,
  NODE_LIFECYCLE_TONES,
  nodeLifecycle,
} from '../../shared/models/cluster-health';
import { HealthBannerComponent } from '../../shared/ui/health-banner.component';
import { SummaryTilesComponent } from '../../shared/ui/summary-tiles.component';
import { MatrixCell, MatrixTableComponent } from '../../shared/ui/matrix-table.component';
import {
  formatNodeLabel,
  NodeResponse,
  NodeRole,
  NODE_ROLE_LABELS,
} from '../../shared/models/cluster';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  TemplateRef,
  computed,
  inject,
  linkedSignal,
  signal,
  viewChild,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { DetailDrawerComponent } from '../../shared/ui/detail-drawer/detail-drawer';
import { CategoryTabOption } from '../../shared/ui/category-tabs.component';
import { MonitorTemplateComponent } from '../../shared/templates/monitor-template.component';
import { RecordFormActionsComponent } from '../../shared/ui/record-form-actions.component';
import { RecordFormComponent } from '../../shared/ui/record-form.component';
import { FlowTemplates } from '../../shared/services/flow-templates';
import { StatusBadgeComponent } from '../../shared/ui/status-badge.component';
import { OverviewExecutionComponent } from './components/overview-execution.component';
import { AiToolsService } from '../../shared/services/ai-tools.service';
import {
  PageShellContextService,
  PageShellSearchResult,
} from '../../core/layout/page-shell-context';
import {
  AiAssistantActivity,
  AiAssistantConversation,
  AiAssistantOptions,
} from '../../shared/models/ai-tools';
import { catchError, firstValueFrom, forkJoin, of } from 'rxjs';
import { interval } from 'rxjs';

type OverviewView = 'general' | 'ai' | 'activity' | 'infrastructure';

@Component({
  selector: 'app-home-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [HomeExecutions],
  imports: [
    IconComponent,
    AiProviderOverviewComponent,
    MatButtonModule,
    DatePipe,
    DetailDrawerComponent,
    OverviewExecutionComponent,
    HealthBannerComponent,
    SummaryTilesComponent,
    MatrixTableComponent,
    NgTemplateOutlet,
    MonitorTemplateComponent,
    RecordFormComponent,
    RecordFormActionsComponent,
    RouterLink,
    StatusBadgeComponent,
  ],
  templateUrl: './home-page.component.html',
  styleUrl: './home-page.component.scss',
})
export class HomePageComponent {
  private readonly router = inject(Router);
  private readonly flowTemplates = inject(FlowTemplates);
  protected readonly flowActionOpen = signal(false);
  private readonly overviewParams = toSignal(inject(ActivatedRoute).queryParamMap);
  protected readonly overviewTabs: readonly CategoryTabOption[] = [
    { value: 'general', label: 'Général' },
    { value: 'ai', label: 'IA' },
    { value: 'activity', label: 'Activité' },
    { value: 'infrastructure', label: 'Infrastructure' },
  ];
  protected readonly overviewView = linkedSignal<OverviewView>(() => {
    const value = this.overviewParams()?.get('view');
    return this.overviewTabs.some((tab) => tab.value === value)
      ? (value as OverviewView)
      : 'general';
  });
  protected readonly monitorLayout = computed(() =>
    this.overviewView() === 'ai'
      ? 'special'
      : this.overviewView() === 'general'
        ? 'overview'
        : 'single',
  );
  protected onOverviewViewChanged(value: string): void {
    if (this.overviewTabs.some((tab) => tab.value === value)) {
      this.overviewView.set(value as OverviewView);
      if (value === 'ai') this.refreshAiOverview();
    }
  }
  protected readonly flowCreationForm = signal<
    import('../../shared/ui/record-form.component').RecordFormData | null
  >(null);
  protected readonly flowFormTemplate = viewChild<TemplateRef<unknown>>('flowForm');
  protected readonly flowFooterTemplate = viewChild<TemplateRef<unknown>>('flowFooter');
  protected readonly flowRecordForm = viewChild(RecordFormComponent);
  protected readonly flowStep = computed(() => {
    const form = this.flowRecordForm();
    return form ? { current: form.stepIndex() + 1, total: form.stepCount() } : null;
  });
  protected readonly homeContentTemplate = viewChild<TemplateRef<unknown>>('homeContent');

  protected async openFlowCreation(): Promise<void> {
    const { createFlowCreationForm } = await import('../../shared/models/flow-creation');
    try {
      this.flowCreationForm.set(
        createFlowCreationForm(await firstValueFrom(this.flowTemplates.list())),
      );
    } catch {
      this.flowCreationForm.set(createFlowCreationForm());
    }
    this.flowActionOpen.set(true);
  }

  protected closeFlowCreation(): void {
    this.flowActionOpen.set(false);
  }

  protected startFlow(values: Record<string, string>): void {
    const starter = values['starter'] ?? 'blank';
    const templateId = starter.startsWith('template:') ? starter.slice('template:'.length) : null;
    if (templateId) {
      void this.router.navigate(['/libraries/templates'], {
        queryParams: { template: templateId, configure: true, name: values['name'] },
        info: { flowEditorDirection: 'forward' },
      });
      this.flowActionOpen.set(false);
      return;
    }
    void this.router.navigate([starter === 'guided' ? '/flows/guided' : '/flows/new'], {
      queryParams: {
        name: values['name'],
        mediaType: values['mediaType'],
        starter,
      },
      info: { flowEditorDirection: 'forward' },
    });
    this.flowActionOpen.set(false);
  }

  private readonly executionTracking = inject(ExecutionTracking);
  private readonly aiTools = inject(AiToolsService);
  private readonly pageShellContext = inject(PageShellContextService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly searchQuery = signal('');
  protected readonly aiOptions = signal<AiAssistantOptions | null>(null);
  protected readonly aiActivity = signal<readonly AiAssistantActivity[]>([]);
  protected readonly aiConversations = signal<readonly AiAssistantConversation[]>([]);
  protected readonly aiOverviewLoading = signal(true);
  protected readonly aiOverviewPartial = signal(false);
  protected readonly aiProviderCount = computed(
    () => new Set((this.aiOptions()?.models ?? []).map((model) => model.provider)).size,
  );
  protected readonly aiDefaultModel = computed(() => this.aiOptions()?.defaultModel || '—');
  protected readonly aiDefaultProvider = computed(() => {
    const provider = this.aiOptions()?.defaultProvider;
    return provider ? this.providerLabel(provider) : 'Fournisseur non défini';
  });
  protected readonly aiProviders = computed(() =>
    [...new Set((this.aiOptions()?.models ?? []).map((model) => model.provider))].sort(),
  );
  protected readonly aiProviderNames = computed(() =>
    this.aiProviders()
      .map((provider) => this.providerLabel(provider))
      .join(' · '),
  );
  protected readonly aiRunningCount = computed(
    () => this.aiActivity().filter((activity) => activity.status === 'running').length,
  );
  protected readonly aiFailedCount = computed(
    () => this.aiActivity().filter((activity) => activity.status === 'failed').length,
  );
  protected readonly aiMetrics = computed(() => [
    {
      label: 'Fournisseurs disponibles',
      value: this.count(this.aiProviderCount()),
      detail: `${this.aiOptions()?.models?.length ?? 0} modèles · ${this.aiProviderNames() || 'Aucun disponible'}`,
    },
    {
      label: 'Modèle par défaut',
      value: this.aiDefaultModel(),
      detail: this.aiDefaultProvider(),
    },
    {
      label: 'Demandes en cours',
      value: this.count(this.aiRunningCount()),
      detail: 'Demandes encore actives',
    },
    {
      label: 'Demandes en échec',
      value: this.count(this.aiFailedCount()),
      detail: 'Dans l’activité conservée',
      tone: this.aiFailedCount() > 0 ? ('attention' as const) : ('default' as const),
    },
  ]);
  protected readonly recentAiActivity = computed(() => {
    const query = this.searchQuery().trim().toLocaleLowerCase('fr-FR');
    return [...this.aiActivity()]
      .filter((item) =>
        !query ||
        [item.provider, item.operation, item.progressDetail, item.status].some((value) =>
          (value ?? '').toLocaleLowerCase('fr-FR').includes(query),
        ),
      )
      .sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))
      .slice(0, 5);
  });
  protected readonly recentAiConversations = computed(() => {
    const query = this.searchQuery().trim().toLocaleLowerCase('fr-FR');
    return [...this.aiConversations()]
      .filter((item) =>
        !query ||
        [item.title, item.provider, item.source.label].some((value) =>
          value.toLocaleLowerCase('fr-FR').includes(query),
        ),
      )
      .sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))
      .slice(0, 5);
  });
  protected readonly executionState = toSignal(this.executionTracking.state$, {
    initialValue: INITIAL_EXECUTION_TRACKING,
  });
  private readonly homeExecutions = inject(HomeExecutions);
  protected readonly homeExecutionState = toSignal(this.homeExecutions.state$, {
    initialValue: INITIAL_HOME_EXECUTIONS,
  });
  protected readonly activePreview = computed(() => {
    const query = this.searchQuery().trim().toLocaleLowerCase('fr-FR');
    return this.homeExecutionState().active.filter((item) =>
      !query ||
      [item.media, item.library, item.flow, item.node ?? '', item.detail].some((value) =>
        value.toLocaleLowerCase('fr-FR').includes(query),
      ),
    );
  });
  protected readonly publications = computed(() => {
    const query = this.searchQuery().trim().toLocaleLowerCase('fr-FR');
    return this.homeExecutionState().publications.filter((item) =>
      !query ||
      [item.media, item.library, item.flow, item.node ?? '', item.detail].some((value) =>
        value.toLocaleLowerCase('fr-FR').includes(query),
      ),
    );
  });
  protected readonly remainingActive = computed(() =>
    Math.max(0, (this.executionState().data?.activeCount ?? 0) - this.activePreview().length),
  );
  protected readonly failedCount = computed(() => this.executionState().data?.failedCount ?? 0);
  protected readonly deferredCount = computed(
    () => this.executionState().data?.deferredCount ?? null,
  );
  protected readonly count = (value: number | null | undefined): string =>
    value == null ? '—' : String(value);
  protected readonly formatNodeLabel = formatNodeLabel;
  protected readonly clusterName = computed(
    () => this.state().snapshot?.identity.name ?? 'Installation',
  );
  private readonly overview = inject(ClusterOverview);
  private aiOverviewInFlight = false;
  private aiOverviewLoaded = false;
  protected readonly state = toSignal(this.overview.state$, {
    initialValue: INITIAL_CLUSTER_OVERVIEW,
  });

  constructor() {
    this.destroyRef.onDestroy(
      this.pageShellContext.register({
        searchPlaceholder: 'Rechercher dans Vue d’ensemble…',
        query: this.searchQuery,
        onSearchQueryChange: (query) => this.searchQuery.set(query),
        searchResults: (query) => this.pageSearchResults(query),
        actions: [
          {
            id: 'create-flow',
            label: 'Créer un flow',
            icon: 'add',
            run: () => void this.openFlowCreation(),
          },
        ],
      }),
    );
    this.refreshAiOverview();
    interval(30_000)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        if (this.overviewView() === 'ai') this.refreshAiOverview();
      });
  }

  private pageSearchResults(query: string): readonly PageShellSearchResult[] {
    const normalized = query.trim().toLocaleLowerCase('fr-FR');
    if (!normalized) return [];
    const matches = (...values: readonly string[]) =>
      values.some((value) => value.toLocaleLowerCase('fr-FR').includes(normalized));
    const nodes = (this.state().snapshot?.nodes ?? [])
      .filter((node) =>
        matches(node.name, node.displayName ?? '', node.roles.join(' '), node.id),
      )
      .slice(0, 4)
      .map((node) => ({
        id: `overview-node:${node.id}`,
        label: node.displayName?.trim() || node.name,
        description: `${node.status === 'online' ? 'En ligne' : 'Hors ligne'} · ${node.roles.join(' · ')}`,
        group: 'Nœuds',
        glyph: '▤',
        path: '/nodes?tab=cluster',
      }));
    const executions = [...this.homeExecutionState().active, ...this.homeExecutionState().publications]
      .filter((item) => matches(item.media, item.library, item.flow, item.node ?? '', item.detail))
      .slice(0, 4)
      .map((item) => ({
        id: `overview-execution:${item.id}`,
        label: item.media,
        description: `${item.library} · ${item.flow}`,
        group: item.state === 'succeeded' ? 'Traitements terminés' : 'Traitements en cours',
        glyph: '▣',
        path: '/activity',
      }));
    const conversations = this.aiConversations()
      .filter((item) => matches(item.title, item.provider, item.source.label))
      .slice(0, 3)
      .map((item) => ({
        id: `overview-ai:${item.id}`,
        label: item.title,
        description: `${this.providerLabel(item.provider)} · ${item.source.label}`,
        group: 'Conversations IA',
        glyph: '✦',
        path: '/ai/assistant',
      }));
    return [...nodes, ...executions, ...conversations].slice(0, 8);
  }

  protected refreshAiOverview(): void {
    if (this.aiOverviewInFlight) return;
    this.aiOverviewInFlight = true;
    if (!this.aiOverviewLoaded) this.aiOverviewLoading.set(true);
    forkJoin({
      options: this.aiTools.getAssistantOptions().pipe(catchError(() => of(null))),
      activity: this.aiTools.listAssistantActivity().pipe(catchError(() => of(null))),
      conversations: this.aiTools.listAssistantConversations().pipe(catchError(() => of(null))),
    }).subscribe(({ options, activity, conversations }) => {
      if (options) this.aiOptions.set(options);
      if (activity) this.aiActivity.set(activity);
      if (conversations) this.aiConversations.set(conversations);
      this.aiOverviewPartial.set(!options || !activity || !conversations);
      this.aiOverviewLoading.set(false);
      this.aiOverviewLoaded = true;
      this.aiOverviewInFlight = false;
    });
  }

  protected providerLabel(provider: string): string {
    const labels: Readonly<Record<string, string>> = {
      openai: 'OpenAI',
      anthropic: 'Anthropic',
      claude: 'Claude',
      ollama: 'Ollama',
      gemini: 'Gemini',
      google: 'Google',
      openrouter: 'OpenRouter',
    };
    return labels[provider.toLocaleLowerCase()] ?? provider;
  }

  protected activityStatusLabel(status: AiAssistantActivity['status']): string {
    const labels: Readonly<Record<AiAssistantActivity['status'], string>> = {
      running: 'En cours',
      succeeded: 'Terminée',
      failed: 'Échec',
      cancelled: 'Annulée',
      'proposal-ready': 'À confirmer',
      'applied-to-editor': 'Appliquée',
      rejected: 'Refusée',
    };
    return labels[status] ?? status;
  }
  protected readonly standalone = computed(
    () => this.state().snapshot?.identity.standalone ?? false,
  );
  protected readonly observedAt = computed(() => this.state().snapshot?.observedAt ?? null);
  protected readonly metrics = computed(() => {
    const data = this.executionState().data;
    const snapshot = this.state().snapshot;
    const executionIsStale = this.executionState().status === 'error';
    const clusterIsStale = this.state().status === 'error';
    return [
      {
        label: 'En cours',
        value: this.count(data?.activeCount),
        detail: 'Traitements en cours d’exécution',
        link: '/activity',
        queryParams: { view: 'active' },
        stale: executionIsStale,
      },
      {
        label: 'En attente',
        value: this.count(data?.queuedCount),
        detail: 'Dans la file de traitement',
        link: '/activity',
        queryParams: { view: 'queue' },
        stale: executionIsStale,
      },
      {
        label: 'Terminés',
        value: this.count(data?.succeededCount),
        detail: 'Total des exécutions réussies',
        link: '/activity',
        queryParams: { view: 'history' },
        stale: executionIsStale,
      },
      {
        label: 'Bibliothèques actives',
        value: snapshot
          ? snapshot.libraries.filter((l) => l.isActive).length + ' / ' + snapshot.libraries.length
          : '—',
        detail: 'Bibliothèques configurées',
        link: '/libraries',
        stale: clusterIsStale,
      },
    ];
  });
  protected readonly nodes = computed(() => {
    const query = this.searchQuery().trim().toLocaleLowerCase('fr-FR');
    return (this.state().snapshot?.nodes ?? []).filter((node) =>
      !query ||
      [node.name, node.displayName ?? '', node.roles.join(' '), node.inventory?.operatingSystem ?? '']
        .some((value) => value.toLocaleLowerCase('fr-FR').includes(query)),
    );
  });
  protected readonly health = computed(() => {
    const snapshot = this.state().snapshot;
    return snapshot
      ? assessClusterHealth(
          snapshot.nodes,
          snapshot.libraries,
          snapshot.accesses,
          snapshot.identity.standalone,
        )
      : null;
  });
  protected readonly matrixRows = computed(() =>
    this.nodes().map((node) => ({
      id: node.id,
      label: formatNodeLabel(node.name, node.displayName),
    })),
  );
  protected readonly matrixColumns = computed(() =>
    (this.state().snapshot?.libraries ?? []).map((library) => ({
      id: library.id,
      label: library.name,
    })),
  );
  protected readonly matrixCells = computed(() => {
    const cells: Record<string, MatrixCell> = {};
    for (const access of this.state().snapshot?.accesses ?? []) {
      cells[access.nodeId + '|' + access.libraryId] = !access.isEnabled
        ? { label: 'Désactivé', tone: 'neutral' }
        : access.mode === 'direct'
          ? { label: 'Accès direct', tone: 'success' }
          : access.mode === 'transfer'
            ? { label: 'Transfert', tone: 'accent' }
            : { label: 'Non pris en charge', tone: 'neutral' };
    }
    return cells;
  });
  protected lifecycleLabel(node: NodeResponse): string {
    return NODE_LIFECYCLE_LABELS[nodeLifecycle(node)];
  }
  protected lifecycleTone(node: NodeResponse) {
    return NODE_LIFECYCLE_TONES[nodeLifecycle(node)];
  }
  protected roleLabel(roles: readonly NodeRole[]): string {
    return roles.map((r) => NODE_ROLE_LABELS[r]).join(' · ');
  }
}
