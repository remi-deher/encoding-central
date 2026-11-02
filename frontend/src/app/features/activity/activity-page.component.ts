import { ActivityExecutionSummaryComponent } from './components/activity-execution-summary.component';
import { ActivityFlowTraceComponent } from './components/activity-flow-trace.component';
import { ActivityMetadataViewerComponent } from './components/activity-metadata-viewer.component';
import { ForceSourceRetryDialogComponent } from './components/force-source-retry-dialog.component';
import { ExecutionPipelineStep } from '../../shared/models/execution';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  TemplateRef,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { AuthService } from '../../core/auth/auth.service';
import { ActivatedRoute, Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTabsModule } from '@angular/material/tabs';
import {
  catchError,
  debounceTime,
  exhaustMap,
  filter,
  firstValueFrom,
  map,
  merge,
  of,
  startWith,
  switchMap,
  tap,
  timeout,
  timer,
} from 'rxjs';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import {
  PageShellContextService,
  PageShellSearchResult,
} from '../../core/layout/page-shell-context';
import { ClusterApi } from '../../shared/services/cluster-api';
import { LibraryActivity } from '../../shared/services/library-activity';
import {
  LibraryActivityCounts,
  LibraryActivityFile,
  LibraryActivityResponse,
  LibraryActivityStatus,
  activityExecutionLabel,
  activityExecutionTone,
} from '../../shared/models/library-activity';
import { ExecutionResponse, executionStateLabel } from '../../shared/models/execution';
import { formatFullDateTime, formatRelativeTime } from '../../shared/models/date-format';
import { ExecutionTracking } from '../../shared/services/execution-tracking';
import { ExecutionDetailPolling } from '../../shared/services/execution-detail-polling';
import { FileDownload } from '../../shared/services/file-download';
import { NotificationService } from '../../shared/services/notification.service';
import {
  ConfirmationDialogComponent,
  ConfirmationDialogData,
} from '../../shared/ui/confirmation-dialog.component';
import {
  DataTableColumn,
  DataTableComponent,
  DataTableRow,
} from '../../shared/ui/data-table.component';
import { DetailDrawerComponent } from '../../shared/ui/detail-drawer/detail-drawer';
import { ExecutionPipelineLogsComponent } from '../../shared/ui/execution-pipeline-logs.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { StatusBadgeComponent } from '../../shared/ui/status-badge.component';
import { SubnavComponent, SubnavOption } from '../../shared/ui/subnav.component';
import { SubnavTemplateComponent } from '../../shared/templates/subnav-template.component';
import { MatDialog } from '@angular/material/dialog';

const ACTIVITY_STATUSES: readonly { value: LibraryActivityStatus; label: string }[] = [
  { value: 'untreated', label: 'À traiter' },
  { value: 'active', label: 'En cours' },
  { value: 'done', label: 'Déjà traité' },
  { value: 'failed', label: 'Erreurs' },
  { value: 'cancelled', label: 'Annulées' },
  { value: 'disabled', label: 'Désactivés' },
];
const PAGE_SIZE = 50;
const PAGE_REFRESH_MS = 10000;
interface ActivityPageState {
  readonly status: 'loading' | 'ready' | 'error';
  readonly data: LibraryActivityResponse | null;
  readonly revision: number;
}

function validStatus(value: string | null): value is LibraryActivityStatus {
  return ACTIVITY_STATUSES.some((status) => status.value === value);
}

function statusFromLegacyView(view: string | null): LibraryActivityStatus {
  switch (view) {
    case 'active':
      return 'active';
    case 'errors':
      return 'failed';
    case 'cancelled':
      return 'cancelled';
    case 'history':
      return 'done';
    default:
      return 'untreated';
  }
}

function viewFromStatus(status: LibraryActivityStatus): string {
  switch (status) {
    case 'active':
      return 'active';
    case 'failed':
      return 'errors';
    case 'cancelled':
      return 'cancelled';
    case 'done':
      return 'history';
    default:
      return 'queue';
  }
}

function baseName(path: string): string {
  return path.split(/[\\/]/).pop() || path;
}

function activityApiError(error: unknown, fallback: string): string {
  if (error instanceof HttpErrorResponse) {
    const detail = (error.error as { detail?: unknown } | null)?.detail;
    if (typeof detail === 'string' && detail.trim()) return detail;
  }
  return AuthService.errorMessage(error, fallback);
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-activity-page',
  imports: [
    PageHeaderComponent,
    DetailDrawerComponent,
    ExecutionPipelineLogsComponent,
    IconComponent,
    MatButtonModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatProgressBarModule,
    MatSelectModule,
    MatTabsModule,
    SubnavComponent,
    SubnavTemplateComponent,
    StatusBadgeComponent,
    DataTableComponent,
    ActivityExecutionSummaryComponent,
    ActivityFlowTraceComponent,
    ActivityMetadataViewerComponent,
  ],
  templateUrl: './activity-page.component.html',
  styleUrl: './activity-page.component.scss',
})
export class ActivityPageComponent {
  protected readonly activityExecutionLabel = activityExecutionLabel;
  protected readonly activityExecutionTone = activityExecutionTone;
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly pageShellContext = inject(PageShellContextService);
  private readonly activityApi = inject(LibraryActivity);
  private readonly clusterApi = inject(ClusterApi);
  private readonly tracking = inject(ExecutionTracking);
  private readonly detailPolling = inject(ExecutionDetailPolling);
  private readonly downloads = inject(FileDownload);
  private readonly dialog = inject(MatDialog);
  private readonly notifications = inject(NotificationService);
  private readonly auth = inject(AuthService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly statuses = ACTIVITY_STATUSES;
  protected readonly status = signal<LibraryActivityStatus>(
    validStatus(this.route.snapshot.queryParamMap.get('state'))
      ? (this.route.snapshot.queryParamMap.get('state') as LibraryActivityStatus)
      : statusFromLegacyView(this.route.snapshot.queryParamMap.get('view')),
  );
  protected readonly search = signal('');
  protected readonly libraryFilter = signal<string | null>(
    this.route.snapshot.queryParamMap.get('library'),
  );
  protected readonly flowFilter = signal<string | null>(
    this.route.snapshot.queryParamMap.get('flow'),
  );
  protected readonly nodeFilter = signal<string | null>(
    this.route.snapshot.queryParamMap.get('node'),
  );
  protected readonly filtersOpen = signal(false);
  protected readonly refreshRevision = signal(0);
  protected readonly pageIndex = signal(0);
  protected readonly actionBusy = signal(false);
  protected readonly feedback = signal('');
  protected readonly selectedIds = signal<ReadonlySet<string>>(new Set());
  protected readonly selectedActionIds = this.selectedIds;
  protected readonly selectedFile = signal<LibraryActivityFile | null>(null);
  protected readonly selectedExecution = signal<ExecutionResponse | null>(null);
  protected readonly executionLoading = signal(false);
  protected readonly selectedDetailTab = signal(0);
  private readonly lastSuccessfulPage = signal<{
    readonly key: string;
    readonly data: LibraryActivityResponse;
  } | null>(null);

  protected readonly libraries = toSignal(
    this.clusterApi.listLibraries().pipe(catchError(() => of([]))),
    {
      initialValue: [],
    },
  );
  protected readonly nodes = toSignal(this.clusterApi.listNodes().pipe(catchError(() => of([]))), {
    initialValue: [],
  });
  protected readonly flowOptions = computed(() =>
    [
      ...new Set(
        this.libraries()
          .map((library) => library.flowName)
          .filter((name): name is string => !!name),
      ),
    ].sort((a, b) => a.localeCompare(b, 'fr-FR')),
  );

  private readonly debouncedSearch = toSignal(toObservable(this.search).pipe(debounceTime(150)), {
    initialValue: this.search(),
  });

  protected readonly offset = computed(() => this.pageIndex() * PAGE_SIZE);

  private readonly request = computed(() => ({
    status: this.status(),
    libraryId: this.libraryFilter() ?? undefined,
    flow: this.flowFilter() ?? undefined,
    nodeId: this.nodeFilter() ?? undefined,
    search: this.debouncedSearch(),
    limit: PAGE_SIZE,
    offset: this.offset(),
  }));
  private readonly requestKey = computed(() =>
    JSON.stringify({
      status: this.status(),
      libraryId: this.libraryFilter(),
      flow: this.flowFilter(),
      node: this.nodeFilter(),
      search: this.debouncedSearch().trim(),
      page: this.pageIndex(),
    }),
  );

  private readonly statusCache = new Map<string, LibraryActivityResponse>();
  private readonly cachedCounts = signal<LibraryActivityCounts | null>(null);
  protected readonly isRefreshing = signal(false);

  private readonly pageState = toSignal(
    toObservable(
      computed(() => ({ query: this.request(), revision: this.refreshRevision() })),
    ).pipe(
      switchMap(({ query, revision }) => {
        const key = this.requestKey();
        const cached =
          this.statusCache.get(key) ??
          (this.lastSuccessfulPage()?.key === key ? this.lastSuccessfulPage()!.data : null);

        const stream$ = this.activityApi.stream(query).pipe(
          tap((data) => {
            this.statusCache.set(key, data);
            this.cachedCounts.set(data.counts);
            this.lastSuccessfulPage.set({ key, data });
            this.isRefreshing.set(false);
          }),
          map((data): ActivityPageState => ({ status: 'ready', data, revision })),
          catchError(() => of(null)),
        );

        const realTimeEvents$ = this.tracking.state$.pipe(
          filter((s) => s.status === 'ready'),
          map(() => void 0),
        );

        const poll$ = merge(timer(0, PAGE_REFRESH_MS), realTimeEvents$).pipe(
          exhaustMap(() => {
            this.isRefreshing.set(true);
            return this.activityApi.list(query).pipe(
              timeout(15000),
              tap((data) => {
                this.statusCache.set(key, data);
                this.cachedCounts.set(data.counts);
                this.lastSuccessfulPage.set({ key, data });
                this.isRefreshing.set(false);
              }),
              map((data): ActivityPageState => ({ status: 'ready', data, revision })),
              catchError(() => {
                this.isRefreshing.set(false);
                const fallback =
                  this.statusCache.get(key) ??
                  (this.lastSuccessfulPage()?.key === key ? this.lastSuccessfulPage()!.data : null);
                return of({
                  status: 'error',
                  data: fallback,
                  revision,
                } as ActivityPageState);
              }),
            );
          }),
        );

        const active$ = merge(
          poll$,
          stream$.pipe(filter((s): s is ActivityPageState => s !== null)),
        );

        if (cached) {
          // Affichage immédiat (0 ms) depuis le cache pour supprimer tout écran de chargement
          return active$.pipe(
            startWith({ status: 'ready', data: cached, revision } as ActivityPageState),
          );
        }

        this.isRefreshing.set(true);
        return active$.pipe(
          startWith({ status: 'loading', data: null, revision } as ActivityPageState),
        );
      }),
    ),
    { initialValue: { status: 'loading', data: null, revision: 0 } as ActivityPageState },
  );

  protected readonly files = computed(() => this.pageState().data?.files ?? []);
  protected readonly counts = computed(() => this.pageState().data?.counts ?? this.cachedCounts());
  protected readonly totalCount = computed(() => this.pageState().data?.totalCount ?? 0);
  protected readonly totalPages = computed(() =>
    Math.max(1, Math.ceil(this.totalCount() / PAGE_SIZE)),
  );
  protected readonly pageRangeText = computed(() => {
    const total = this.totalCount();
    if (total === 0) return '0 fichier';
    const start = this.pageIndex() * PAGE_SIZE + 1;
    const end = Math.min((this.pageIndex() + 1) * PAGE_SIZE, total);
    return `Page ${this.pageIndex() + 1} sur ${this.totalPages()} (${start}–${end} sur ${total})`;
  });

  protected nextPage(): void {
    if (this.pageIndex() + 1 < this.totalPages()) {
      this.pageIndex.update((p) => p + 1);
      this.clearSelection();
    }
  }

  protected prevPage(): void {
    if (this.pageIndex() > 0) {
      this.pageIndex.update((p) => p - 1);
      this.clearSelection();
    }
  }

  protected readonly statusOptions = computed<readonly SubnavOption[]>(() =>
    ACTIVITY_STATUSES.map((item) => ({
      value: item.value,
      label: item.label,
      count: this.counts()?.[item.value],
    })),
  );
  protected readonly countText = computed(() => {
    const count = this.counts();
    if (!count) return 'Suivi des fichiers';
    const total =
      count.untreated +
      count.active +
      count.done +
      count.failed +
      (count.cancelled ?? 0) +
      count.disabled;
    return `${total} fichier${total === 1 ? '' : 's'} suivi${total === 1 ? '' : 's'}`;
  });
  protected readonly currentStatusLabel = computed(
    () => ACTIVITY_STATUSES.find((item) => item.value === this.status())?.label ?? 'Activité',
  );

  protected readonly selectedFiles = computed(() => {
    const ids = this.selectedIds();
    if (!ids.size) return [];
    return this.files().filter((file) => ids.has(file.id));
  });

  protected readonly selectedCount = computed(() => this.selectedIds().size);

  protected readonly isAllSelected = computed(() => {
    const list = this.files();
    return list.length > 0 && list.every((file) => this.selectedIds().has(file.id));
  });

  protected readonly isPartiallySelected = computed(() => {
    const list = this.files();
    const count = list.filter((file) => this.selectedIds().has(file.id)).length;
    return count > 0 && count < list.length;
  });

  protected readonly selectedCancelableFiles = computed(() =>
    this.selectedFiles().filter(
      (file) =>
        file.executionId &&
        ['running', 'queued', 'cancelling'].includes(file.executionState ?? ''),
    ),
  );

  protected readonly selectedRetriableFiles = computed(() =>
    this.selectedFiles().filter(
      (file) =>
        (file.executionId && ['failed', 'cancelled'].includes(file.executionState ?? '')) ||
        file.status === 'done' ||
        file.status === 'untreated',
    ),
  );

  private readonly detailState = this.detailPolling.observe(
    toObservable(computed(() => this.selectedExecution()?.id ?? null)),
    toObservable(this.selectedDetailTab),
  );
  protected readonly eventState = toSignal(this.detailState.events, {
    initialValue: { message: 'Chargement de la chronologie…', rows: [] },
  });
  protected readonly artifactsState = toSignal(this.detailState.artifacts, {
    initialValue: { message: '', rows: [] },
  });
  protected readonly pipelineState = toSignal(this.detailState.pipeline, {
    initialValue: { message: '', steps: [] },
  });

  protected readonly drawerHeaderActions = viewChild<TemplateRef<unknown>>('detailHeaderActions');
  protected readonly drawerContent = viewChild<TemplateRef<unknown>>('detailContent');
  protected readonly drawerFooter = viewChild<TemplateRef<unknown>>('detailFooter');
  protected readonly drawerTitle = computed(() => {
    const file = this.selectedFile();
    return file ? baseName(file.relativePath) : 'Détails du fichier';
  });
  protected readonly drawerDescription = computed(() => {
    const file = this.selectedFile();
    if (!file) return '';
    return [file.library, this.statusLabel(file.status), file.flow].filter(Boolean).join(' · ');
  });
  protected readonly canCancel = computed(() =>
    ['queued', 'running'].includes(this.selectedExecution()?.state ?? ''),
  );
  protected readonly canRetry = computed(() => this.selectedExecution()?.state === 'failed');
  protected readonly canResume = computed(() => {
    const execution = this.selectedExecution();
    return execution?.state === 'cancelled' && execution.ownership?.state !== 'quarantined';
  });
  protected readonly canForceSourceRetry = computed(() => {
    const file = this.selectedFile();
    const execution = this.selectedExecution();
    const forceableState =
      execution?.state === 'queued' ||
      (execution?.state === 'cancelled' && execution.startedAt === null);
    return (
      this.auth.isAdmin() &&
      file !== null &&
      execution !== null &&
      forceableState &&
      file.libraryActive &&
      !!file.quarantineReason &&
      !!file.conflictingExecutionId &&
      execution.id === file.executionId &&
      execution.ownership?.state === 'quarantined'
    );
  });

  protected readonly eventColumns: readonly DataTableColumn[] = [
    { key: 'revision', label: 'Ordre' },
    { key: 'createdAt', label: 'Date' },
    { key: 'state', label: 'État' },
    { key: 'detail', label: 'Détail' },
  ];
  protected readonly artifactColumns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Fichier' },
    { key: 'size', label: 'Taille' },
  ];
  protected readonly artifactActions = [{ id: 'download', label: 'Télécharger' }];
  protected readonly videoEncodingColumns: readonly DataTableColumn[] = [
    { key: 'flowNode', label: 'Bloc' },
    { key: 'codec', label: 'Codec' },
    { key: 'encoder', label: 'Encodeur' },
    { key: 'ffmpeg', label: 'Version FFmpeg' },
    { key: 'acceleration', label: 'Accélération' },
    { key: 'quality', label: 'Qualité appliquée' },
    { key: 'preset', label: 'Preset appliqué' },
    { key: 'profile', label: 'Profil figé' },
    { key: 'worker', label: 'Identifiant worker' },
  ];
  protected readonly videoEncodingRows = computed<readonly DataTableRow[]>(() =>
    (this.selectedExecution()?.videoEncodings ?? []).map((encoding, index) => ({
      id: `${encoding.flowNodeId}-${encoding.encoderId}-${index}`,
      flowNode: encoding.flowNodeId,
      worker: encoding.workerId,
      codec: encoding.codec.toUpperCase(),
      encoder: encoding.encoderId,
      ffmpeg: encoding.ffmpegVersion,
      acceleration: encoding.acceleration,
      quality: `${encoding.qualityMode} (${encoding.qualityOption}) : ${encoding.quality}`,
      preset:
        encoding.presetOption && encoding.presetValue
          ? `${encoding.presetOption} : ${encoding.presetValue}`
          : 'Sans preset',
      profile: `${encoding.profileName} · v${encoding.profileVersion}`,
    })),
  );

  constructor() {
    this.destroyRef.onDestroy(
      this.pageShellContext.register({
        searchPlaceholder: 'Rechercher dans Activité…',
        query: this.search,
        onSearchQueryChange: (query) => this.search.set(query),
        searchResults: (query) => this.searchResults(query),
        filter: {
          label: 'Filtres',
          activeCount: () =>
            Number(Boolean(this.libraryFilter())) +
            Number(Boolean(this.flowFilter())) +
            Number(Boolean(this.nodeFilter())),
          toggle: () => this.filtersOpen.update((open) => !open),
        },
      }),
    );
    this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((params) => {
      const requested = params.get('state');
      const state = validStatus(requested) ? requested : statusFromLegacyView(params.get('view'));
      if (state !== this.status()) this.status.set(state);
      const library = params.get('library');
      if (library !== this.libraryFilter()) this.libraryFilter.set(library);
      const flow = params.get('flow');
      if (flow !== this.flowFilter()) this.flowFilter.set(flow);
      const node = params.get('node');
      if (node !== this.nodeFilter()) this.nodeFilter.set(node);
    });
  }

  protected setStatus(value: string): void {
    if (!validStatus(value) || value === this.status()) return;
    this.status.set(value);
    this.pageIndex.set(0);
    this.clearSelection();
    this.close();
    this.syncQueryParams();
  }

  protected setLibrary(value: string): void {
    const libraryId = value || null;
    if (libraryId === this.libraryFilter()) return;
    this.libraryFilter.set(libraryId);
    this.pageIndex.set(0);
    this.clearSelection();
    this.close();
    this.syncQueryParams();
  }

  protected setFlow(value: string): void {
    const flow = value || null;
    if (flow === this.flowFilter()) return;
    this.flowFilter.set(flow);
    this.pageIndex.set(0);
    this.clearSelection();
    this.close();
    this.syncQueryParams();
  }

  protected setNode(value: string): void {
    const node = value || null;
    if (node === this.nodeFilter()) return;
    this.nodeFilter.set(node);
    this.pageIndex.set(0);
    this.clearSelection();
    this.close();
    this.syncQueryParams();
  }

  protected clearFilters(): void {
    this.libraryFilter.set(null);
    this.flowFilter.set(null);
    this.nodeFilter.set(null);
    this.pageIndex.set(0);
    this.clearSelection();
    this.close();
    this.syncQueryParams();
  }

  protected toggleSelectAll(checked: boolean): void {
    const list = this.files();
    this.selectedIds.update((current) => {
      const next = new Set(current);
      if (checked) {
        for (const file of list) next.add(file.id);
      } else {
        for (const file of list) next.delete(file.id);
      }
      return next;
    });
  }

  protected toggleFileSelection(file: LibraryActivityFile, checked: boolean): void {
    this.selectedIds.update((current) => {
      const next = new Set(current);
      if (checked) next.add(file.id);
      else next.delete(file.id);
      return next;
    });
  }

  protected clearSelection(): void {
    this.selectedIds.set(new Set());
  }

  /** @deprecated use toggleFileSelection */
  protected toggleActionSelection(file: LibraryActivityFile, checked: boolean): void {
    this.toggleFileSelection(file, checked);
  }

  /** @deprecated use clearSelection */
  protected clearActionSelection(): void {
    this.clearSelection();
  }

  protected open(file: LibraryActivityFile): void {
    this.selectedFile.set(file);
    this.selectedExecution.set(null);
    this.selectedDetailTab.set(0);
    this.executionLoading.set(Boolean(file.executionId));
    if (!file.executionId) return;
    this.tracking
      .execution(file.executionId)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        catchError(() => of(null)),
      )
      .subscribe((execution) => {
        if (this.selectedFile()?.id !== file.id) return;
        this.selectedExecution.set(execution);
        this.executionLoading.set(false);
      });
  }

  protected close(): void {
    this.selectedFile.set(null);
    this.selectedExecution.set(null);
    this.executionLoading.set(false);
  }

  protected rowTime(file: LibraryActivityFile): string {
    const instant = file.updatedAt ?? file.modifiedAt;
    return formatRelativeTime(instant);
  }

  protected fileName(path: string): string {
    return baseName(path);
  }

  protected statusLabel(value: LibraryActivityStatus): string {
    return ACTIVITY_STATUSES.find((status) => status.value === value)?.label ?? value;
  }

  protected statusTone(
    value: LibraryActivityStatus,
  ): 'accent' | 'neutral' | 'danger' | 'success' | 'warning' {
    switch (value) {
      case 'active':
        return 'accent';
      case 'done':
        return 'success';
      case 'failed':
        return 'danger';
      case 'cancelled':
        return 'warning';
      case 'disabled':
        return 'neutral';
      default:
        return 'neutral';
    }
  }

  protected async runBulkCancel(): Promise<void> {
    const targets = this.selectedCancelableFiles();
    if (!targets.length || this.actionBusy()) return;
    const accepted = await firstValueFrom(
      this.dialog
        .open(ConfirmationDialogComponent, {
          data: {
            title: `Annuler ${targets.length} tâche(s) ?`,
            message: `${targets.length} tâche(s) recevront une demande d’interruption immédiate.`,
            confirmLabel: 'Annuler les tâches',
            variant: 'danger',
          } satisfies ConfirmationDialogData,
        })
        .afterClosed(),
    );
    if (!accepted) return;
    this.actionBusy.set(true);
    const results = await Promise.allSettled(
      targets.map((file) => firstValueFrom(this.tracking.cancel(file.executionId!))),
    );
    this.actionBusy.set(false);
    this.clearSelection();
    this.tracking.refresh();
    this.refreshRevision.update((value) => value + 1);
    const succeeded = results.filter((result) => result.status === 'fulfilled').length;
    this.notifications.show(`${succeeded}/${targets.length} tâche(s) annulée(s).`);
  }

  protected async runBulkRetry(): Promise<void> {
    const targets = this.selectedRetriableFiles();
    if (!targets.length || this.actionBusy()) return;
    const accepted = await firstValueFrom(
      this.dialog
        .open(ConfirmationDialogComponent, {
          data: {
            title: `Relancer ${targets.length} tâche(s) ?`,
            message: `Une relance va être demandée pour les ${targets.length} élément(s) sélectionné(s).`,
            confirmLabel: 'Relancer les tâches',
          } satisfies ConfirmationDialogData,
        })
        .afterClosed(),
    );
    if (!accepted) return;
    this.actionBusy.set(true);

    const promises: Promise<unknown>[] = [];

    const failedFiles = targets.filter((f) => f.executionId && f.executionState === 'failed');
    for (const f of failedFiles) {
      promises.push(firstValueFrom(this.tracking.retry(f.executionId!)));
    }

    const cancelledFiles = targets.filter(
      (f) => f.executionId && f.executionState === 'cancelled',
    );
    for (const f of cancelledFiles) {
      promises.push(firstValueFrom(this.tracking.resume(f.executionId!)));
    }

    const enqueuableFiles = targets.filter(
      (f) =>
        (!f.executionId || !['failed', 'cancelled'].includes(f.executionState ?? '')) &&
        (f.status === 'untreated' || f.status === 'done'),
    );
    if (enqueuableFiles.length > 0) {
      const byLibrary = new Map<string, string[]>();
      for (const f of enqueuableFiles) {
        const list = byLibrary.get(f.libraryId) ?? [];
        list.push(f.id);
        byLibrary.set(f.libraryId, list);
      }
      for (const [libId, fileIds] of byLibrary.entries()) {
        promises.push(firstValueFrom(this.clusterApi.enqueueLibraryFiles(libId, fileIds, 1)));
      }
    }

    const results = await Promise.allSettled(promises);
    this.actionBusy.set(false);
    this.clearSelection();
    this.tracking.refresh();
    this.refreshRevision.update((value) => value + 1);
    const succeeded = results.filter((result) => result.status === 'fulfilled').length;
    this.notifications.show(`${succeeded}/${targets.length} tâche(s) relancée(s).`);
  }

  protected cancelSelected(): void {
    const execution = this.selectedExecution();
    if (!execution) return;
    this.confirm(
      {
        title: 'Annuler cette exécution ?',
        message: `« ${execution.media} » recevra une demande d’annulation. Un traitement en cours attend la confirmation du worker.`,
        confirmLabel: 'Annuler l’exécution',
        variant: 'danger',
      },
      () => this.perform('cancel', execution.id),
    );
  }

  protected retrySelected(): void {
    const execution = this.selectedExecution();
    if (!execution) return;
    this.confirm(
      {
        title: 'Créer une nouvelle tentative ?',
        message: `Une nouvelle exécution de « ${execution.media} » sera ajoutée à la file. L’échec restera dans l’historique.`,
        confirmLabel: 'Mettre en file',
      },
      () => this.perform('retry', execution.id),
    );
  }

  protected resumeSelected(): void {
    const execution = this.selectedExecution();
    if (!execution || !this.canResume()) return;
    this.confirm(
      {
        title: 'Remettre ce fichier en file ?',
        message: `Une nouvelle tentative repartira de la source de « ${execution.media} ». La tentative annulée restera dans l’historique. Le serveur refusera la reprise si une autre tentative du même fichier est encore active ou en quarantaine.`,
        confirmLabel: 'Remettre en file',
      },
      () => this.perform('resume', execution.id),
    );
  }

  protected forceSourceRetrySelected(): void {
    const file = this.selectedFile();
    const execution = this.selectedExecution();
    if (!file || !execution || !this.canForceSourceRetry() || this.actionBusy()) return;
    this.dialog
      .open(ForceSourceRetryDialogComponent, {
        data: { media: this.fileName(file.relativePath) },
      })
      .afterClosed()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((attestation) => {
        if (!attestation) return;
        this.actionBusy.set(true);
        this.clusterApi
          .forceSourceRetry(execution.id, attestation.confirmedStopped, attestation.reason)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
            next: (result) => {
              this.actionBusy.set(false);
              this.feedback.set(result.detail);
              this.refreshRevision.update((value) => value + 1);
              this.tracking.refresh();
              this.notifications.show(result.detail);
            },
            error: (error: unknown) => {
              this.actionBusy.set(false);
              const message = activityApiError(
                error,
                'La reprise forcée a échoué ou la tentative a changé d’état.',
              );
              this.feedback.set(message);
              this.notifications.show(message);
            },
          });
      });
  }

  protected downloadArtifact(row: DataTableRow): void {
    const id = this.selectedExecution()?.id;
    if (id)
      this.downloads.fromUrl(
        '/api/v1/executions/' +
          encodeURIComponent(id) +
          '/artifacts/' +
          encodeURIComponent(String(row['id'])),
      );
  }

  private searchResults(query: string): readonly PageShellSearchResult[] {
    const normalized = query.trim().toLocaleLowerCase('fr-FR');
    return this.files()
      .filter(
        (file) =>
          !normalized ||
          [file.relativePath, file.library, file.flow, file.node, this.statusLabel(file.status)]
            .filter(Boolean)
            .join(' ')
            .toLocaleLowerCase('fr-FR')
            .includes(normalized),
      )
      .slice(0, 8)
      .map((file) => ({
        id: `activity:${file.id}`,
        label: baseName(file.relativePath),
        description: `${file.library} · ${this.statusLabel(file.status)}`,
        group: 'Activité',
        glyph: '▣',
        run: () => this.open(file),
      }));
  }

  private syncQueryParams(): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        state: this.status(),
        view: viewFromStatus(this.status()),
        library: this.libraryFilter(),
        flow: this.flowFilter(),
        node: this.nodeFilter(),
        type: null,
        request: null,
      },
      queryParamsHandling: 'merge',
    });
  }

  private confirm(data: ConfirmationDialogData, onConfirm: () => void): void {
    this.dialog
      .open(ConfirmationDialogComponent, { data })
      .afterClosed()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((confirmed: boolean | undefined) => {
        if (confirmed) onConfirm();
      });
  }

  private perform(action: 'cancel' | 'retry' | 'resume', id: string): void {
    if (this.actionBusy()) return;
    this.actionBusy.set(true);
    const succeed = () => {
      this.actionBusy.set(false);
      this.refreshRevision.update((value) => value + 1);
      this.tracking.refresh();
      this.notifications.show(
        action === 'cancel'
          ? 'Demande d’annulation enregistrée.'
          : action === 'resume'
            ? 'Nouvelle tentative depuis la source mise en file.'
            : 'Nouvelle tentative mise en file.',
      );
      if (action === 'retry') this.setStatus('active');
      else if (action === 'resume') this.setStatus('untreated');
    };
    const fail = () => {
      this.actionBusy.set(false);
      this.feedback.set('La demande a échoué ou l’état de l’exécution a changé.');
    };
    if (action === 'cancel')
      this.tracking
        .cancel(id)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({ next: succeed, error: fail });
    else if (action === 'retry')
      this.tracking
        .retry(id)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({ next: succeed, error: fail });
    else
      this.tracking
        .resume(id)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({ next: succeed, error: fail });
  }

  protected downloadExecutionLog(): void {
    const execution = this.selectedExecution();
    if (!execution) return;
    const steps = this.pipelineState().steps;
    const content = this.serializeExecutionLogs(execution, steps);
    const fileName =
      'execution-' +
      execution.id.slice(0, 8) +
      '-' +
      baseName(execution.media).replace(/[^a-zA-Z0-9._-]/g, '_') +
      '.log';
    this.downloads.save(new Blob([content], { type: 'text/plain;charset=utf-8' }), fileName);
    this.notifications.show('Journal de traitement téléchargé.');
  }

  private executionDuration(execution: ExecutionResponse): string {
    if (!execution.startedAt) return '—';
    const end = execution.finishedAt ? Date.parse(execution.finishedAt) : Date.now();
    const elapsed = Math.max(0, end - Date.parse(execution.startedAt));
    const seconds = Math.floor(elapsed / 1000);
    if (seconds < 60) return seconds + ' s';
    const minutes = Math.floor(seconds / 60);
    const remainingSeconds = seconds % 60;
    return minutes + ' min ' + remainingSeconds + ' s';
  }

  private stepDuration(step: ExecutionPipelineStep): string {
    if (!step.startedAt) return '—';
    const end = step.completedAt ? Date.parse(step.completedAt) : Date.now();
    const elapsed = Math.max(0, end - Date.parse(step.startedAt));
    if (elapsed < 1000) return elapsed + ' ms';
    const seconds = Math.floor(elapsed / 1000);
    return seconds + ' s';
  }

  private serializeExecutionLogs(
    execution: ExecutionResponse,
    steps: readonly ExecutionPipelineStep[],
  ): string {
    const lines: string[] = [
      '================================================================================',
      "Encoding — Journal d'exécution",
      '================================================================================',
      'Identifiant : ' + execution.id,
      'Fichier     : ' + execution.media,
      'Bibliothèque: ' + execution.library,
      'Flow        : ' + execution.flow + ' (v' + execution.flowVersion + ')',
      'Worker      : ' + (execution.node ?? '—'),
      'État        : ' + executionStateLabel(execution),
      'Progression : ' + (execution.progress != null ? execution.progress + '%' : '—'),
      'Démarré     : ' + (execution.startedAt ? formatFullDateTime(execution.startedAt) : '—'),
      'Terminé     : ' + (execution.finishedAt ? formatFullDateTime(execution.finishedAt) : '—'),
      'Durée       : ' + this.executionDuration(execution),
      'Sortie      : ' + (execution.publishedPath ?? '—'),
      '================================================================================\n',
    ];

    if (steps.length === 0) {
      lines.push('Aucune étape de pipeline enregistrée.');
    } else {
      steps.forEach((step, index) => {
        lines.push(
          '--------------------------------------------------------------------------------',
        );
        lines.push(
          'Étape #' +
            (index + 1) +
            ' : ' +
            (step.nodeName || step.nodeId) +
            ' (' +
            step.nodeId +
            ')',
        );
        lines.push('Type        : ' + step.componentKind);
        lines.push('Statut      : ' + step.status);
        lines.push('Durée       : ' + this.stepDuration(step));
        if (step.outputLabel) lines.push('Sortie      : ' + step.outputLabel);
        if (step.nextNodeId) lines.push('Bloc suivant: ' + step.nextNodeId);
        if (step.diagnostic)
          lines.push('Diagnostic  : [' + step.diagnostic.code + '] ' + step.diagnostic.message);
        lines.push(
          '--------------------------------------------------------------------------------',
        );
        if (step.commands.length === 0) {
          lines.push('Aucune commande système enregistrée.\n');
        } else {
          for (const cmd of step.commands) {
            lines.push('> ' + cmd.command);
            lines.push('  Sortie : code ' + (cmd.exitCode ?? '—'));
            if (cmd.standardOutput) {
              lines.push('  --- stdout ---');
              lines.push(cmd.standardOutput);
            }
            if (cmd.standardError) {
              lines.push('  --- stderr ---');
              lines.push(cmd.standardError);
            }
            lines.push('');
          }
        }
      });
    }

    return lines.join('\n');
  }
}
