import { FlowDetailsComponent } from './editor/flow-details.component';
import { FlowDocument, flowDocumentKey } from '../../shared/models/flow-validation';
import { FlowComponentDefinition } from '../../shared/models/flow-component';
import { RecordFormData } from '../../shared/ui/record-form.component';
import { MatDialog } from '@angular/material/dialog';
import { FlowDeleteDialogComponent } from './flow-delete-dialog.component';
import { ConfirmationDialogComponent } from '../../shared/ui/confirmation-dialog.component';
import { FileDownload } from '../../shared/services/file-download';
import { NotificationService } from '../../shared/services/notification.service';
import { HttpErrorResponse } from '@angular/common/http';
import { FlowValidation } from '../../shared/services/flow-validation';
import { map, catchError, of, timer, switchMap, merge, Subject } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatMenuModule } from '@angular/material/menu';
import {
  DataTableListActionEvent,
  DataTableActionEvent,
  DataTableSelectionActionEvent,
  DataTableRow,
} from '../../shared/ui/data-table.component';
import { IconComponent } from '../../shared/ui/icon.component';
import { CatalogEmptyState } from '../../shared/templates/management-catalog-template.component';
import { ResourceCatalogTemplateComponent } from '../../shared/templates/resource-catalog-template.component';
import {
  ResourceListGroup,
  ResourceListItem,
  ResourceListSelectionAction,
} from '../../shared/ui/resource-list.component';
import { MEDIA_TYPE_ICONS, MEDIA_TYPE_OPTIONS, MediaType } from '../../shared/models/media-type';
import { createFlowCreationForm, FLOW_CREATION_FORM } from '../../shared/models/flow-creation';
import { RecordFormComponent } from '../../shared/ui/record-form.component';
import { DrawerActionButtonComponent } from '../../shared/ui/drawer-action-button/drawer-action-button.component';
import { FlowTemplates } from '../../shared/services/flow-templates';
import { isTemplateConfigurableField } from '../../shared/models/flow-templates';
import { FlowComponentPacks } from '../../shared/services/flow-component-packs';
import { FlowPreviewComponent } from '../../shared/ui/flow-preview/flow-preview';
import { firstValueFrom } from 'rxjs';
import { FileImportComponent } from '../../shared/ui/file-import.component';
import {
  PageShellContextService,
  PageShellSearchResult,
} from '../../core/layout/page-shell-context';

interface FlowRecord extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly category: MediaType;
  readonly mediaType: string;
  readonly steps: string;
  readonly nodeCount: number;
  readonly publishedVersion: number;
  readonly version: string;
  readonly summary: string;
  readonly components: string;
  readonly state: string;
  readonly status: string;
  readonly backup: string;
  readonly modifiedAt: string;
  readonly modifiedOrder: number;
}

const MEDIA_TYPE_TONES = {
  video: 'blue',
  image: 'purple',
  music: 'warning',
  subtitles: 'default',
} as const satisfies Record<MediaType, NonNullable<ResourceListGroup['tone']>>;

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-flow-catalog-page',
  imports: [
    FlowDetailsComponent,
    ResourceCatalogTemplateComponent,
    IconComponent,
    MatButtonModule,
    MatCheckboxModule,
    MatMenuModule,
    RecordFormComponent,
    DrawerActionButtonComponent,
    FlowPreviewComponent,
    FileImportComponent,
  ],
  templateUrl: './flow-catalog-page.component.html',
  styleUrl: './flow-catalog-page.component.scss',
})
export class FlowCatalogPageComponent {
  private readonly destroyRef = inject(DestroyRef);
  private readonly pageShellContext = inject(PageShellContextService);
  protected readonly auth = inject(AuthService);
  protected readonly deletingFlow = signal<string | null>(null);
  private readonly refreshCatalog = new Subject<void>();
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });

  protected readonly actionDefinition = signal<RecordFormData | null>(null);
  private readonly globalFlowImportPicker =
    viewChild<ElementRef<HTMLInputElement>>('globalFlowImportPicker');
  protected readonly actionForm = computed(
    () => this.actionDefinition() ?? this.flowCreationForm(),
  );
  private readonly templateSource = signal<{
    readonly id: string;
    readonly version: number;
  } | null>(null);
  private readonly packSource = signal<{ readonly id: string; readonly version: number } | null>(
    null,
  );
  protected readonly sourcePreview = signal<FlowDocument | null>(null);
  protected readonly sourcePreviewComponents = signal<readonly FlowComponentDefinition[]>([]);
  protected readonly sourcePreviewStatus = signal<'idle' | 'loading' | 'ready' | 'error'>('idle');
  protected readonly templateSetupSelection = signal<Readonly<Record<string, readonly string[]>>>(
    {},
  );
  protected readonly templateConfigurableFields = computed(() => {
    const definition = this.sourcePreview();
    const components = this.sourcePreviewComponents();
    if (!definition) return [];
    return definition.nodes.flatMap((node) => {
      const component = components.find(
        (item) =>
          item.id === node.component.componentId && item.pluginId === node.component.pluginId,
      );
      const fields =
        component?.configurationSchema?.fields.filter(isTemplateConfigurableField) ?? [];
      return fields.length ? [{ node, component: component!, fields }] : [];
    });
  });
  private previewRequestId = 0;

  private async loadSourcePreview(id: string, version: number): Promise<void> {
    const requestId = ++this.previewRequestId;
    this.sourcePreview.set(null);
    this.sourcePreviewComponents.set([]);
    this.sourcePreviewStatus.set('loading');
    try {
      const [saved, plugins] = await Promise.all([
        firstValueFrom(this.api.versionStatus(id, version)),
        this.api.catalog(),
      ]);
      if (requestId !== this.previewRequestId) return;
      this.sourcePreview.set(saved.definition);
      this.sourcePreviewComponents.set(
        plugins.flatMap((plugin) =>
          plugin.components.map((component) => ({
            ...component,
            pluginId: plugin.id,
            pluginName: plugin.name,
          })),
        ),
      );
      this.sourcePreviewStatus.set('ready');
    } catch {
      if (requestId === this.previewRequestId) this.sourcePreviewStatus.set('error');
    }
  }

  private clearSourcePreview(): void {
    this.previewRequestId++;
    this.sourcePreview.set(null);
    this.sourcePreviewComponents.set([]);
    this.sourcePreviewStatus.set('idle');
  }
  protected async submitAction(values: Record<string, string>): Promise<void> {
    const packSource = this.packSource();
    if (packSource) {
      try {
        await this.componentPacks.createFromFlow({
          sourceFlowId: packSource.id,
          sourceVersion: packSource.version,
          name: values['name'],
          description: values['description'] ?? '',
        });
        this.packSource.set(null);
        this.actionDefinition.set(null);
        this.clearSourcePreview();
        this.actionOpen.set(false);
        this.notifications.show('Pack créé depuis la version publiée du flow.');
      } catch (error) {
        this.notifications.show(
          error instanceof HttpErrorResponse
            ? (error.error?.title ?? 'Création du pack impossible.')
            : 'Création du pack impossible.',
        );
      }
      return;
    }
    const templateSource = this.templateSource();
    if (templateSource) {
      try {
        if (this.sourcePreviewStatus() !== 'ready') {
          this.notifications.show(
            'Attendez le chargement du flow source avant de créer le modèle.',
          );
          return;
        }
        await this.templates.createFromFlow({
          sourceFlowId: templateSource.id,
          sourceVersion: templateSource.version,
          name: values['name'],
          description: values['description'] ?? '',
          configuration: {
            nodes: Object.entries(this.templateSetupSelection())
              .filter(([, fieldIds]) => fieldIds.length > 0)
              .map(([nodeId, fieldIds]) => ({ nodeId, fieldIds })),
          },
        });
        this.templateSource.set(null);
        this.templateSetupSelection.set({});
        this.actionDefinition.set(null);
        this.clearSourcePreview();
        this.actionOpen.set(false);
        this.notifications.show('Modèle créé depuis la version publiée du flow.');
      } catch (error) {
        this.notifications.show(
          error instanceof HttpErrorResponse
            ? (error.error?.title ?? 'Création du modèle impossible.')
            : 'Création du modèle impossible.',
        );
      }
      return;
    }
    this.startFlow(values);
  }

  private readonly dialog = inject(MatDialog);
  private readonly downloads = inject(FileDownload);
  private readonly notifications = inject(NotificationService);
  protected readonly importing = signal(false);
  protected readonly exporting = signal(false);
  protected openGlobalImportPicker(): void {
    if (!this.importing()) this.globalFlowImportPicker()?.nativeElement.click();
  }
  protected onGlobalImportChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (file) void this.onImportFile(file);
  }
  protected async onImportFile(file: File): Promise<void> {
    this.importing.set(true);
    try {
      await this.onListAction({ actionId: 'import', file });
    } finally {
      this.importing.set(false);
    }
  }
  protected readonly rowActions = [
    { id: 'edit', label: 'Ouvrir l’éditeur' },
    { id: 'export', label: 'Exporter' },
    { id: 'versions', label: 'Versions / Comparer' },
    {
      id: 'template',
      label: 'Créer un modèle',
      visibleWhen: (row: DataTableRow) => Number(row['publishedVersion']) > 0,
    },
    {
      id: 'component-pack',
      label: 'Créer un pack de composants',
      visibleWhen: (row: DataTableRow) => Number(row['publishedVersion']) > 0,
    },
    {
      id: 'delete',
      label: 'Supprimer',
      tone: 'danger' as const,
      visibleWhen: () => this.auth.isAdmin() && this.deletingFlow() === null,
    },
  ];
  protected readonly selectionActions: readonly ResourceListSelectionAction[] = [
    {
      id: 'publish',
      label: 'Publier les brouillons',
      labelFor: (rows) => {
        const count = rows.filter((row) => row['state'] === 'Brouillon').length;
        return `Publier ${count} brouillon(s)`;
      },
      visibleWhen: (rows) =>
        this.auth.isAdmin() &&
        !this.selectingFlows() &&
        rows.some((row) => row['state'] === 'Brouillon'),
    },
    {
      id: 'delete',
      label: 'Supprimer la sélection',
      visibleWhen: () => this.auth.isAdmin() && !this.selectingFlows(),
    },
  ];
  protected readonly selectedFlowIds = signal<ReadonlySet<string>>(new Set());
  protected readonly selectingFlows = signal(false);
  protected async onListAction(event: DataTableListActionEvent): Promise<void> {
    if (!event.file) return;
    try {
      const result = await this.api.import(event.file);
      this.notifications.show(result.imported + ' flow(s) importé(s).');
    } catch (error) {
      this.notifications.show(
        error instanceof HttpErrorResponse
          ? (error.error?.title ??
              error.error?.issues?.map((issue: { message: string }) => issue.message).join(' ') ??
              'Import impossible.')
          : 'Import impossible.',
      );
    }
  }
  protected async onRowAction(event: DataTableActionEvent): Promise<void> {
    if (event.actionId === 'delete') {
      await this.confirmDeleteFlow(event.row);
      return;
    }
    if (event.actionId === 'edit') {
      void this.router.navigate(['/flows', String(event.row['id']), 'edit'], {
        info: { flowEditorDirection: 'forward' },
      });
      return;
    }
    if (event.actionId === 'component-pack') {
      const version = Number(event.row['publishedVersion']);
      this.packSource.set({ id: String(event.row['id']), version });
      this.templateSource.set(null);
      void this.loadSourcePreview(String(event.row['id']), version);
      this.actionDefinition.set({
        title: 'Créer un pack de composants depuis un flow',
        description: `La version publiée v${version} sera copiée avec une entrée et ses sorties de flow.`,
        submitLabel: 'Créer le pack',
        fields: [
          { id: 'name', label: 'Nom du pack', type: 'text', required: true, maxLength: 128 },
          { id: 'description', label: 'Description', type: 'text', maxLength: 1000 },
        ],
        initialValues: { name: `${String(event.row['name'])} — Pack`, description: '' },
      });
      this.actionOpen.set(true);
      return;
    }
    if (event.actionId === 'template') {
      const version = Number(event.row['publishedVersion']);
      this.templateSource.set({ id: String(event.row['id']), version });
      this.templateSetupSelection.set({});
      this.packSource.set(null);
      void this.loadSourcePreview(String(event.row['id']), version);
      this.actionDefinition.set({
        title: 'Créer un modèle depuis un flow',
        description: `La version publiée v${version} sera copiée. Le flow d’origine ne sera pas modifié.`,
        submitLabel: 'Créer le modèle',
        fields: [
          {
            id: 'name',
            label: 'Nom du modèle',
            type: 'text',
            required: true,
            maxLength: 128,
          },
          {
            id: 'description',
            label: 'Description',
            type: 'text',
            maxLength: 1000,
          },
        ],
        initialValues: {
          name: `${String(event.row['name'])} — Modèle`,
          description: '',
        },
      });
      this.actionOpen.set(true);
      return;
    }
    if (event.actionId === 'versions') {
      this.onFlowOpen(event.row);
      this.detailTab.set(1);
      return;
    }
    try {
      this.downloads.save(
        await this.api.export(String(event.row['id'])),
        String(event.row['id']) + '.json',
      );
    } catch {
      this.notifications.show('Export impossible.');
    }
  }

  protected templateFieldSelected(nodeId: string, fieldId: string): boolean {
    return this.templateSetupSelection()[nodeId]?.includes(fieldId) ?? false;
  }

  protected toggleTemplateField(nodeId: string, fieldId: string, selected: boolean): void {
    const selection = this.templateSetupSelection();
    const fieldIds = new Set(selection[nodeId] ?? []);
    if (selected) fieldIds.add(fieldId);
    else fieldIds.delete(fieldId);
    this.templateSetupSelection.set({ ...selection, [nodeId]: [...fieldIds] });
  }

  protected async onSelectionAction(event: DataTableSelectionActionEvent): Promise<void> {
    if (event.actionId === 'publish') {
      await this.publishSelected(event.rows);
      return;
    }
    if (event.actionId === 'delete') {
      await this.deleteSelected(event.rows);
      return;
    }
  }
  protected async exportSelectedFlows(): Promise<void> {
    const ids = [...this.selectedFlowIds()];
    if (!ids.length || this.exporting()) return;
    this.exporting.set(true);
    try {
      this.downloads.save(await this.api.exportMany(ids), 'flows.zip');
    } catch {
      this.notifications.show('Export de la sélection impossible.');
    } finally {
      this.exporting.set(false);
    }
  }
  protected async publishSelected(rows: readonly DataTableRow[]): Promise<void> {
    if (!this.auth.isAdmin() || this.selectingFlows()) return;
    const drafts = rows.filter((flow) => flow['state'] === 'Brouillon');
    if (!drafts.length) return;
    const accepted = await firstValueFrom(
      this.dialog
        .open(ConfirmationDialogComponent, {
          data: {
            title: `Publier jusqu’à ${drafts.length} brouillon(s) ?`,
            message:
              'Seuls les brouillons valides et différents de leur dernière version publiée seront publiés. Les autres seront ignorés.',
            confirmLabel: 'Vérifier et publier',
          },
        })
        .afterClosed(),
    );
    if (!accepted) return;
    this.selectingFlows.set(true);
    let published = 0,
      unchanged = 0,
      failed = 0;
    for (const flow of drafts) {
      try {
        const flowId = String(flow['id']);
        const draft = await this.api.get(flowId);
        if (draft.draftRevision == null) {
          unchanged++;
          continue;
        }
        const currentVersion = draft.publishedVersion ?? 0;
        if (currentVersion > 0) {
          const current = await firstValueFrom(this.api.versionStatus(flowId, currentVersion));
          if (flowDocumentKey(current.definition) === flowDocumentKey(draft.definition)) {
            unchanged++;
            continue;
          }
        }
        const validation = await this.api.validateDocument(draft.definition);
        if (!validation.isValid) {
          failed++;
          continue;
        }
        await this.api.save(draft.definition, currentVersion, draft.draftRevision);
        published++;
      } catch {
        failed++;
      }
    }
    this.selectingFlows.set(false);
    this.selectedFlowIds.set(new Set());
    this.refreshCatalog.next();
    this.notifications.show(
      `${published} publié(s), ${unchanged} sans changement, ${failed} en échec ou invalide(s).`,
    );
  }
  protected async deleteSelected(rows: readonly DataTableRow[]): Promise<void> {
    if (!this.auth.isAdmin() || this.selectingFlows()) return;
    const flows = [...rows];
    if (!flows.length) return;
    const usageById = new Map<string, Awaited<ReturnType<FlowValidation['usage']>>>();
    this.selectingFlows.set(true);
    try {
      await Promise.all(
        flows.map(async (flow) => {
          const id = String(flow['id']);
          usageById.set(id, await this.api.usage(id));
        }),
      );
    } catch (error) {
      this.selectingFlows.set(false);
      this.notifications.show(
        AuthService.errorMessage(error, 'Impossible de vérifier les bibliothèques attribuées.'),
      );
      return;
    }
    this.selectingFlows.set(false);
    const usages = [...usageById.values()].flat();
    const accepted = await firstValueFrom(
      this.dialog
        .open(ConfirmationDialogComponent, {
          data: {
            title: `Supprimer ${flows.length} flow(s) ?`,
            message: `Les flows sélectionnés seront supprimés. ${usages.length ? `Les bibliothèques utilisant ces flows perdront leur attribution : ${usages.map((item) => item.libraryName).join(', ')}.` : ''} Cette action est irréversible.`,
            confirmLabel: 'Supprimer',
            variant: 'danger',
          },
        })
        .afterClosed(),
    );
    if (!accepted) return;
    this.selectingFlows.set(true);
    const results = await Promise.allSettled(
      flows.map((flow) => {
        const id = String(flow['id']);
        return this.api.remove(id, (usageById.get(id)?.length ?? 0) > 0);
      }),
    );
    this.selectingFlows.set(false);
    this.selectedFlowIds.set(new Set());
    this.refreshCatalog.next();
    const succeeded = results.filter((result) => result.status === 'fulfilled').length;
    this.notifications.show(`${succeeded}/${flows.length} flow(s) supprimé(s).`);
  }
  private readonly api = inject(FlowValidation);
  protected async confirmDeleteFlow(row: DataTableRow): Promise<void> {
    if (!this.auth.isAdmin() || this.deletingFlow() !== null) return;
    const id = String(row['id']),
      name = String(row['name']);
    this.deletingFlow.set(id);
    try {
      const libraries = await this.api.usage(id);
      const confirmed = await firstValueFrom(
        this.dialog.open(FlowDeleteDialogComponent, { data: { name, libraries } }).afterClosed(),
      );
      if (!confirmed) return;
      await this.api.remove(id, libraries.length > 0);
      if (this.selectedFlow()?.id === id) this.onDetailClosed();
      this.refreshCatalog.next();
      this.notifications.show(`Le flow « ${name} » a été supprimé.`);
    } catch (error) {
      this.notifications.show(AuthService.errorMessage(error, 'Impossible de supprimer ce flow.'));
    } finally {
      this.deletingFlow.set(null);
    }
  }
  private readonly templates = inject(FlowTemplates);
  private readonly componentPacks = inject(FlowComponentPacks);
  private readonly catalog = toSignal(
    merge(timer(0, 5000), this.refreshCatalog).pipe(
      switchMap(() =>
        this.api.list().pipe(
          map((flows) => ({ state: 'ready' as const, flows })),
          catchError(() => of({ state: 'error' as const, flows: [] })),
        ),
      ),
      map(
        ({
          state,
          flows,
        }): { state: 'ready' | 'error' | 'loading'; records: readonly FlowRecord[] } => ({
          state,
          records: flows.map((flow) => ({
            id: flow.id,
            name: flow.name,
            category: flow.mediaType,
            mediaType:
              (MEDIA_TYPE_OPTIONS.find((type) => type.value === flow.mediaType)?.label ??
                flow.mediaType) +
              ' · ' +
              flow.nodeCount +
              (flow.nodeCount === 1 ? ' étape' : ' étapes'),
            steps: flow.nodeCount + (flow.nodeCount === 1 ? ' étape' : ' étapes'),
            nodeCount: flow.nodeCount,
            publishedVersion: flow.version,
            version: 'v' + flow.version,
            summary: flow.nodeCount + ' étapes',
            components: flow.nodeCount + ' composants',
            state: flow.isDraft ? 'Brouillon' : 'Publié',
            status: (flow.isDraft ? 'Brouillon' : 'Publié') + ' · v' + flow.version,
            backup:
              flow.backupStatus === 'draft'
                ? 'Brouillon en base'
                : flow.backupStatus === 'ready'
                  ? 'Disponible'
                  : flow.backupStatus === 'failed'
                    ? 'En échec'
                    : 'En attente',
            modifiedAt: new Date(flow.updatedAt).toLocaleString('fr-FR'),
            modifiedOrder: Date.parse(flow.updatedAt),
          })),
        }),
      ),
    ),
    { initialValue: { state: 'loading' as const, records: [] as readonly FlowRecord[] } },
  );
  protected readonly catalogState = computed(() => this.catalog().state);
  private readonly records = computed(() => this.catalog().records);

  protected readonly title = computed(() => (this.routeData()['title'] as string) ?? 'Flows');
  protected readonly searchQuery = signal('');
  protected readonly detailTab = signal(0);
  protected refreshDetails(): void {
    this.refreshCatalog.next();
  }
  protected readonly actionOpen = signal(false);
  protected readonly flowCreationForm = signal(FLOW_CREATION_FORM);
  protected readonly creationRecordForm = viewChild(RecordFormComponent);
  protected readonly selectedFlow = signal<FlowRecord | null>(null);
  protected readonly detailTitle = computed(
    () =>
      this.records().find((flow) => flow.id === this.selectedFlow()?.id)?.name ??
      this.selectedFlow()?.name ??
      'Flow',
  );
  protected readonly detailDescription = computed(() => {
    const flow = this.selectedFlow();
    if (!flow) return 'Détails du flow';
    return (
      flow.mediaType +
      ' · ' +
      (flow.state === 'Brouillon'
        ? 'Brouillon · ' +
          (flow.publishedVersion > 0
            ? 'Dernière publication ' + flow.version
            : 'Aucune version publiée')
        : flow.state + ' · ' + flow.version)
    );
  });
  protected readonly filteredFlows = computed(() => {
    const type = this.typeFilter();
    const query = this.searchQuery().trim().toLocaleLowerCase('fr-FR');
    return this.records().filter(
      (flow) =>
        (!type || flow.category === type) &&
        (!query ||
          flow.name.toLocaleLowerCase('fr-FR').includes(query) ||
          flow.mediaType.toLocaleLowerCase('fr-FR').includes(query) ||
          flow.summary.toLocaleLowerCase('fr-FR').includes(query) ||
          flow.status.toLocaleLowerCase('fr-FR').includes(query) ||
          flow.state.toLocaleLowerCase('fr-FR').includes(query)),
    );
  });
  protected readonly typeFilter = signal<MediaType | null>(null);
  protected readonly totalCount = computed(() => this.records().length);
  protected readonly typeTiles = computed(() =>
    MEDIA_TYPE_OPTIONS.map((type) => ({
      ...type,
      icon: MEDIA_TYPE_ICONS[type.value],
      count: this.records().filter((flow) => flow.category === type.value).length,
    })),
  );
  protected toggleType(type: MediaType): void {
    this.typeFilter.update((current) => (current === type ? null : type));
  }
  protected readonly emptyState: CatalogEmptyState = {
    icon: '▶',
    eyebrow: 'FLOWS',
    title: 'Créez votre premier flow',
    description:
      'Un flow décrit les étapes appliquées à vos médias : analyse, conversion, sous-titres, publication. Partez d’un modèle ou importez un flow existant.',
    chips: MEDIA_TYPE_OPTIONS.map((type) => type.label),
    actionLabel: 'Créer le premier flow',
  };
  protected readonly catalogueItems = computed<readonly ResourceListItem[]>(() =>
    this.filteredFlows().map((flow) => ({
      id: flow.id,
      name: flow.name,
      subtitle: flow.steps,
      meta: flow.modifiedAt,
      group: {
        id: flow.category,
        label:
          MEDIA_TYPE_OPTIONS.find((type) => type.value === flow.category)?.label ?? flow.category,
        icon: MEDIA_TYPE_ICONS[flow.category],
        tone: MEDIA_TYPE_TONES[flow.category],
        countUnit: { singular: 'flow', plural: 'flows' },
      },
      tags: [
        {
          label: flow.state === 'Brouillon' ? 'Brouillon' : 'Publié · ' + flow.version,
          tone: flow.state === 'Brouillon' ? 'warn' : 'ok',
        },
        {
          label: 'JSON : ' + flow.backup.toLocaleLowerCase('fr-FR'),
          tone: flow.backup === 'En échec' ? 'danger' : 'neutral',
        },
      ],
      rowData: flow,
    })),
  );

  constructor() {
    this.destroyRef.onDestroy(
      this.pageShellContext.register({
        searchPlaceholder: 'Rechercher dans les flows…',
        query: this.searchQuery,
        onSearchQueryChange: (query) => this.searchQuery.set(query),
        searchResults: (query) => this.flowSearchResults(query),
        actions: [
          {
            id: 'import-flows',
            label: 'Importer',
            icon: 'upload',
            visibleWhen: () => this.selectedFlowIds().size === 0 && !this.importing(),
            run: () => this.openGlobalImportPicker(),
          },
          {
            id: 'export-flows',
            label: 'Exporter',
            icon: 'outward',
            visibleWhen: () => this.selectedFlowIds().size > 0 && !this.exporting(),
            run: () => void this.exportSelectedFlows(),
          },
          {
            id: 'create-flow',
            label: 'Créer un flow',
            icon: 'add',
            run: () => void this.openFlowCreation(),
          },
        ],
      }),
    );
  }

  private flowSearchResults(query: string): readonly PageShellSearchResult[] {
    const normalized = query.trim().toLocaleLowerCase('fr-FR');
    return this.records()
      .filter(
        (flow) =>
          (!this.typeFilter() || flow.category === this.typeFilter()) &&
          (!normalized ||
            [flow.name, flow.mediaType, flow.summary, flow.status, flow.state]
              .join(' ')
              .toLocaleLowerCase('fr-FR')
              .includes(normalized)),
      )
      .slice(0, 8)
      .map((flow) => ({
        id: `flow:${flow.id}`,
        label: flow.name,
        description: `${flow.mediaType} · ${flow.status}`,
        group: 'Flows',
        glyph: '▶',
        run: () => this.onFlowOpen(flow.id),
      }));
  }

  protected onFlowOpen(row: DataTableRow | string): void {
    this.detailTab.set(0);
    const id = typeof row === 'string' ? row : String(row['id']);
    this.selectedFlow.set(this.records().find((flow) => flow.id === id) ?? null);
  }

  protected onDetailClosed(): void {
    this.selectedFlow.set(null);
  }

  protected async openFlowCreation(): Promise<void> {
    this.clearSourcePreview();
    this.actionDefinition.set(null);
    this.templateSource.set(null);
    this.packSource.set(null);
    try {
      this.flowCreationForm.set(
        createFlowCreationForm(await firstValueFrom(this.templates.list())),
      );
    } catch {
      this.flowCreationForm.set(createFlowCreationForm());
    }
    this.actionOpen.set(true);
  }

  protected closeFlowCreation(): void {
    this.clearSourcePreview();
    this.actionOpen.set(false);
    this.templateSource.set(null);
    this.packSource.set(null);
  }

  protected startFlow(values: Record<string, string>): void {
    const starter = values['starter'] ?? 'blank';
    const templateId = starter.startsWith('template:') ? starter.slice('template:'.length) : null;
    if (templateId) {
      void this.router.navigate(['/libraries/templates'], {
        queryParams: { template: templateId, configure: true, name: values['name'] },
        info: { flowEditorDirection: 'forward' },
      });
      this.actionOpen.set(false);
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
    this.actionOpen.set(false);
  }

  protected readonly creationStep = computed(() => this.creationRecordForm()?.stepIndex() ?? 0);

  protected readonly creationStepCount = computed(
    () => this.creationRecordForm()?.stepCount() ?? 3,
  );

  protected readonly creationProgress = computed(() => ({
    current: this.creationStep() + 1,
    total: this.creationStepCount(),
  }));

  protected readonly creationStepLabel = computed(
    () => this.creationRecordForm()?.primaryLabel() ?? 'Continuer',
  );

  protected readonly creationSecondaryLabel = computed(
    () => this.creationRecordForm()?.secondaryLabel() ?? 'Annuler',
  );

  protected creationPrimary(): void {
    this.creationRecordForm()?.primary();
  }

  protected creationSecondary(): void {
    this.creationRecordForm()?.secondary();
  }

  protected async importCreationFile(file: File): Promise<void> {
    this.importing.set(true);
    try {
      const result = await this.api.import(file);
      this.notifications.show(`${result.imported} flow(s) importé(s).`);
      this.actionOpen.set(false);
      this.refreshCatalog.next();
    } catch (error) {
      this.notifications.show(
        error instanceof HttpErrorResponse
          ? (error.error?.title ?? 'Import impossible.')
          : 'Import impossible.',
      );
    } finally {
      this.importing.set(false);
    }
  }
}
