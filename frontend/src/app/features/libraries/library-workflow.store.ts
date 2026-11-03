import { HttpClient } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import {
  catchError,
  exhaustMap,
  firstValueFrom,
  map,
  of,
  startWith,
  switchMap,
  tap,
} from 'rxjs';
import { LiveUpdates } from '../../shared/services/live-updates';
import { CentralApi } from '../../shared/services/central-api';
import { FlowValidation } from '../../shared/services/flow-validation';
import { NotificationService } from '../../shared/services/notification.service';
import {
  ExecutionTracking,
  INITIAL_EXECUTION_TRACKING,
} from '../../shared/services/execution-tracking';
import { LibraryProcessingSettings } from '../../shared/models/library-workflow';
import { ExecutionResponse } from '../../shared/models/execution';
import { formatFileSize } from '../../shared/models/file-size';
import {
  DataTableColumn,
  DataTableRow,
  DataTableRowAction,
  DataTableSelectionActionEvent,
} from '../../shared/ui/data-table.component';
import type {
  ResourceListItem,
  ResourceListGroup,
} from '../../shared/ui/resource-list.component';
import type { RecordFormData } from '../../shared/ui/record-form.component';
import type { LibraryWorkerAccessResponse } from '../../shared/ui/library-worker-access-types';
import { LibraryCatalogStore } from './library-catalog.store';
import { buildLibrarySheet } from './library-sheet';
import {
  LIBRARY_GROUPS,
  LibraryRecord,
  toLibraryListItem,
} from './library-catalog.utils';

/**
 * Workflow de la bibliothèque sélectionnée : flow attribué, découverte des
 * fichiers, limites de traitement, exécutions récentes et fiche de synthèse.
 */
@Injectable()
export class LibraryWorkflowStore {
  private readonly http = inject(HttpClient);
  private readonly clusterApi = inject(CentralApi);
  private readonly flowApi = inject(FlowValidation);
  private readonly notifications = inject(NotificationService);
  private readonly executionTracking = inject(ExecutionTracking);
  private readonly live = inject(LiveUpdates);
  readonly catalog = inject(LibraryCatalogStore);

  readonly executionTrackingState = toSignal(this.executionTracking.state$, {
    initialValue: INITIAL_EXECUTION_TRACKING,
  });

  readonly processingState = toSignal(
    toObservable(
      computed(() => ({ library: this.catalog.selectedLibrary(), refresh: this.workflowRefresh() })),
    ).pipe(
      switchMap(({ library }) =>
        library
          ? this.live.drive(['libraries', 'executions']).pipe(
              exhaustMap(() =>
                this.clusterApi.libraryProcessing(library.id).pipe(catchError(() => of(null))),
              ),
              startWith(null),
            )
          : of(null),
      ),
    ),
    { initialValue: null },
  );

  readonly processingSummary = computed(() => {
    const settings = this.processingState();
    if (!settings) return 'Chargement des règles de traitement…';
    return (
      (settings.watchEnabled
        ? 'Surveillance automatique · toutes les ' + settings.scanIntervalSeconds + ' s'
        : 'Surveillance automatique arrêtée') +
      ' · ' +
      (settings.maxConcurrentTasks
        ? settings.maxConcurrentTasks + ' traitements simultanés pour toute la bibliothèque'
        : 'Pas de plafond global de bibliothèque')
    );
  });

  readonly processingForm = signal<RecordFormData | null>(null);
  private processingRevision = 0;
  private processingNodeIds: readonly string[] = [];
  readonly workflowBusy = signal(false);
  readonly assignmentForm = signal<RecordFormData | null>(null);
  readonly workflowError = signal('');
  private readonly workflowRefresh = signal(0);
  private readonly assignmentRevision = signal(0);

  readonly workflowState = toSignal(
    toObservable(
      computed(() => ({ library: this.catalog.selectedLibrary(), refresh: this.workflowRefresh() })),
    ).pipe(
      switchMap(({ library }) =>
        library
          ? this.live
              .drive(['libraries', 'executions'])
              .pipe(
                exhaustMap(() =>
                  this.clusterApi.libraryWorkflow(library.id).pipe(
                    tap(() => this.workflowError.set('')),
                    catchError(() => {
                      this.workflowError.set(
                        'Impossible de charger les fichiers et le flow attribué.',
                      );
                      return of(null);
                    }),
                  ),
                ),
              )
              .pipe(startWith(null))
          : of(null),
      ),
    ),
    { initialValue: null },
  );

  readonly workflowLabel = computed(() => {
    const assignment = this.workflowState()?.assignment;
    return assignment ? assignment.name + ' · v' + assignment.version : 'Aucun flow attribué';
  });

  readonly discoveryStatus = computed(() => {
    const scan = this.workflowState()?.scan;
    return (
      this.workflowError() ||
      (!scan
        ? 'Aucune découverte effectuée.'
        : scan.state === 'queued'
          ? 'En attente d’un worker ayant un accès direct en lecture.'
          : scan.detail)
    );
  });

  readonly mediaRows = computed<readonly DataTableRow[]>(() =>
    (this.workflowState()?.files ?? []).map((file) => ({
      id: file.id,
      name: file.relativePath,
      size: formatFileSize(file.bytes),
      modifiedAt: new Date(file.modifiedAt).toLocaleString('fr-FR'),
    })),
  );

  readonly mediaColumns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Fichier', sortable: true },
    { key: 'size', label: 'Taille' },
    { key: 'modifiedAt', label: 'Modifié le' },
  ];

  readonly mediaListActions = [{ id: 'discover', label: 'Découvrir / Actualiser les fichiers' }];

  readonly mediaRowActions: readonly DataTableRowAction[] = [
    {
      id: 'process',
      label: 'Traiter',
      visibleWhen: () =>
        !!this.workflowState()?.assignment &&
        !!this.catalog.selectedLibrary()?.isActive &&
        !this.workflowBusy(),
    },
  ];

  readonly mediaSelectionActions = [
    {
      id: 'process',
      label: 'Traiter la sélection',
      visibleWhen: () =>
        !!this.workflowState()?.assignment &&
        !!this.catalog.selectedLibrary()?.isActive &&
        !this.workflowBusy(),
    },
  ];

  readonly selectedProcessingExecutionId = signal<string | null>(null);

  readonly processingPipelineState = toSignal(
    toObservable(this.selectedProcessingExecutionId).pipe(
      switchMap((id) =>
        id
          ? this.live.drive(['executions', 'execution-logs']).pipe(
              exhaustMap(() =>
                this.executionTracking.pipeline(id).pipe(
                  map((steps) => ({ message: '', steps })),
                  catchError(() =>
                    of({
                      message: 'Impossible de charger les commandes de cette exécution.',
                      steps: [],
                    }),
                  ),
                ),
              ),
              startWith({ message: 'Chargement du pipeline…', steps: [] }),
            )
          : of({ message: '', steps: [] }),
      ),
    ),
    { initialValue: { message: '', steps: [] } },
  );

  readonly libraryExecutions = computed<readonly ExecutionResponse[]>(() => {
    const libraryId = this.catalog.selectedLibrary()?.id;
    return (this.executionTrackingState().data?.executions ?? [])
      .filter((execution) => execution.libraryId === libraryId)
      .slice(0, 25);
  });

  readonly detailTitle = computed(
    () => this.catalog.selectedLibrary()?.name ?? 'Bibliothèque',
  );

  readonly detailDescription = computed(() =>
    this.catalog.selectedLibrary() ? 'Définition et accès aux workers' : 'Détails de la bibliothèque',
  );

  readonly actionTitle = computed(() => {
    switch (this.catalog.actionType()) {
      case 'processing':
        return 'Surveillance et limites';
      case 'flow':
        return 'Attribuer un flow';
      case 'create':
        return this.catalog.createFormData.title;
      case 'edit':
        return 'Modifier ' + (this.catalog.selectedLibrary()?.name ?? 'la bibliothèque');
      default:
        return 'Configurer les accès par worker';
    }
  });

  readonly actionDescription = computed(() =>
    this.catalog.actionType() === 'processing'
      ? 'Configurez la découverte, la mise en file et les plafonds simultanés.'
      : this.catalog.actionType() === 'flow'
        ? 'Choisissez une version publiée pour les traitements de cette bibliothèque.'
        : this.catalog.actionType() === 'create'
          ? this.catalog.createFormData.description
          : this.catalog.actionType() === 'edit'
            ? 'Modifiez le nom de cette définition partagée.'
            : `Définissez quels workers peuvent accéder à « ${this.catalog.selectedLibrary()?.name ?? 'cette bibliothèque'} » et leur mode d’accès.`,
  );

  readonly librarySheet = computed(() =>
    buildLibrarySheet({
      library: this.catalog.selectedLibrary(),
      assignment: this.workflowState()?.assignment,
      processing:
        this.processingState() ?? this.catalog.processingByLibrary()[this.catalog.selectedLibrary()?.id ?? ''] ?? null,
      scan: this.workflowState()?.scan,
      workflowError: this.workflowError(),
      workflowLabel: this.workflowLabel(),
      processingSummary: this.processingSummary(),
      discoveryStatus: this.discoveryStatus(),
      mediaCount: this.mediaRows().length,
    }),
  );

  readonly filteredLibraries = computed<readonly LibraryRecord[]>(() => {
    const selectedId = this.catalog.selectedLibrary()?.id;
    const workflow = this.workflowState();
    const selectedProcessing = this.processingState();
    const processingMap = this.catalog.processingByLibrary();
    const query = this.catalog.searchQuery().trim().toLocaleLowerCase('fr');
    const visible = query
      ? this.catalog
          .libraries()
          .filter(
            (library) =>
              library.id === selectedId ||
              library.name.toLocaleLowerCase('fr').includes(query) ||
              library.nodePaths.toLocaleLowerCase('fr').includes(query) ||
              library.assignedFlow.toLocaleLowerCase('fr').includes(query) ||
              library.state.toLocaleLowerCase('fr').includes(query),
          )
      : this.catalog.libraries();
    return visible.map((library) => {
      const processing =
        library.id === selectedId && selectedProcessing
          ? selectedProcessing
          : processingMap[library.id];
      const assignedFlow =
        library.id === selectedId && workflow
          ? workflow.assignment
            ? `${workflow.assignment.name} · v${workflow.assignment.version}`
            : 'Aucun flow'
          : library.assignedFlow;
      const monitoring = processing
        ? processing.watchEnabled
          ? 'Automatique'
          : 'Manuelle'
        : library.monitoring;
      return {
        ...library,
        assignedFlow,
        monitoring,
      };
    });
  });

  readonly libraryItems = computed<readonly ResourceListItem[]>(() =>
    this.filteredLibraries().map((library) =>
      toLibraryListItem(library, this.catalog.activationBusy()),
    ),
  );

  readonly libraryGroups: readonly ResourceListGroup[] = LIBRARY_GROUPS;

  /** Ouverture de la fiche : réinitialise les états du tiroir et charge les accès. */
  openLibrary(library: LibraryRecord, loadAccesses: (libraryId: string) => void): void {
    this.catalog.selectedLibrary.set(library);
    this.catalog.selectedLibraryTabIndex.set(0);
    this.selectedProcessingExecutionId.set(null);
    loadAccesses(library.id);
  }

  onProcessingExecutionAction(executionId: string): void {
    this.selectedProcessingExecutionId.update((selectedId) =>
      selectedId === executionId ? null : executionId,
    );
  }

  async configureProcessing(): Promise<void> {
    const library = this.catalog.selectedLibrary();
    if (!library || this.workflowBusy()) return;
    this.workflowBusy.set(true);
    try {
      const [settings, nodes, accesses] = await Promise.all([
        firstValueFrom(this.clusterApi.libraryProcessing(library.id)),
        firstValueFrom(this.clusterApi.listNodes()),
        firstValueFrom(
          this.http.get<readonly LibraryWorkerAccessResponse[]>(
            '/api/v1/libraries/' + library.id + '/worker-access',
          ),
        ),
      ]);
      if (this.catalog.selectedLibrary()?.id !== library.id) return;
      const workers = nodes.filter(
        (node) =>
          node.roles.includes('worker') &&
          accesses.some((access) => access.workerId === node.id && access.mode !== 'unsupported'),
      );
      this.processingNodeIds = workers.map((node) => node.id);
      this.processingRevision = settings.revision;
      this.processingForm.set({
        title: 'Surveillance et limites',
        description:
          'Les plafonds se cumulent avec la limite globale de chaque worker. Les champs vides laissent les workers se partager les tâches disponibles.',
        submitLabel: 'Enregistrer',
        initialValues: {
          watch: String(settings.watchEnabled),
          interval: String(settings.scanIntervalSeconds),
          maximum: settings.maxConcurrentTasks == null ? '' : String(settings.maxConcurrentTasks),
          ...Object.fromEntries(
            workers.map((node) => [
              'limit_' + node.id,
              String(
                settings.workerLimits.find((limit) => limit.workerId === node.id)?.maxConcurrentTasks ??
                  '',
              ),
            ]),
          ),
        },
        fields: [
          {
            id: 'watch',
            label: 'Surveillance et mise en file automatiques',
            type: 'select',
            required: true,
            options: [
              { value: 'true', label: 'Activées' },
              { value: 'false', label: 'Arrêtées' },
            ],
          },
          {
            id: 'interval',
            label: 'Intervalle de découverte (secondes)',
            type: 'number',
            required: true,
            min: 15,
            max: 86400,
            step: 1,
          },
          {
            id: 'maximum',
            label: 'Limite globale de la bibliothèque',
            type: 'number',
            min: 1,
            max: 1000,
            step: 1,
            hint: 'Tous workers confondus. Vide : pas de plafond supplémentaire.',
          },
          ...workers.map((node) => ({
            id: 'limit_' + node.id,
            label: 'Limite pour ' + node.name + ' sur cette bibliothèque',
            type: 'number' as const,
            min: 1,
            max: 1000,
            step: 1,
            hint: 'Vide : partage selon les places disponibles sur ce worker.',
          })),
        ],
      });
      this.catalog.actionType.set('processing');
      this.catalog.actionOpen.set(true);
    } catch {
      this.notifications.show('Impossible de charger les règles de traitement.');
    } finally {
      this.workflowBusy.set(false);
    }
  }

  async saveProcessing(values: Record<string, string>): Promise<void> {
    const library = this.catalog.selectedLibrary();
    if (!library || this.workflowBusy()) return;
    this.workflowBusy.set(true);
    const settings: Omit<LibraryProcessingSettings, 'revision'> = {
      watchEnabled: values['watch'] === 'true',
      scanIntervalSeconds: Number(values['interval']),
      maxConcurrentTasks: values['maximum'] ? Number(values['maximum']) : null,
      workerLimits: this.processingNodeIds
        .filter((id) => values['limit_' + id])
        .map((id) => ({ workerId: id, maxConcurrentTasks: Number(values['limit_' + id]) })),
    };
    try {
      await firstValueFrom(
        this.clusterApi.saveLibraryProcessing(library.id, settings, this.processingRevision),
      );
      this.catalog.processingByLibrary.update((current) => ({
        ...current,
        [library.id]: { ...settings, revision: this.processingRevision + 1 },
      }));
      this.workflowRefresh.update((value) => value + 1);
      this.catalog.actionOpen.set(false);
      this.notifications.show('Surveillance et limites enregistrées.');
    } catch {
      this.notifications.show(
        'Enregistrement impossible. Vérifiez les limites et actualisez la configuration.',
      );
    } finally {
      this.workflowBusy.set(false);
    }
  }

  async assignFlow(): Promise<void> {
    const library = this.catalog.selectedLibrary();
    if (!library || this.workflowBusy()) return;
    this.workflowBusy.set(true);
    try {
      const catalog = await firstValueFrom(this.flowApi.list());
      const versions = (
        await Promise.all(
          catalog.filter((flow) => flow.version > 0).map((flow) => this.flowApi.versions(flow.id)),
        )
      ).flat();
      const workflow = await firstValueFrom(this.clusterApi.libraryWorkflow(library.id));
      if (this.catalog.selectedLibrary()?.id !== library.id) return;
      const assignment = workflow.assignment;
      this.assignmentRevision.set(workflow.revision);
      this.assignmentForm.set({
        title: 'Attribuer un flow',
        description:
          'Cette version reste fixe jusqu’à une modification explicite. Les exécutions déjà créées conservent leur définition.',
        submitLabel: 'Enregistrer',
        initialValues: {
          version: assignment ? assignment.flowId + ':' + assignment.version : 'none',
        },
        fields: [
          {
            id: 'version',
            label: 'Flow et version publiée',
            type: 'select',
            required: true,
            options: [
              { value: 'none', label: 'Aucun flow' },
              ...versions.map((flow) => ({
                value: flow.id + ':' + flow.version,
                label: flow.name + ' · v' + flow.version,
              })),
            ],
          },
        ],
      });
      this.catalog.actionType.set('flow');
      this.catalog.actionOpen.set(true);
    } catch {
      this.notifications.show('Impossible de charger les versions publiées.');
    } finally {
      this.workflowBusy.set(false);
    }
  }

  async saveFlowAssignment(values: Record<string, string>): Promise<void> {
    const library = this.catalog.selectedLibrary();
    if (!library || this.workflowBusy()) return;
    const value = values['version'];
    const [flowId, version] = value.split(':');
    this.workflowBusy.set(true);
    try {
      await firstValueFrom(
        this.clusterApi.assignLibraryFlow(
          library.id,
          value === 'none' ? null : flowId,
          value === 'none' ? null : Number(version),
          this.assignmentRevision(),
        ),
      );
      this.catalog.actionOpen.set(false);
      this.notifications.show('Flow de la bibliothèque enregistré.');
      this.workflowRefresh.update((value) => value + 1);
      this.catalog.refresh();
    } catch {
      this.notifications.show('Attribution impossible : la configuration ou la version a changé.');
    } finally {
      this.workflowBusy.set(false);
    }
  }

  async discoverFiles(): Promise<void> {
    const library = this.catalog.selectedLibrary();
    if (!library || this.workflowBusy()) return;
    this.workflowBusy.set(true);
    try {
      await firstValueFrom(this.clusterApi.discoverLibrary(library.id));
      this.notifications.show('Découverte demandée au worker d’accès.');
      this.workflowRefresh.update((value) => value + 1);
    } catch {
      this.notifications.show(
        'Attribuez un flow publié à une bibliothèque active avant la découverte.',
      );
    } finally {
      this.workflowBusy.set(false);
    }
  }

  async processFiles(ids: readonly string[]): Promise<void> {
    const library = this.catalog.selectedLibrary();
    const assignment = this.workflowState()?.assignment;
    if (!library || !assignment || this.workflowBusy()) return;
    this.workflowBusy.set(true);
    try {
      const result = await firstValueFrom(
        this.clusterApi.enqueueLibraryFiles(library.id, ids, assignment.revision),
      );
      this.notifications.show(
        result.queued +
          ' traitement(s) ajouté(s) à la file.' +
          (result.skipped ? ' ' + result.skipped + ' déjà en attente ou en cours.' : ''),
      );
    } catch {
      this.notifications.show(
        'Traitement impossible : actualisez la bibliothèque et le flow attribué.',
      );
    } finally {
      this.workflowBusy.set(false);
    }
  }

  onMediaSelection(event: DataTableSelectionActionEvent): void {
    if (event.actionId === 'process')
      void this.processFiles(event.rows.map((row) => String(row['id'])));
  }
}
