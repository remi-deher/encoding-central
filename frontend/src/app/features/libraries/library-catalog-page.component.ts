import { LibraryProcessingSettings } from '../../shared/models/library-workflow';
import { FlowValidation } from '../../shared/services/flow-validation';

import { ClusterApi } from '../../shared/services/cluster-api';

import { LibraryResponse, NodeGroupResponse } from '../../shared/models/cluster';

import { HttpClient } from '@angular/common/http';

import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  untracked,
  signal,
  viewChildren,
  viewChild,
} from '@angular/core';

import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';

import { toSignal, toObservable } from '@angular/core/rxjs-interop';

import { ActivatedRoute, Router } from '@angular/router';

import {
  catchError,
  EMPTY,
  finalize,
  forkJoin,
  map,
  of,
  timer,
  switchMap,
  exhaustMap,
  firstValueFrom,
  tap,
  startWith,
} from 'rxjs';

import {
  DataTableColumn,
  DataTableComponent,
  DataTableActionEvent,
  DataTableRow,
  DataTableRowAction,
  DataTableToggleEvent,
  DataTableSelectionActionEvent,
} from '../../shared/ui/data-table.component';

import {
  ResourceListGroup,
  ResourceListItem,
  ResourceListTag,
  ResourceListToggleEvent,
} from '../../shared/ui/resource-list.component';

import { LibraryPreparationTemplateComponent } from '../../shared/templates/library-preparation-template.component';

import {
  ResourceSheetComponent,
  ResourceSheetNotice,
  ResourceSheetSection,
} from '../../shared/ui/resource-sheet.component';

import { RecordFormActionsComponent } from '../../shared/ui/record-form-actions.component';
import { RecordFormComponent, RecordFormData } from '../../shared/ui/record-form.component';

import {
  LibraryNodeAccessFormComponent,
  LibraryNodeAccessOption,
  LibraryNodeAccessValue,
} from '../../shared/ui/library-node-access-form.component';

import {
  LibraryNodeAccessMode,
  LibraryNodeAccessResponse,
} from '../../shared/ui/library-node-access-types';

import { DrawerActionButtonComponent } from '../../shared/ui/drawer-action-button/drawer-action-button.component';

import { NotificationService } from '../../shared/services/notification.service';

import { LibrarySectionTabsComponent } from './library-section-tabs.component';
import { formatFileSize } from '../../shared/models/file-size';
import {
  ExecutionTracking,
  INITIAL_EXECUTION_TRACKING,
} from '../../shared/services/execution-tracking';
import { ExecutionPipelineLogsComponent } from '../../shared/ui/execution-pipeline-logs.component';
import { ExecutionCardComponent } from '../../shared/ui/execution-card.component';
import { ExecutionResponse } from '../../shared/models/execution';
import { ConfirmationDialogComponent } from '../../shared/ui/confirmation-dialog.component';
import { FileDownload } from '../../shared/services/file-download';
import {
  PageShellContextService,
  PageShellSearchResult,
} from '../../core/layout/page-shell-context';

interface LibraryExportFile {
  readonly format: number;
  readonly libraries: readonly Pick<LibraryResponse, 'name' | 'isActive'>[];
}

interface LibraryRecord extends DataTableRow {
  readonly id: string;

  readonly name: string;

  readonly configuredNodes: string;

  readonly configuredNodeCount: number;

  readonly directAccessNodeCount: number;

  readonly transferNodeCount: number;

  readonly isActive: boolean;

  readonly accessModes: string;

  readonly nodePaths: string;

  readonly lastScan: string;

  readonly associatedFlows: string;

  readonly assignedFlow: string;

  readonly monitoring: string;

  readonly state: string;

  readonly modifiedAt: string;

  readonly modifiedOrder: number;
}

interface LibraryAccessRecord extends DataTableRow {
  readonly nodeName: string;

  readonly isEnabled: boolean;

  readonly modeLabel: string;

  readonly location: string;

  readonly permissions: string;

  readonly mode: LibraryNodeAccessMode;
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-library-catalog-page',

  imports: [
    DataTableComponent,
    ResourceSheetComponent,

    LibraryPreparationTemplateComponent,

    MatButtonModule,

    RecordFormComponent,
    RecordFormActionsComponent,

    LibraryNodeAccessFormComponent,

    DrawerActionButtonComponent,
    LibrarySectionTabsComponent,
    ExecutionPipelineLogsComponent,
    ExecutionCardComponent,
  ],

  templateUrl: './library-catalog-page.component.html',

  styleUrl: './library-catalog-page.component.scss',
})
export class LibraryCatalogPageComponent {
  private readonly recordForms = viewChildren(RecordFormComponent);
  protected readonly actionRecordForm = computed(() =>
    this.recordForms().find((form) => form.embedded()),
  );
  protected readonly actionStep = computed(() => {
    const form = this.actionRecordForm();
    return form ? { current: form.stepIndex() + 1, total: form.stepCount() } : null;
  });

  private readonly flowApi = inject(FlowValidation);

  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });

  private readonly http = inject(HttpClient);

  private readonly clusterApi = inject(ClusterApi);

  private readonly executionTracking = inject(ExecutionTracking);

  protected readonly executionTrackingState = toSignal(this.executionTracking.state$, {
    initialValue: INITIAL_EXECUTION_TRACKING,
  });

  private readonly notifications = inject(NotificationService);
  private readonly dialog = inject(MatDialog);
  private readonly destroyRef = inject(DestroyRef);
  private readonly pageShellContext = inject(PageShellContextService);
  private readonly downloads = inject(FileDownload);
  private readonly libraryImportInput =
    viewChild<ElementRef<HTMLInputElement>>('libraryImportFile');

  private readonly libraries = signal<readonly LibraryRecord[]>([]);
  protected readonly searchQuery = signal('');
  protected readonly selectedLibraryIds = signal<ReadonlySet<string>>(new Set());
  private readonly libraryParam = toSignal(
    this.route.queryParamMap.pipe(map((params) => params.get('library'))),
    { initialValue: this.route.snapshot.queryParamMap.get('library') },
  );

  private readonly processingByLibrary = signal<
    Readonly<Record<string, LibraryProcessingSettings>>
  >({});

  private readonly selectedAccesses = signal<readonly LibraryNodeAccessResponse[]>([]);

  protected readonly processingForm = signal<RecordFormData | null>(null);
  private processingRevision = 0;
  private processingNodeIds: readonly string[] = [];
  protected readonly processingState = toSignal(
    toObservable(
      computed(() => ({ library: this.selectedLibrary(), refresh: this.workflowRefresh() })),
    ).pipe(
      switchMap(({ library }) =>
        library
          ? timer(0, 5000).pipe(
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
  protected readonly processingSummary = computed(() => {
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
  protected async configureProcessing(): Promise<void> {
    const library = this.selectedLibrary();
    if (!library || this.workflowBusy()) return;
    this.workflowBusy.set(true);
    try {
      const [settings, nodes, accesses] = await Promise.all([
        firstValueFrom(this.clusterApi.libraryProcessing(library.id)),
        firstValueFrom(this.clusterApi.listNodes()),
        firstValueFrom(
          this.http.get<readonly LibraryNodeAccessResponse[]>(
            '/api/v1/libraries/' + library.id + '/node-access',
          ),
        ),
      ]);
      if (this.selectedLibrary()?.id !== library.id) return;
      const workers = nodes.filter(
        (node) =>
          node.roles.includes('worker') &&
          accesses.some((access) => access.nodeId === node.id && access.mode !== 'unsupported'),
      );
      this.processingNodeIds = workers.map((node) => node.id);
      this.processingRevision = settings.revision;
      this.processingForm.set({
        title: 'Surveillance et limites',
        description:
          'Les plafonds se cumulent avec la limite globale de chaque nœud. Les champs vides laissent les workers se partager les tâches disponibles.',
        submitLabel: 'Enregistrer',
        initialValues: {
          watch: String(settings.watchEnabled),
          interval: String(settings.scanIntervalSeconds),
          maximum: settings.maxConcurrentTasks == null ? '' : String(settings.maxConcurrentTasks),
          ...Object.fromEntries(
            workers.map((node) => [
              'limit_' + node.id,
              String(
                settings.nodeLimits.find((limit) => limit.nodeId === node.id)?.maxConcurrentTasks ??
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
            hint: 'Tous nœuds confondus. Vide : pas de plafond supplémentaire.',
          },
          ...workers.map((node) => ({
            id: 'limit_' + node.id,
            label: 'Limite pour ' + node.name + ' sur cette bibliothèque',
            type: 'number' as const,
            min: 1,
            max: 1000,
            step: 1,
            hint: 'Vide : partage selon les places disponibles sur ce nœud.',
          })),
        ],
      });
      this.actionType.set('processing');
      this.actionOpen.set(true);
    } catch {
      this.notifications.show('Impossible de charger les règles de traitement.');
    } finally {
      this.workflowBusy.set(false);
    }
  }
  protected async saveProcessing(values: Record<string, string>): Promise<void> {
    const library = this.selectedLibrary();
    if (!library || this.workflowBusy()) return;
    this.workflowBusy.set(true);
    const settings: Omit<LibraryProcessingSettings, 'revision'> = {
      watchEnabled: values['watch'] === 'true',
      scanIntervalSeconds: Number(values['interval']),
      maxConcurrentTasks: values['maximum'] ? Number(values['maximum']) : null,
      nodeLimits: this.processingNodeIds
        .filter((id) => values['limit_' + id])
        .map((id) => ({ nodeId: id, maxConcurrentTasks: Number(values['limit_' + id]) })),
    };
    try {
      await firstValueFrom(
        this.clusterApi.saveLibraryProcessing(library.id, settings, this.processingRevision),
      );
      this.processingByLibrary.update((current) => ({
        ...current,
        [library.id]: { ...settings, revision: this.processingRevision + 1 },
      }));
      this.workflowRefresh.update((value) => value + 1);
      this.actionOpen.set(false);
      this.notifications.show('Surveillance et limites enregistrées.');
    } catch {
      this.notifications.show(
        'Enregistrement impossible. Vérifiez les limites et actualisez la configuration.',
      );
    } finally {
      this.workflowBusy.set(false);
    }
  }

  protected readonly title = computed(
    () => (this.routeData()['title'] as string) ?? 'Bibliothèques',
  );

  protected readonly actionOpen = signal(false);

  protected readonly actionType = signal<'create' | 'edit' | 'access' | 'flow' | 'processing'>(
    'create',
  );

  protected readonly accessSetupMode = signal(false);

  protected readonly accessFormLoading = signal(false);

  protected readonly accessFormBusy = signal(false);

  private readonly activationBusy = signal<ReadonlySet<string>>(new Set());

  private readonly accessActivationBusy = signal<ReadonlySet<string>>(new Set());

  protected readonly accessNodes = signal<readonly LibraryNodeAccessOption[]>([]);

  protected readonly accessNodeGroups = signal<readonly NodeGroupResponse[]>([]);

  protected readonly automaticallySelectedAccessNodeIds = signal<readonly string[]>([]);

  protected readonly initialAccesses = signal<readonly LibraryNodeAccessResponse[]>([]);

  protected readonly selectedLibrary = signal<LibraryRecord | null>(null);

  protected readonly selectedLibraryTabIndex = signal(0);

  protected readonly selectedProcessingExecutionId = signal<string | null>(null);

  protected readonly selectedAccessesLoading = signal(false);

  protected readonly processingPipelineState = toSignal(
    toObservable(this.selectedProcessingExecutionId).pipe(
      switchMap((id) =>
        id
          ? timer(0, 3000).pipe(
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

  protected readonly libraryExecutions = computed<readonly ExecutionResponse[]>(() => {
    const libraryId = this.selectedLibrary()?.id;
    return (this.executionTrackingState().data?.executions ?? [])
      .filter((execution) => execution.libraryId === libraryId)
      .slice(0, 25);
  });

  protected readonly actionTitle = computed(() => {
    switch (this.actionType()) {
      case 'processing':
        return 'Surveillance et limites';
      case 'flow':
        return 'Attribuer un flow';
      case 'create':
        return this.createFormData.title;
      case 'edit':
        return 'Modifier ' + (this.selectedLibrary()?.name ?? 'la bibliothèque');
      default:
        return 'Configurer les accès par nœud';
    }
  });
  protected readonly actionDescription = computed(() =>
    this.actionType() === 'processing'
      ? 'Configurez la découverte, la mise en file et les plafonds simultanés.'
      : this.actionType() === 'flow'
        ? 'Choisissez une version publiée pour les traitements de cette bibliothèque.'
        : this.actionType() === 'create'
          ? this.createFormData.description
          : this.actionType() === 'edit'
            ? 'Modifiez le nom de cette définition partagée.'
            : `Définissez quels nœuds peuvent accéder à « ${this.selectedLibrary()?.name ?? 'cette bibliothèque'} » et leur mode d’accès.`,
  );

  protected readonly createFormData: RecordFormData = {
    title: 'Ajouter une bibliothèque',

    description:
      'Créez sa définition partagée. Les chemins et modes d’accès seront configurés séparément pour chaque nœud.',

    submitLabel: 'Créer la bibliothèque',

    fields: [{ id: 'name', label: 'Nom', type: 'text', required: true, maxLength: 128 }],
  };

  protected readonly editFormData = computed<RecordFormData>(() => ({
    ...this.createFormData,

    title: 'Modifier la bibliothèque',

    description: 'Modifiez le nom de la définition partagée sans changer les accès configurés.',

    submitLabel: 'Enregistrer la définition',

    initialValues: {
      name: this.selectedLibrary()?.name ?? '',
    },
  }));

  protected readonly libraryRowActions: readonly DataTableRowAction[] = [
    { id: 'edit', label: 'Modifier' },

    { id: 'flow', label: 'Attribuer un flow' },
    { id: 'processing', label: 'Surveillance et limites' },

    {
      id: 'delete',

      label: 'Supprimer',

      tone: 'danger',

      confirmation: (row) => ({
        title: 'Supprimer cette bibliothèque ?',

        message: `La définition « ${row['name']} » et ses accès par nœud seront supprimés. Les fichiers média ne seront pas touchés. Cette action est irréversible.`,

        confirmLabel: 'Supprimer',
        variant: 'danger',
      }),
    },
  ];
  protected readonly librarySelectionActions = [
    {
      id: 'activate',
      label: 'Activer',
      visibleWhen: (rows: readonly DataTableRow[]) =>
        rows.some(
          (row) => !this.libraries().find((item) => item.id === String(row['id']))?.isActive,
        ),
    },
    {
      id: 'deactivate',
      label: 'Désactiver',
      visibleWhen: (rows: readonly DataTableRow[]) =>
        rows.some(
          (row) => this.libraries().find((item) => item.id === String(row['id']))?.isActive,
        ),
    },
    { id: 'delete', label: 'Supprimer', visibleWhen: () => true },
  ];

  protected readonly accessRowActions: readonly DataTableRowAction[] = [
    { id: 'edit', label: 'Modifier' },

    {
      id: 'delete',

      label: 'Supprimer',

      tone: 'danger',

      confirmation: (row) => ({
        title: 'Supprimer cet accès ?',

        message: `L’accès de « ${row['nodeName']} » sera retiré de cette bibliothèque.`,

        confirmLabel: 'Supprimer l’accès',
        variant: 'danger',
      }),
    },
  ];

  private readonly workflowRefresh = signal(0);

  private readonly assignmentRevision = signal(0);

  protected readonly workflowBusy = signal(false);

  protected readonly assignmentForm = signal<RecordFormData | null>(null);

  protected readonly workflowError = signal('');

  protected readonly workflowState = toSignal(
    toObservable(
      computed(() => ({ library: this.selectedLibrary(), refresh: this.workflowRefresh() })),
    ).pipe(
      switchMap(({ library }) =>
        library
          ? timer(0, 5000)
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

  protected readonly workflowLabel = computed(() => {
    const assignment = this.workflowState()?.assignment;

    return assignment ? assignment.name + ' · v' + assignment.version : 'Aucun flow attribué';
  });

  protected readonly discoveryStatus = computed(() => {
    const scan = this.workflowState()?.scan;

    return (
      this.workflowError() ||
      (!scan
        ? 'Aucune découverte effectuée.'
        : scan.state === 'queued'
          ? 'En attente d’un nœud ayant un accès direct en lecture.'
          : scan.detail)
    );
  });

  protected readonly mediaRows = computed<readonly DataTableRow[]>(() =>
    (this.workflowState()?.files ?? []).map((file) => ({
      id: file.id,

      name: file.relativePath,

      size: formatFileSize(file.bytes),

      modifiedAt: new Date(file.modifiedAt).toLocaleString('fr-FR'),
    })),
  );

  protected readonly mediaColumns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Fichier', sortable: true },

    { key: 'size', label: 'Taille' },

    { key: 'modifiedAt', label: 'Modifié le' },
  ];

  protected readonly mediaListActions = [
    { id: 'discover', label: 'Découvrir / Actualiser les fichiers' },
  ];

  protected readonly mediaRowActions: readonly DataTableRowAction[] = [
    {
      id: 'process',

      label: 'Traiter',

      visibleWhen: () =>
        !!this.workflowState()?.assignment &&
        !!this.selectedLibrary()?.isActive &&
        !this.workflowBusy(),
    },
  ];

  protected readonly mediaSelectionActions = [
    {
      id: 'process',

      label: 'Traiter la sélection',

      visibleWhen: () =>
        !!this.workflowState()?.assignment &&
        !!this.selectedLibrary()?.isActive &&
        !this.workflowBusy(),
    },
  ];

  protected async assignFlow(): Promise<void> {
    const library = this.selectedLibrary();

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

      if (this.selectedLibrary()?.id !== library.id) return;

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

      this.actionType.set('flow');

      this.actionOpen.set(true);
    } catch {
      this.notifications.show('Impossible de charger les versions publiées.');
    } finally {
      this.workflowBusy.set(false);
    }
  }

  protected async saveFlowAssignment(values: Record<string, string>): Promise<void> {
    const library = this.selectedLibrary();

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

      this.actionOpen.set(false);

      this.notifications.show('Flow de la bibliothèque enregistré.');

      this.workflowRefresh.update((value) => value + 1);

      this.refresh();
    } catch {
      this.notifications.show('Attribution impossible : la configuration ou la version a changé.');
    } finally {
      this.workflowBusy.set(false);
    }
  }

  protected async discoverFiles(): Promise<void> {
    const library = this.selectedLibrary();

    if (!library || this.workflowBusy()) return;

    this.workflowBusy.set(true);

    try {
      await firstValueFrom(this.clusterApi.discoverLibrary(library.id));

      this.notifications.show('Découverte demandée au nœud d’accès.');

      this.workflowRefresh.update((value) => value + 1);
    } catch {
      this.notifications.show(
        'Attribuez un flow publié à une bibliothèque active avant la découverte.',
      );
    } finally {
      this.workflowBusy.set(false);
    }
  }

  protected async processFiles(ids: readonly string[]): Promise<void> {
    const library = this.selectedLibrary();

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

  protected onMediaSelection(event: DataTableSelectionActionEvent): void {
    if (event.actionId === 'process')
      void this.processFiles(event.rows.map((row) => String(row['id'])));
  }

  protected readonly detailTitle = computed(() => this.selectedLibrary()?.name ?? 'Bibliothèque');

  protected readonly detailDescription = computed(() =>
    this.selectedLibrary() ? 'Définition et accès aux nœuds' : 'Détails de la bibliothèque',
  );

  protected readonly accessStatus = computed(() => {
    const library = this.selectedLibrary();

    if (library?.state === 'Désactivée')
      return 'Bibliothèque désactivée par le serveur. Les chemins et autorisations restent enregistrés.';

    if (!library?.configuredNodeCount)
      return 'Aucun accès actif. Les nœuds ne peuvent pas encore traiter ses médias.';

    const modes = [
      library.directAccessNodeCount
        ? `${library.directAccessNodeCount} accès direct${library.directAccessNodeCount > 1 ? 's' : ''}`
        : '',

      library.transferNodeCount
        ? `${library.transferNodeCount} transfert${library.transferNodeCount > 1 ? 's' : ''}`
        : '',
    ]

      .filter(Boolean)

      .join(' · ');

    return `${library.configuredNodeCount} nœud${library.configuredNodeCount > 1 ? 's' : ''} utilisable${library.configuredNodeCount > 1 ? 's' : ''} · ${modes}`;
  });

  protected readonly accessColumns: readonly DataTableColumn[] = [
    { key: 'nodeName', label: 'Nœud' },

    {
      key: 'isEnabled',

      label: 'Accès',

      toggle: {
        showStatusLabel: false,
        ariaLabel: (row, checked) =>
          `${checked ? 'Désactiver' : row['mode'] === 'unsupported' ? 'Configurer' : 'Activer'} l’accès de ${row['nodeName']}`,

        disabledWhen: (row) =>
          this.accessActivationBusy().has(`${this.selectedLibrary()?.id}:${row['id']}`),

        confirmation: (row, checked) =>
          checked
            ? undefined
            : {
                title: 'Désactiver cet accès ?',

                message: `Le nœud « ${row['nodeName']} » ne recevra plus de tâche pour cette bibliothèque.`,

                confirmLabel: 'Désactiver',
              },
      },
    },

    { key: 'modeLabel', label: 'Accès' },

    { key: 'location', label: 'Chemin ou source' },

    { key: 'permissions', label: 'Droits' },
  ];

  protected readonly accessRows = computed<readonly LibraryAccessRecord[]>(() =>
    this.selectedAccesses().map((access) => ({
      id: access.nodeId,

      nodeName: access.nodeName,

      isEnabled: access.isEnabled,

      modeLabel: `${this.accessModeLabel(access.mode)}${access.mode !== 'unsupported' && !access.isEnabled ? ' · Désactivé' : ''}`,

      location:
        access.mode === 'direct'
          ? (access.localPath ?? 'Chemin absent')
          : access.mode === 'transfer'
            ? `Depuis ${access.transferSourceNodeName ?? 'source inconnue'}`
            : 'Aucun accès',

      permissions:
        access.mode === 'direct'
          ? [access.canRead ? 'Lecture' : '', access.canWrite ? 'Écriture' : '']

              .filter(Boolean)

              .join(' · ') || 'Aucun droit'
          : access.mode === 'transfer'
            ? 'Copie temporaire · lecture et écriture'
            : 'Aucun',

      mode: access.mode,
    })),
  );

  protected readonly librarySheet = computed<{
    readonly title: string;
    readonly subtitle: string;
    readonly notices: readonly ResourceSheetNotice[];
    readonly sections: readonly ResourceSheetSection[];
  } | null>(() => {
    const library = this.selectedLibrary();
    if (!library) return null;

    const assignment = this.workflowState()?.assignment;
    const processing = this.processingState() ?? this.processingByLibrary()[library.id] ?? null;
    const scan = this.workflowState()?.scan;
    const accessCount = library.configuredNodeCount;
    const scanState = scan?.state;
    const scanLabel = scanState
      ? scanState === 'queued'
        ? 'En attente'
        : scanState === 'running'
          ? 'En cours'
          : scanState === 'completed'
            ? 'Terminée'
            : 'Échec'
      : this.workflowError()
        ? 'Indisponible'
        : 'Aucune découverte';

    const notices: ResourceSheetNotice[] = [];
    if (this.workflowError()) {
      notices.push({
        tone: 'warning',
        title: 'Découverte indisponible',
        description: this.workflowError(),
      });
    }

    const sections: readonly ResourceSheetSection[] = [
      {
        id: 'library-processing',
        title: 'Flow et traitement',
        caption: assignment ? `${assignment.name} · v${assignment.version}` : 'Aucun flow attribué',
        items: [
          {
            label: 'Flow attribué',
            detail: this.workflowLabel(),
            badges: [
              {
                label: assignment ? 'Flow attribué' : 'À configurer',
                tone: assignment ? 'success' : 'warning',
              },
            ],
          },
          {
            label: 'Surveillance et limites',
            detail: this.processingSummary(),
            badges: processing
              ? [
                  {
                    label: processing.watchEnabled ? 'Automatique' : 'Manuelle',
                    tone: processing.watchEnabled ? 'accent' : 'neutral',
                  },
                  {
                    label: processing.maxConcurrentTasks
                      ? `${processing.maxConcurrentTasks} tâche(s) max.`
                      : 'Sans plafond global',
                    tone: 'neutral',
                  },
                ]
              : [],
          },
        ],
      },
      {
        id: 'library-access',
        title: 'Accès du cluster',
        caption: accessCount ? `${accessCount} nœud(s) configuré(s)` : 'Aucun nœud configuré',
        metrics: [
          {
            label: 'Accès direct',
            value: `${library.directAccessNodeCount} nœud(s)`,
          },
          {
            label: 'Transfert vers worker',
            value: `${library.transferNodeCount} nœud(s)`,
          },
        ],
      },
      {
        id: 'library-discovery',
        title: 'Découverte et activité',
        caption: `${this.mediaRows().length} fichier(s) détecté(s)`,
        metrics: [
          {
            label: 'Dernière découverte',
            value: scanLabel,
            detail: this.discoveryStatus(),
          },
          {
            label: 'Fichiers détectés',
            value: `${this.mediaRows().length}`,
          },
        ],
        fields: [
          {
            label: 'Terminée le',
            value: scan?.completedAt
              ? new Date(scan.completedAt).toLocaleString('fr-FR')
              : 'Aucune date enregistrée',
          },
          { label: 'Créée le', value: library.modifiedAt },
        ],
      },
    ];

    return {
      title: library.name,
      subtitle: `${library.state} · ${library.configuredNodes}`,
      notices,
      sections,
    };
  });

  protected readonly filteredLibraries = computed<readonly LibraryRecord[]>(() => {
    const selectedId = this.selectedLibrary()?.id;
    const workflow = this.workflowState();
    const selectedProcessing = this.processingState();
    const processingMap = this.processingByLibrary();
    const query = this.searchQuery().trim().toLocaleLowerCase('fr');
    const visible = query
      ? this.libraries().filter(
          (library) =>
            library.id === selectedId ||
            library.name.toLocaleLowerCase('fr').includes(query) ||
            library.nodePaths.toLocaleLowerCase('fr').includes(query) ||
            library.assignedFlow.toLocaleLowerCase('fr').includes(query) ||
            library.state.toLocaleLowerCase('fr').includes(query),
        )
      : this.libraries();
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

  protected readonly libraryItems = computed<readonly ResourceListItem[]>(() =>
    this.filteredLibraries().map((library) => this.toListItem(library)),
  );

  protected readonly libraryGroups: readonly ResourceListGroup[] = [
    { id: 'configuration', label: 'À configurer', tone: 'warning' },
    { id: 'ready', label: 'Prêtes', tone: 'default' },
    { id: 'disabled', label: 'Désactivées', tone: 'blue' },
  ];

  constructor() {
    this.destroyRef.onDestroy(
      this.pageShellContext.register({
        searchPlaceholder: 'Rechercher dans Bibliothèques…',
        query: this.searchQuery,
        onSearchQueryChange: (query) => this.searchQuery.set(query),
        searchResults: (query) => this.librarySearchResults(query),
        actions: [
          {
            id: 'create-library',
            label: 'Ajouter une bibliothèque',
            icon: 'add',
            run: () => this.addLibrary(),
          },
          {
            id: 'import-libraries',
            label: 'Importer',
            icon: 'upload',
            visibleWhen: () => !this.hasSelectedLibraries(),
            run: () => this.libraryImportInput()?.nativeElement.click(),
          },
          {
            id: 'export-libraries',
            label: 'Exporter',
            icon: 'outward',
            visibleWhen: () => this.hasSelectedLibraries(),
            run: () => void this.exportSelectedLibraries(),
          },
        ],
      }),
    );
    this.refresh();
    // La fiche est portée par l'URL (?library=<id>) : le menu latéral et le bouton précédent
    // du navigateur ramènent à la liste sans bouton retour dédié.
    effect(() => {
      const id = this.libraryParam();
      const libraries = this.libraries();
      untracked(() => {
        if (!id) {
          if (this.selectedLibrary()) this.selectedLibrary.set(null);
          return;
        }
        if (this.selectedLibrary()?.id === id) return;
        const library = libraries.find((item) => item.id === id);
        if (library) this.openLibrary(library);
      });
    });
  }

  private librarySearchResults(query: string): readonly PageShellSearchResult[] {
    const normalized = query.trim().toLocaleLowerCase('fr-FR');
    return this.libraries()
      .filter(
        (library) =>
          !normalized ||
          [
            library.name,
            library.state,
            library.assignedFlow,
            library.configuredNodes,
            library.accessModes,
            library.nodePaths,
          ]
            .join(' ')
            .toLocaleLowerCase('fr-FR')
            .includes(normalized),
      )
      .slice(0, 8)
      .map((library) => ({
        id: `library:${library.id}`,
        label: library.name,
        description: `${library.state} · ${library.assignedFlow}`,
        group: 'Bibliothèques',
        glyph: '▱',
        run: () => this.onLibraryOpen({ id: library.id }),
      }));
  }

  private hasSelectedLibraries(): boolean {
    const selected = this.selectedLibraryIds();
    return this.libraries().some((library) => selected.has(library.id));
  }

  private async exportSelectedLibraries(): Promise<void> {
    const selectedIds = new Set(
      [...this.selectedLibraryIds()].filter((id) =>
        this.libraries().some((library) => library.id === id),
      ),
    );
    if (!selectedIds.size) return;
    try {
      const exportFile = await firstValueFrom(
        this.http.get<{
          format: number;
          exportedAt: string;
          clusterName: string;
          libraries: readonly LibraryResponse[];
        }>('/api/v1/libraries/export'),
      );
      const selected = exportFile.libraries.filter((library) => selectedIds.has(library.id));
      if (!selected.length) {
        this.notifications.show('Aucune bibliothèque sélectionnée à exporter.');
        return;
      }
      this.downloads.save(
        new Blob([JSON.stringify({ ...exportFile, libraries: selected }, null, 2)], {
          type: 'application/json',
        }),
        'bibliotheques-selection.json',
      );
    } catch {
      this.notifications.show('Les bibliothèques sélectionnées n’ont pas pu être exportées.');
    }
  }

  protected onLibraryImportSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (file) void this.importLibraries(file);
  }

  private async importLibraries(file: File): Promise<void> {
    let entries: readonly Pick<LibraryResponse, 'name' | 'isActive'>[];
    try {
      const parsed: unknown = JSON.parse(await file.text());
      if (
        !parsed ||
        typeof parsed !== 'object' ||
        !('format' in parsed) ||
        parsed.format !== 1 ||
        !('libraries' in parsed) ||
        !Array.isArray(parsed.libraries)
      ) {
        throw new Error('invalid-file');
      }
      const payload = parsed as LibraryExportFile;
      entries = payload.libraries.filter(
        (library): library is Pick<LibraryResponse, 'name' | 'isActive'> =>
          !!library &&
          typeof library === 'object' &&
          'name' in library &&
          typeof library.name === 'string' &&
          !!library.name.trim(),
      );
      if (!entries.length) throw new Error('empty-file');
    } catch {
      this.notifications.show(
        'Import impossible. Sélectionnez un export de bibliothèques au format JSON pris en charge.',
      );
      return;
    }

    let existing: readonly LibraryResponse[];
    try {
      existing = await firstValueFrom(this.clusterApi.listLibraries());
    } catch {
      this.notifications.show('Impossible de charger les bibliothèques avant l’import.');
      return;
    }
    this.libraries.set(existing.map((library) => this.toRecord(library)));

    const knownNames = new Set(existing.map((library) => library.name.toLocaleLowerCase('fr-FR')));
    let imported = 0;
    let skipped = 0;
    let failed = 0;
    let activationFailed = 0;
    for (const entry of entries) {
      const name = entry.name.trim();
      const key = name.toLocaleLowerCase('fr-FR');
      if (knownNames.has(key)) {
        skipped++;
        continue;
      }
      try {
        const created = await firstValueFrom(
          this.http.post<LibraryResponse>('/api/v1/libraries', { name }),
        );
        knownNames.add(key);
        imported++;
        if (entry.isActive === false) {
          try {
            await firstValueFrom(
              this.http.put<LibraryResponse>(`/api/v1/libraries/${created.id}/activation`, {
                isActive: false,
              }),
            );
          } catch {
            activationFailed++;
          }
        }
      } catch (error) {
        if ((error as { status?: number }).status === 409) skipped++;
        else failed++;
      }
    }
    this.refresh();
    const details = [
      `${imported} bibliothèque(s) importée(s)`,
      skipped ? `${skipped} déjà présente(s)` : '',
      failed ? `${failed} en échec` : '',
      activationFailed ? `${activationFailed} état(s) à vérifier` : '',
      imported ? 'flows et accès des nœuds à reconfigurer' : '',
    ].filter(Boolean);
    this.notifications.show(details.join(' · '));
  }

  protected onLibraryOpen(row: DataTableRow): void {
    const id = String(row['id']);
    void this.router.navigate([], { relativeTo: this.route, queryParams: { library: id } });
  }

  private toListItem(library: LibraryRecord): ResourceListItem {
    const hasFlow = library.assignedFlow !== 'Aucun flow';
    const tags: ResourceListTag[] = [
      hasFlow
        ? { label: `Flow : ${library.assignedFlow}`, tone: 'ok' }
        : { label: 'Flow à attribuer', tone: 'warn' },
      library.configuredNodeCount
        ? { label: library.accessModes }
        : { label: 'Aucun nœud n’a accès', tone: 'warn' },
      { label: `Surveillance ${library.monitoring.toLocaleLowerCase('fr')}` },
    ];
    if (!library.isActive) tags.unshift({ label: 'Désactivée', tone: 'warn' });

    return {
      id: library.id,
      name: library.name,
      subtitle: `Créée le ${library.modifiedAt}`,
      group: !library.isActive
        ? this.libraryGroups[2]
        : hasFlow && library.configuredNodeCount > 0
          ? this.libraryGroups[1]
          : this.libraryGroups[0],
      rowData: library,
      tags,
      toggle: {
        checked: library.isActive,
        disabled: this.activationBusy().has(library.id),
        ariaLabel: `Activer la bibliothèque ${library.name}`,
        confirmOff: {
          title: 'Désactiver cette bibliothèque ?',
          message: `« ${library.name} » ne sera plus disponible pour le cluster. Ses accès configurés seront conservés.`,
          confirmLabel: 'Désactiver',
        },
      },
    };
  }

  private openLibrary(library: LibraryRecord): void {
    this.selectedLibrary.set(library);
    this.selectedLibraryTabIndex.set(0);

    this.selectedAccesses.set([]);
    this.selectedProcessingExecutionId.set(null);

    this.loadSelectedAccesses(library.id);
  }

  protected onProcessingExecutionAction(executionId: string): void {
    this.selectedProcessingExecutionId.update((selectedId) =>
      selectedId === executionId ? null : executionId,
    );
  }

  protected onDetailClosed(): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { library: null },
      queryParamsHandling: 'merge',
    });
  }

  protected addLibrary(): void {
    this.actionType.set('create');

    this.actionOpen.set(true);
  }

  protected onLibraryAction(event: DataTableActionEvent): void {
    const library = this.libraries().find((item) => item.id === event.row['id']);

    if (!library) return;

    if (event.actionId === 'processing') {
      this.selectedLibrary.set(library);
      void this.configureProcessing();
      return;
    }
    if (event.actionId === 'flow') {
      this.selectedLibrary.set(library);

      void this.assignFlow();

      return;
    }

    if (event.actionId === 'edit') {
      this.selectedLibrary.set(library);

      this.actionType.set('edit');

      this.actionOpen.set(true);
    } else if (event.actionId === 'delete') {
      this.deleteLibrary(library);
    }
  }

  protected onLibraryItemToggle(event: ResourceListToggleEvent): void {
    const library = this.libraries().find((item) => item.id === event.id);

    if (library) this.setLibraryActive(library, event.checked);
  }

  protected async onLibrarySelection(event: DataTableSelectionActionEvent): Promise<void> {
    const selected = event.rows
      .map((row) => this.libraries().find((library) => library.id === String(row['id'])))
      .filter((library): library is LibraryRecord => !!library);
    if (!selected.length) return;
    const targets =
      event.actionId === 'activate'
        ? selected.filter((item) => !item.isActive)
        : event.actionId === 'deactivate'
          ? selected.filter((item) => item.isActive)
          : selected;
    if (!targets.length) return;
    const action = event.actionId;
    const confirmed = await firstValueFrom(
      this.dialog
        .open(ConfirmationDialogComponent, {
          data:
            action === 'delete'
              ? {
                  title: `Supprimer ${targets.length} bibliothèque(s) ?`,
                  message: `Les définitions et accès par nœud de ${targets.map((item) => `« ${item.name} »`).join(', ')} seront supprimés. Les fichiers média ne seront pas touchés.`,
                  confirmLabel: 'Supprimer',
                  variant: 'danger',
                }
              : {
                  title: `${action === 'activate' ? 'Activer' : 'Désactiver'} ${targets.length} bibliothèque(s) ?`,
                  message: targets.map((item) => `« ${item.name} »`).join(', '),
                  confirmLabel: action === 'activate' ? 'Activer' : 'Désactiver',
                },
        })
        .afterClosed(),
    );
    if (!confirmed) return;
    const results = await Promise.allSettled(
      targets.map(async (library) => {
        if (action === 'delete') {
          await firstValueFrom(this.http.delete<void>(`/api/v1/libraries/${library.id}`));
          this.libraries.update((items) => items.filter((item) => item.id !== library.id));
          if (this.selectedLibrary()?.id === library.id) this.selectedLibrary.set(null);
        } else {
          const isActive = action === 'activate';
          const updated = await firstValueFrom(
            this.http.put<LibraryResponse>(`/api/v1/libraries/${library.id}/activation`, {
              isActive,
            }),
          );
          this.replaceLibraryRecord(this.toRecord(updated));
        }
      }),
    );
    const succeeded = results.filter((result) => result.status === 'fulfilled').length;
    this.notifications.show(
      `${succeeded}/${targets.length} bibliothèque(s) ${action === 'delete' ? 'supprimée(s)' : action === 'activate' ? 'activée(s)' : 'désactivée(s)'}.`,
    );
  }

  protected updateLibrary(value: Record<string, string>): void {
    const library = this.selectedLibrary();

    if (!library) return;

    this.http

      .put<LibraryResponse>(`/api/v1/libraries/${library.id}`, {
        name: value['name'],
      })

      .pipe(
        catchError((error: { status?: number }) => {
          this.notifications.show(
            error.status === 409
              ? 'Une bibliothèque porte déjà ce nom.'
              : 'La définition n’a pas pu être modifiée.',
          );

          return EMPTY;
        }),
      )

      .subscribe((updated) => {
        const record = this.toRecord(updated);

        this.libraries.update((items) =>
          items.map((item) => (item.id === updated.id ? record : item)),
        );

        this.selectedLibrary.set(record);

        this.actionOpen.set(false);

        this.notifications.show(`Définition « ${updated.name} » enregistrée.`);
      });
  }

  protected onAccessRowAction(event: DataTableActionEvent): void {
    const access = this.selectedAccesses().find((item) => item.nodeId === event.row['id']);

    if (!access) return;

    if (event.actionId === 'edit') {
      this.configureAccesses();

      return;
    }

    const updatedAccesses = this.selectedAccesses()

      .filter((item) => event.actionId !== 'delete' || item.nodeId !== access.nodeId)

      .map((item) => ({
        nodeId: item.nodeId,

        mode: item.mode,

        isEnabled: item.isEnabled,

        localPath: item.localPath,

        canRead: item.canRead,

        canWrite: item.canWrite,

        transferSourceNodeId: item.transferSourceNodeId,
      }));

    this.saveAccessConfiguration(updatedAccesses);
  }

  protected onAccessToggle(event: DataTableToggleEvent): void {
    const access = this.selectedAccesses().find((item) => item.nodeId === event.row['id']);

    if (!access) return;

    if (event.checked && access.mode === 'unsupported') {
      this.configureAccesses();

      return;
    }

    const library = this.selectedLibrary();

    if (!library) return;

    const busyKey = `${library.id}:${access.nodeId}`;

    this.accessActivationBusy.update((current) => new Set(current).add(busyKey));

    this.http

      .put<LibraryNodeAccessResponse>(
        `/api/v1/libraries/${library.id}/node-access/${access.nodeId}/activation`,

        { isEnabled: event.checked },
      )

      .pipe(
        catchError(() => {
          this.notifications.show('L’accès de ce nœud n’a pas pu être modifié.');

          return EMPTY;
        }),

        finalize(() =>
          this.accessActivationBusy.update((current) => {
            const next = new Set(current);

            next.delete(busyKey);

            return next;
          }),
        ),
      )

      .subscribe((updated) => {
        const accesses = this.selectedAccesses().map((item) =>
          item.nodeId === updated.nodeId ? updated : item,
        );

        this.selectedAccesses.set(accesses);

        const summary = this.toAccessSummary(accesses);

        const state = library.state === 'Désactivée' ? 'Désactivée' : summary.state;

        this.libraries.update((current) =>
          current.map((item) => (item.id === library.id ? { ...item, ...summary, state } : item)),
        );

        this.selectedLibrary.update((current) =>
          current?.id === library.id ? { ...current, ...summary, state } : current,
        );

        this.notifications.show(
          `Accès à « ${library.name} » ${event.checked ? 'réactivé' : 'désactivé'} pour ${access.nodeName}.`,
        );
      });
  }

  protected configureAccesses(): void {
    const library = this.selectedLibrary();

    if (!library) return;

    this.actionType.set('access');

    this.accessSetupMode.set(false);

    this.actionOpen.set(true);

    this.loadAccessForm(library.id);
  }

  protected closeCreateLibrary(): void {
    this.actionOpen.set(false);
  }

  protected createLibrary(value: Record<string, string>): void {
    this.accessFormBusy.set(true);

    this.http

      .post<LibraryResponse>('/api/v1/libraries', value)

      .pipe(
        catchError((error: { status?: number }) => {
          const message =
            error.status === 409
              ? 'Une bibliothèque porte déjà ce nom.'
              : 'La bibliothèque n’a pas pu être créée. Vérifiez que le serveur est disponible.';

          this.notifications.show(message);

          return of(null);
        }),
      )

      .subscribe((created) => {
        this.accessFormBusy.set(false);

        if (!created) return;

        const record = this.toRecord(created);

        this.libraries.update((current) => [...current, record]);

        this.selectedLibrary.set(record);

        this.actionType.set('access');

        this.accessSetupMode.set(true);

        this.notifications.show(
          `Bibliothèque « ${created.name} » créée. Vous pouvez maintenant configurer les accès de ses nœuds.`,
        );

        this.loadAccessForm(created.id);
      });
  }

  protected configureLibraryAccess(values: readonly LibraryNodeAccessValue[]): void {
    this.saveAccessConfiguration(values);
  }

  private saveAccessConfiguration(values: readonly LibraryNodeAccessValue[]): void {
    const library = this.selectedLibrary();

    if (!library) return;

    this.accessFormBusy.set(true);

    this.http

      .put<readonly LibraryNodeAccessResponse[]>(`/api/v1/libraries/${library.id}/node-access`, {
        nodes: values,
      })

      .pipe(
        catchError((error: { status?: number; error?: { errors?: Record<string, string[]> } }) => {
          this.accessFormBusy.set(false);

          const validationMessage = error.error?.errors
            ? Object.values(error.error.errors).flat()[0]
            : undefined;

          this.notifications.show(
            validationMessage ??
              'Les accès de la bibliothèque n’ont pas pu être enregistrés. Vérifiez le serveur et les nœuds sélectionnés.',
          );

          return EMPTY;
        }),
      )

      .subscribe((accesses) => {
        this.accessFormBusy.set(false);

        this.selectedAccesses.set(accesses);

        const summary = this.toAccessSummary(accesses);

        const state = library.state === 'Désactivée' ? 'Désactivée' : summary.state;

        this.libraries.update((current) =>
          current.map((item) => (item.id === library.id ? { ...item, ...summary, state } : item)),
        );

        this.selectedLibrary.update((item) =>
          item?.id === library.id ? { ...item, ...summary, state } : item,
        );

        this.actionOpen.set(false);

        this.notifications.show(
          `Accès de « ${library.name} » enregistrés pour ${accesses.filter((item) => item.mode !== 'unsupported').length} nœud(s).`,
        );
      });
  }

  private setLibraryActive(library: LibraryRecord, isActive: boolean): void {
    this.activationBusy.update((busy) => new Set(busy).add(library.id));

    this.replaceLibraryRecord({
      ...library,

      isActive,

      state: this.libraryState(isActive, library.configuredNodeCount),
    });

    this.http

      .put<LibraryResponse>(`/api/v1/libraries/${library.id}/activation`, { isActive })

      .pipe(
        catchError(() => {
          this.replaceLibraryRecord(library);

          this.notifications.show('L’état de la bibliothèque n’a pas pu être modifié.');

          return EMPTY;
        }),

        finalize(() =>
          this.activationBusy.update((busy) => {
            const next = new Set(busy);

            next.delete(library.id);

            return next;
          }),
        ),
      )

      .subscribe((updated) => {
        const record = this.toRecord(updated);

        this.replaceLibraryRecord(record);

        this.notifications.show(
          `Bibliothèque « ${updated.name} » ${isActive ? 'réactivée' : 'désactivée'}.`,
        );
      });
  }

  private replaceLibraryRecord(record: LibraryRecord): void {
    this.libraries.update((items) => items.map((item) => (item.id === record.id ? record : item)));

    this.selectedLibrary.update((current) => (current?.id === record.id ? record : current));
  }

  private libraryState(isActive: boolean, configuredNodeCount: number): string {
    return !isActive ? 'Désactivée' : configuredNodeCount ? 'Active' : 'Accès à configurer';
  }

  private deleteLibrary(library: LibraryRecord): void {
    this.http

      .delete<void>(`/api/v1/libraries/${library.id}`)

      .pipe(
        catchError(() => {
          this.notifications.show('La bibliothèque n’a pas pu être supprimée.');

          return EMPTY;
        }),
      )

      .subscribe(() => {
        this.libraries.update((items) => items.filter((item) => item.id !== library.id));

        if (this.selectedLibrary()?.id === library.id) this.selectedLibrary.set(null);

        this.notifications.show(
          `Bibliothèque « ${library.name} » supprimée. Les fichiers média sont conservés.`,
        );
      });
  }

  protected accessModeLabel(mode: LibraryNodeAccessMode): string {
    if (mode === 'direct') return 'Accès direct';

    if (mode === 'transfer') return 'Transfert worker';

    return 'Aucun accès';
  }

  private loadSelectedAccesses(libraryId: string): void {
    this.selectedAccessesLoading.set(true);

    this.http

      .get<readonly LibraryNodeAccessResponse[]>(`/api/v1/libraries/${libraryId}/node-access`)

      .pipe(
        catchError(() => {
          this.notifications.show('Les accès de cette bibliothèque n’ont pas pu être chargés.');

          return of([]);
        }),
        finalize(() => this.selectedAccessesLoading.set(false)),
      )

      .subscribe((accesses) => this.selectedAccesses.set(accesses));
  }

  private loadAccessForm(libraryId: string): void {
    this.accessFormLoading.set(true);

    const otherLibraryIds = this.libraries()
      .map((library) => library.id)
      .filter((id) => id !== libraryId);

    forkJoin({
      nodes: this.clusterApi.listNodes(),
      groups: this.http.get<readonly NodeGroupResponse[]>('/api/v1/node-groups'),

      accesses: this.http.get<readonly LibraryNodeAccessResponse[]>(
        `/api/v1/libraries/${libraryId}/node-access`,
      ),
      otherLibraryAccesses: otherLibraryIds.length
        ? forkJoin(
            otherLibraryIds.map((id) =>
              this.http.get<readonly LibraryNodeAccessResponse[]>(
                `/api/v1/libraries/${id}/node-access`,
              ),
            ),
          )
        : of([] as readonly (readonly LibraryNodeAccessResponse[])[]),
    })
      .pipe(
        catchError(() => {
          this.accessFormLoading.set(false);

          this.notifications.show(
            'Les nœuds ou les accès de cette bibliothèque n’ont pas pu être chargés.',
          );

          return EMPTY;
        }),
      )

      .subscribe(({ nodes, groups, accesses, otherLibraryAccesses }) => {
        this.accessNodes.set(nodes);

        this.accessNodeGroups.set(groups);

        this.initialAccesses.set(accesses);

        this.automaticallySelectedAccessNodeIds.set([
          ...new Set(
            otherLibraryAccesses
              .flat()
              .filter((access) => access.isEnabled && access.mode === 'direct')
              .map((access) => access.nodeId),
          ),
        ]);

        this.accessFormLoading.set(false);
      });
  }

  private toAccessSummary(
    accesses: readonly LibraryNodeAccessResponse[],
  ): Pick<
    LibraryRecord,
    | 'configuredNodes'
    | 'accessModes'
    | 'nodePaths'
    | 'state'
    | 'configuredNodeCount'
    | 'directAccessNodeCount'
    | 'transferNodeCount'
  > {
    const directCount = accesses.filter(
      (access) => access.isEnabled && access.mode === 'direct',
    ).length;

    const transferCount = accesses.filter(
      (access) => access.isEnabled && access.mode === 'transfer',
    ).length;

    const configuredCount = directCount + transferCount;

    const modes = [
      directCount ? `${directCount} direct(s)` : '',

      transferCount ? `${transferCount} transfert(s)` : '',
    ]

      .filter(Boolean)

      .join(' · ');

    return {
      configuredNodeCount: configuredCount,

      directAccessNodeCount: directCount,

      transferNodeCount: transferCount,

      configuredNodes: configuredCount ? `${configuredCount} nœud(s) utilisable(s)` : 'Aucun accès',

      accessModes: modes || 'Non configuré',

      nodePaths:
        accesses

          .filter((access) => access.isEnabled && access.mode === 'direct')

          .map((access) => `${access.nodeName}: ${access.localPath}`)

          .join(' · ') || 'Aucun chemin direct',

      state:
        this.selectedLibrary()?.state === 'Désactivée'
          ? 'Désactivée'
          : configuredCount
            ? 'Active'
            : 'Accès à configurer',
    };
  }

  private refresh(): void {
    this.clusterApi

      .listLibraries()

      .pipe(
        catchError(() => {
          this.notifications.show('Impossible de charger les bibliothèques depuis le serveur.');

          return of([]);
        }),
      )

      .subscribe((libraries) => {
        this.libraries.set(libraries.map((library) => this.toRecord(library)));
        if (!libraries.length) return;
        forkJoin(
          libraries.map((library) =>
            this.clusterApi.libraryProcessing(library.id).pipe(
              map((settings) => [library.id, settings] as const),
              catchError(() => of(null)),
            ),
          ),
        ).subscribe((entries) => {
          const valid = entries.filter(
            (entry): entry is readonly [string, LibraryProcessingSettings] => entry !== null,
          );
          if (valid.length) {
            this.processingByLibrary.update((current) => ({
              ...current,
              ...Object.fromEntries(valid),
            }));
          }
        });
      });
  }

  private toRecord(library: LibraryResponse): LibraryRecord {
    const date = new Date(library.createdAt);

    const summary = this.toAccessSummaryFromCounts(library);
    const existing = this.libraries().find((item) => item.id === library.id);
    const assignedFlow =
      library.flowName !== undefined
        ? library.flowName && library.flowVersion != null
          ? `${library.flowName} · v${library.flowVersion}`
          : 'Aucun flow'
        : (existing?.assignedFlow ?? 'Aucun flow');
    const processing = this.processingByLibrary()[library.id];
    const monitoring = processing
      ? processing.watchEnabled
        ? 'Automatique'
        : 'Manuelle'
      : (existing?.monitoring ?? 'Manuelle');

    return {
      id: library.id,

      name: library.name,

      isActive: library.isActive,

      ...summary,

      lastScan: 'Jamais',

      associatedFlows: assignedFlow,

      assignedFlow,

      monitoring,

      modifiedAt: new Intl.DateTimeFormat('fr-FR', { dateStyle: 'medium' }).format(date),

      modifiedOrder: date.getTime(),
    };
  }

  private toAccessSummaryFromCounts(
    library: LibraryResponse,
  ): Pick<
    LibraryRecord,
    | 'configuredNodes'
    | 'accessModes'
    | 'nodePaths'
    | 'state'
    | 'configuredNodeCount'
    | 'directAccessNodeCount'
    | 'transferNodeCount'
  > {
    const directCount = library.directAccessNodeCount;

    const transferCount = library.transferNodeCount;

    const configuredCount = library.configuredNodeCount;

    const modes = [
      directCount ? `${directCount} direct(s)` : '',

      transferCount ? `${transferCount} transfert(s)` : '',
    ]

      .filter(Boolean)

      .join(' · ');

    return {
      configuredNodeCount: configuredCount,

      directAccessNodeCount: directCount,

      transferNodeCount: transferCount,

      configuredNodes: configuredCount ? `${configuredCount} nœud(s) utilisable(s)` : 'Aucun accès',

      accessModes: modes || 'Non configuré',

      nodePaths: directCount ? 'Consulter les chemins configurés' : 'Aucun chemin direct',

      state: this.libraryState(library.isActive, configuredCount),
    };
  }
}
