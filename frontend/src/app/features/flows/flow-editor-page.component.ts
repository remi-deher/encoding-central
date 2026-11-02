import { flowDocumentNodes } from '../../shared/models/flow-document-graph';
import { DetailDrawerComponent } from '../../shared/ui/detail-drawer/detail-drawer';
import { MatButtonModule } from '@angular/material/button';
import { FlowComponentDrop, FlowPaletteDrop } from '../../shared/ui/flow-palette-drag';
import { toObservable, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { timer, switchMap, catchError, of, from, firstValueFrom } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';
import { FlowValidation } from '../../shared/services/flow-validation';
import {
  flowDocumentKey,
  flowPublishedVersion,
  FlowDocument,
  FlowPluginCatalog,
} from '../../shared/models/flow-validation';
import { FlowContinuationSource } from '../../shared/models/flow-sequence';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  viewChild,
  signal,
  untracked,
} from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { FlowEditorAssistant } from '../../shared/services/flow-editor-assistant.service';
import {
  MEDIA_INPUT_COMPONENTS,
  createFlowInputConfigurationSchema,
  getFlowInputComponents,
} from '../../shared/models/flow-control-components';
import { canConnectFlowGraph } from '../../shared/models/flow-graph';
import { NotificationService } from '../../shared/services/notification.service';
import { MEDIA_TYPE_OPTIONS, MediaType } from '../../shared/models/media-type';
import { RecordFormComponent, RecordFormData } from '../../shared/ui/record-form.component';
import {
  createDefaultFlowComponentConfiguration,
  isFlowComponentCompatible,
  getFlowConfigurationSchema,
  FlowComponentConfiguration,
  FlowComponentDefinition,
  FlowComponentOutput,
  FlowGraphConnection,
  FlowGraphNode,
} from '../../shared/models/flow-component';
import { FlowGraphEditorComponent } from '../../shared/ui/flow-graph-editor.component';
import { ComponentConfigurationFormComponent } from '../../shared/ui/component-configuration-form.component';
import { FlowSimulation } from '../../shared/services/flow-simulation';
import { FlowWorkspaceTemplateComponent } from '../../shared/templates/flow-workspace-template.component';
import { createUuid } from '../../shared/utils/uuid';
import { FlowTemplates } from '../../shared/services/flow-templates';
import { FlowComponentPacks } from '../../shared/services/flow-component-packs';
import {
  FlowComponentPackDetails,
  FlowComponentPackSummary,
} from '../../shared/models/flow-component-packs';
import { FlowComponentPackInstance } from '../../shared/models/flow-validation';
import { FlowCommandBarComponent } from './editor/flow-command-bar.component';
import { FlowPaletteDockComponent } from './editor/flow-palette-dock.component';
import {
  FlowSimulationTrayComponent,
  FlowValidationIssue,
} from './editor/flow-simulation-tray.component';

const TYPE_OPTIONS = [
  { value: 'video', label: 'Vidéo' },
  { value: 'image', label: 'Image' },
  { value: 'audio', label: 'Audio' },
  { value: 'subtitles', label: 'Sous-titres' },
  { value: 'general', label: 'Logique' },
] as const;

type ComponentDestination =
  | { readonly kind: 'continuation'; readonly sources: readonly FlowContinuationSource[] }
  | {
      readonly kind: 'branch';
      readonly sourceNodeId: string;
      readonly output: FlowComponentOutput;
    };

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-flow-editor-page',
  providers: [FlowSimulation],
  host: {
    '(window:beforeunload)': 'warnBeforeUnload($event)',
    '(window:keydown)': 'onWindowKeyDown($event)',
  },
  imports: [
    MatButtonModule,
    FlowWorkspaceTemplateComponent,
    DetailDrawerComponent,
    FlowComponentDrop,
    FlowCommandBarComponent,
    FlowPaletteDockComponent,
    ComponentConfigurationFormComponent,
    FlowSimulationTrayComponent,
    FlowGraphEditorComponent,
    RecordFormComponent,
  ],
  templateUrl: './flow-editor-page.component.html',
  styleUrl: './flow-editor-page.component.scss',
})
export class FlowEditorPageComponent {
  protected readonly simulator = inject(FlowSimulation);
  private readonly route = inject(ActivatedRoute);
  private readonly notifications = inject(NotificationService);
  private readonly serverValidation = inject(FlowValidation);
  private readonly templates = inject(FlowTemplates);
  private readonly editorRouter = inject(Router);
  private readonly packApi = inject(FlowComponentPacks);

  // Workbench shell state
  protected readonly graphEditor = viewChild(FlowGraphEditorComponent);
  protected readonly paletteOpen = signal(false);
  protected readonly palettePinned = signal(false);
  protected readonly trayOpen = signal(false);
  protected readonly autoLayoutRequest = signal(0);
  protected readonly trayTab = signal<'simulation' | 'diagnostics'>('simulation');
  protected readonly validationIssues = signal<readonly FlowValidationIssue[]>([]);
  protected readonly validationSuccess = signal<boolean | null>(null);
  private readonly connectionVisibilityOverride = signal<boolean | null>(null);
  protected readonly showFlowConnections = computed(
    () => this.connectionVisibilityOverride() ?? true,
  );

  protected readonly configuringNodeId = signal<string | undefined>(undefined);
  protected configurationReferences() {
    const current = this.configuringNode()?.id;
    const upstream = new Set<string>();
    const pending = current ? [current] : [];
    while (pending.length) {
      const target = pending.pop()!;
      for (const edge of this.connections().filter((edge) => edge.targetNodeId === target)) {
        if (!upstream.has(edge.sourceNodeId)) {
          upstream.add(edge.sourceNodeId);
          pending.push(edge.sourceNodeId);
        }
      }
    }
    return {
      artifact: this.nodes()
        .filter((node) => upstream.has(node.id) && node.component.producesFiles)
        .map((node) => ({
          value: 'artifact:' + encodeURIComponent(node.id) + '/0',
          label: 'Premier fichier produit · ' + (node.name || node.component.name),
        })),
      'flow-node': this.nodes()
        .filter(
          (node) =>
            node.id !== current &&
            node.component.kind === 'condition' &&
            !['base', 'conditions', 'nodes'].includes(node.component.pluginId),
        )
        .map((node) => ({ value: node.id, label: node.name || node.component.name })),
    };
  }
  protected readonly nodes = signal<readonly FlowGraphNode[]>([]);
  protected readonly connections = signal<readonly FlowGraphConnection[]>([]);
  protected readonly componentPacks = signal<readonly FlowComponentPackInstance[]>([]);
  protected readonly packCatalog = signal<readonly FlowComponentPackSummary[]>([]);
  protected readonly packCreationOpen = signal(false);
  protected readonly aiProposalRollback = signal<FlowDocument | null>(null);

  protected readonly flowMediaType = signal<MediaType>('video');
  private readonly installedComponents = signal<readonly FlowComponentDefinition[]>([]);

  protected readonly components = computed(() =>
    this.installedComponents().filter(
      (component) =>
        component.kind !== 'input' && isFlowComponentCompatible(component, this.flowMediaType()),
    ),
  );
  protected readonly otherMediaComponents = computed(() =>
    this.installedComponents().filter(
      (component) =>
        component.kind !== 'input' && !isFlowComponentCompatible(component, this.flowMediaType()),
    ),
  );

  protected readonly typeOptions = computed(() =>
    TYPE_OPTIONS.filter((option) =>
      this.components().some((component) => component.mediaType === option.value),
    ),
  );

  protected readonly pluginOptions = computed(() => [
    ...new Map(
      this.components().map((component) => [
        component.pluginId,
        { value: component.pluginId, label: component.pluginName },
      ]),
    ).values(),
  ]);

  protected readonly configuringNode = computed(() =>
    this.nodes().find((node) => node.id === this.configuringNodeId()),
  );

  protected readonly configuringSchema = computed(() =>
    this.configuringNode()?.component.kind === 'input'
      ? createFlowInputConfigurationSchema(this.flowMediaType())
      : getFlowConfigurationSchema(
          this.configuringNode()?.component.configurationSchema,
          this.flowMediaType(),
        ),
  );

  protected readonly configuringValues = computed<FlowComponentConfiguration>(() => {
    const node = this.configuringNode();
    return node?.component.kind === 'input'
      ? { inputComponentId: node.component.id }
      : (node?.configuration ?? {});
  });

  private readonly flowId = this.route.snapshot.paramMap.get('flowId');
  private readonly savedName = signal<string | null>(null);

  protected readonly flowTitle = computed(() => {
    if (this.savedName()) return this.savedName()!;
    const name = this.route.snapshot.queryParamMap.get('name');
    if (name) return name;
    return this.flowId ? 'Modifier un flow' : 'Nouveau flow';
  });

  protected readonly componentDestination = signal<ComponentDestination | null>(null);

  protected readonly destinationDescription = computed(() => {
    const dest = this.componentDestination();
    if (!dest) return null;
    if (dest.kind === 'branch') {
      const source = this.nodes().find((node) => node.id === dest.sourceNodeId);
      const sourceName = source?.name || source?.component.name || 'Étape';
      return `Insertion sur la sortie « ${dest.output.label} » de ${sourceName}`;
    }
    return `Insertion sur une suite commune (${dest.sources.length} branche${dest.sources.length > 1 ? 's' : ''})`;
  });

  private nextNodeId = 1;
  private nextConnectionId = 1;

  protected readonly validationFlowId =
    this.flowId && /^[0-9a-f]{8}-[0-9a-f-]{27}$/i.test(this.flowId) ? this.flowId : createUuid();

  private readonly savedGraph = signal<string | null>(null);
  private readonly savedDocumentKey = signal<string | null>(null);
  private readonly draftRevision = signal<number | undefined>(undefined);
  protected readonly storedVersion = signal(0);
  protected readonly saving = signal(false);
  protected readonly loadState = signal<'ready' | 'loading' | 'error'>('ready');
  protected readonly backupStatus = signal<string | null>(null);
  protected readonly validating = signal(false);

  protected readonly hasDraft = computed(() => this.draftRevision() !== undefined);

  protected readonly editorStatus = computed(() =>
    this.loadState() === 'loading'
      ? 'Chargement du flow…'
      : this.loadState() === 'error'
        ? 'Chargement impossible'
        : this.hasUnsavedChanges()
          ? 'Modifications non enregistrées'
          : this.draftRevision() !== undefined
            ? 'Brouillon enregistré'
            : 'Enregistré',
  );

  protected readonly packCreationForm = computed<RecordFormData>(() => ({
    title: 'Créer un pack depuis la version publiée',
    description: `La version publiée v${this.storedVersion()} sera copiée avec ses composants internes et ses ports.`,
    submitLabel: 'Créer le pack',
    initialValues: { name: `${this.flowTitle()} — Pack`, description: '' },
    fields: [
      { id: 'name', label: 'Nom du pack', type: 'text', required: true, maxLength: 128 },
      { id: 'description', label: 'Description', type: 'text', maxLength: 1000 },
    ],
  }));

  private readonly editHistory = signal<
    readonly {
      nodes: readonly FlowGraphNode[];
      connections: readonly FlowGraphConnection[];
      packs: readonly FlowComponentPackInstance[];
      name: string | null;
      mediaType: MediaType;
    }[]
  >([]);
  private readonly editHistoryIndex = signal(-1);
  protected readonly canUndoEdit = computed(() => this.editHistoryIndex() > 0);
  protected readonly canRedoEdit = computed(
    () => this.editHistoryIndex() + 1 < this.editHistory().length,
  );
  protected undoEdit(redo = false): void {
    const index = this.editHistoryIndex() + (redo ? 1 : -1),
      state = this.editHistory()[index];
    if (!state) return;
    this.editHistoryIndex.set(index);
    this.nodes.set(state.nodes);
    this.connections.set(state.connections);
    this.componentPacks.set(state.packs);
    this.savedName.set(state.name);
    this.flowMediaType.set(state.mediaType);
    this.aiProposalRollback.set(null);
    if (this.configuringNodeId() && !state.nodes.some((n) => n.id === this.configuringNodeId()))
      this.configuringNodeId.set(undefined);
  }
  constructor() {
    effect(() => {
      const ready = this.loadState() === 'ready',
        state = {
          nodes: this.nodes(),
          connections: this.connections(),
          packs: this.componentPacks(),
          name: this.savedName(),
          mediaType: this.flowMediaType(),
        };
      untracked(() => {
        this.simulator.invalidate();
        if (!ready) {
          this.editHistory.set([]);
          this.editHistoryIndex.set(-1);
          return;
        }
        const history = this.editHistory(),
          index = this.editHistoryIndex();
        if (JSON.stringify(history[index]) === JSON.stringify(state)) return;
        const next = [...history.slice(0, index + 1), state].slice(-60);
        this.editHistory.set(next);
        this.editHistoryIndex.set(next.length - 1);
      });
    });
    this.flowAssistant.register(this, (nodeId) => this.prepareAiSession(nodeId));
    inject(DestroyRef).onDestroy(() => this.flowAssistant.unregister(this));
    void this.packApi.list().subscribe({
      next: (packs) => this.packCatalog.set(packs),
      error: () => this.packCatalog.set([]),
    });

    const requestedMediaType = this.route.snapshot.queryParamMap.get('mediaType');
    const templateId = this.route.snapshot.queryParamMap.get('templateId');
    if (templateId) this.loadState.set('loading');
    const mediaType =
      MEDIA_TYPE_OPTIONS.find((option) => option.value === requestedMediaType)?.value ?? 'video';
    this.flowMediaType.set(mediaType);
    const entry: FlowGraphNode = {
      id: 'flow-input',
      component: MEDIA_INPUT_COMPONENTS[mediaType],
      position: { x: 64, y: 48 },
      configuration: {},
    };
    this.setGraph([entry], []);
    void this.serverValidation
      .catalog()
      .then(async (plugins) => {
        const installed = plugins
          .filter((plugin) => plugin.enabled !== false)
          .flatMap((plugin) =>
            plugin.components.map((component) => ({
              ...component,
              pluginId: plugin.id,
              pluginName: plugin.name,
            })),
          );
        this.installedComponents.set(installed);
        if (templateId && !this.flowId) {
          await this.loadTemplate(templateId, plugins);
          return;
        }
      })
      .catch(() =>
        this.notifications.show(
          'Catalogue des composants indisponible. Aucun nouveau bloc ne peut être ajouté.',
        ),
      );
    if (this.flowId && /^[0-9a-f]{8}-[0-9a-f-]{27}$/i.test(this.flowId)) {
      this.loadState.set('loading');
      void this.loadSavedFlow(this.flowId);
    }
    toObservable(computed(() => ({ version: this.storedVersion(), draft: this.draftRevision() })))
      .pipe(
        switchMap(({ version, draft }) =>
          version > 0 || draft !== undefined
            ? timer(0, 5000).pipe(
                switchMap(() =>
                  (draft !== undefined
                    ? from(this.serverValidation.get(this.validationFlowId))
                    : this.serverValidation.versionStatus(this.validationFlowId, version)
                  ).pipe(catchError(() => of(null))),
                ),
              )
            : of(null),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((response) => {
        if (response && (response.draftRevision ?? undefined) === this.draftRevision())
          this.backupStatus.set(response.backupStatus);
      });
  }

  hasUnsavedChanges(): boolean {
    return this.loadState() === 'ready' && this.savedGraph() !== this.graphSnapshot();
  }

  private graphSnapshot(): string {
    return JSON.stringify([this.nodes(), this.connections(), this.componentPacks()]);
  }

  protected warnBeforeUnload(event: BeforeUnloadEvent): void {
    if (this.hasUnsavedChanges()) {
      event.preventDefault();
      event.returnValue = '';
    }
  }

  protected onWindowKeyDown(event: KeyboardEvent): void {
    if (
      (event.ctrlKey || event.metaKey) &&
      event.key.toLowerCase() === 'z' &&
      !(
        event.target instanceof Element &&
        event.target.closest('input,textarea,select,[contenteditable="true"],.cm-editor')
      )
    ) {
      event.preventDefault();
      this.undoEdit(event.shiftKey);
      return;
    }
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
      event.preventDefault();
      void this.saveFlow();
    } else if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'b') {
      event.preventDefault();
      this.togglePalette();
    } else if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
      event.preventDefault();
      void this.openSimulator();
    } else if (event.key === 'Escape') {
      if (this.packCreationOpen()) {
        this.packCreationOpen.set(false);
      } else if (this.configuringNodeId()) {
        this.closeComponentEditor();
      } else if (this.componentDestination()) {
        this.componentDestination.set(null);
      }
    }
  }

  protected toggleFlowConnections(): void {
    this.connectionVisibilityOverride.set(!this.showFlowConnections());
  }

  protected setFlowConnections(visible: boolean): void {
    this.connectionVisibilityOverride.set(visible);
  }

  protected onTitleChange(newTitle: string): void {
    this.savedName.set(newTitle);
  }

  protected cancelInsertion(): void {
    this.componentDestination.set(null);
  }

  protected openPalette(): void {
    this.configuringNodeId.set(undefined);
    this.paletteOpen.set(true);
  }
  protected togglePalette(): void {
    if (this.paletteOpen()) this.paletteOpen.set(false);
    else this.openPalette();
  }

  protected closeComponentEditor(): void {
    this.configuringNodeId.set(undefined);
  }

  protected deleteConfiguredNode(nodeId: string): void {
    this.removeNode(nodeId);
    this.configuringNodeId.set(undefined);
  }

  protected onPaletteComponentDropped(event: FlowPaletteDrop): void {
    if (this.loadState() !== 'ready') return;
    if (event.sourceNodeId && event.output) {
      this.componentDestination.set({
        kind: 'branch',
        sourceNodeId: event.sourceNodeId,
        output: event.output,
      });
      this.addComponent(event.component);
    } else {
      const previousCount = this.nodes().length;
      this.addComponent(event.component, event.position ?? { x: 0, y: 0 });
      if (this.nodes().length > previousCount && !this.palettePinned()) this.paletteOpen.set(false);
    }
  }

  protected requestAutoLayout(): void {
    this.autoLayoutRequest.update((request) => request + 1);
  }

  protected async onPaletteComponentSelected(component: FlowComponentDefinition): Promise<void> {
    const previous = this.nodes().length;
    this.addComponent(component, this.componentDestination() ? undefined : { x: 0, y: 0 });
    if (this.nodes().length > previous && !this.palettePinned()) this.paletteOpen.set(false);
  }

  protected async onPalettePackSelected(summary: FlowComponentPackSummary): Promise<void> {
    try {
      const pack = await this.packApi.get(summary.id);
      const previous = this.nodes().length;
      await this.insertPack(pack);
      if (this.nodes().length > previous && !this.palettePinned()) this.paletteOpen.set(false);
    } catch (error) {
      this.notifications.show(
        error instanceof HttpErrorResponse
          ? (error.error?.title ?? 'Chargement du pack impossible.')
          : 'Chargement du pack impossible.',
      );
    }
  }

  protected async openSimulator(configure = false): Promise<void> {
    const nodes = this.nodes(),
      connections = this.connections(),
      packs = this.componentPacks();
    try {
      const definition = await this.serverValidation.document(
        this.validationFlowId,
        this.storedVersion() + 1,
        this.flowTitle(),
        this.flowMediaType(),
        nodes,
        connections,
        packs,
      );
      if (
        nodes !== this.nodes() ||
        connections !== this.connections() ||
        packs !== this.componentPacks()
      ) {
        this.notifications.show('Le flow a changé pendant la préparation. Relancez le test.');
        return;
      }
      this.simulator.prepare(definition);
      this.trayTab.set('simulation');
      this.trayOpen.set(configure);
      if (!configure) await this.runSimulation(this.simulator.profile());
    } catch {
      this.notifications.show('Impossible de préparer le test du flow.');
    }
  }

  protected async runSimulation(values: FlowComponentConfiguration, resume = false): Promise<void> {
    if (resume) await this.simulator.continue(values);
    else await this.simulator.run(values);
    this.trayOpen.set(this.simulator.result()?.status !== 'completed' || !!this.simulator.error());
  }

  protected openPackCreation(): void {
    this.packCreationOpen.set(true);
  }

  protected onTerminationRequested(event: {
    readonly sourceNodeId: string;
    readonly output: FlowComponentOutput;
  }): void {
    const component = this.components().find((component) => component.id === 'base-complete-flow');
    if (!component) return;
    this.componentDestination.set({ kind: 'branch', ...event });
    this.addComponent(component);
  }

  protected onBranchComponentRequested(event: {
    readonly sourceNodeId: string;
    readonly output: FlowComponentOutput;
  }): void {
    this.componentDestination.set({ kind: 'branch', ...event });
    this.openPalette();
  }

  protected onContinuationRequested(sources: readonly FlowContinuationSource[]): void {
    if (!sources.length) return;
    this.componentDestination.set({ kind: 'continuation', sources });
    this.paletteOpen.set(true);
  }

  protected async createPackFromPublishedFlow(values: Record<string, string>): Promise<void> {
    if (this.storedVersion() < 1 || this.saving()) return;
    try {
      await this.packApi.createFromFlow({
        sourceFlowId: this.validationFlowId,
        sourceVersion: this.storedVersion(),
        name: values['name'],
        description: values['description'] ?? '',
      });
      this.packCatalog.set(await firstValueFrom(this.packApi.list()));
      this.packCreationOpen.set(false);
      this.notifications.show('Pack créé depuis la version publiée du flow.');
    } catch (error) {
      this.notifications.show(
        error instanceof HttpErrorResponse
          ? (error.error?.title ?? 'Création du pack impossible.')
          : 'Création du pack impossible.',
      );
    }
  }

  private async insertPack(pack: FlowComponentPackDetails): Promise<void> {
    const destination: ComponentDestination = this.componentDestination() ?? {
      kind: 'continuation',
      sources: [],
    };
    if (pack.pack.mediaType !== this.flowMediaType()) {
      this.notifications.show('Ce pack n’est pas compatible avec le type de média du flow.');
      return;
    }
    const plugins = await this.serverValidation.catalog();
    const sourceNodes = this.mapDefinitionNodes(pack.definition, plugins).filter(
      (node) => node.id !== findEntryId(pack.definition),
    );
    const clonedIds = new Map(
      sourceNodes.map((node) => [node.id, `flow-node-${this.nextNodeId++}`]),
    );
    const clonedNodes = sourceNodes.map((node) => ({ ...node, id: clonedIds.get(node.id)! }));
    const inputNodeId = clonedIds.get(pack.inputNodeId);
    if (!inputNodeId) {
      this.notifications.show('L’entrée du pack ne correspond plus à un composant disponible.');
      return;
    }
    const internalConnections = pack.definition.connections
      .filter((edge) => clonedIds.has(edge.sourceNodeId) && clonedIds.has(edge.targetNodeId))
      .map((edge) =>
        this.createConnection(
          clonedIds.get(edge.sourceNodeId)!,
          edge.sourceOutputId,
          clonedIds.get(edge.targetNodeId)!,
        ),
      );
    const outputs = pack.outputs.flatMap((port) => {
      const sourceNodeId = clonedIds.get(port.sourceNodeId);
      return sourceNodeId ? [{ ...port, sourceNodeId }] : [];
    });
    const instance: FlowComponentPackInstance = {
      id: createUuid(),
      packId: pack.pack.id,
      packVersion: 1,
      name: pack.pack.name,
      inputNodeId,
      nodeIds: clonedNodes.map((node) => node.id),
      outputs,
    };
    const existing =
      destination.kind === 'branch'
        ? this.connections().find(
            (edge) =>
              edge.sourceNodeId === destination.sourceNodeId &&
              edge.sourceOutputId === destination.output.id,
          )
        : undefined;
    const remaining = this.connections().filter((edge) => edge.id !== existing?.id);
    const nodes = [...this.nodes(), ...clonedNodes];
    const next = [...remaining, ...internalConnections];
    if (destination.kind === 'branch') {
      if (
        !canConnectFlowGraph(nodes, next, {
          sourceNodeId: destination.sourceNodeId,
          sourceOutputId: destination.output.id,
          targetNodeId: inputNodeId,
        })
      ) {
        this.notifications.show('Cette sortie n’est plus disponible pour insérer le pack.');
        return;
      }
      next.push(
        this.createConnection(destination.sourceNodeId, destination.output.id, inputNodeId),
      );
      if (existing) {
        const continuations = outputs.filter((port) => port.sourceOutputId === 'success');
        if (!continuations.length) {
          this.notifications.show(
            'Ce pack n’expose pas de sortie « Suite » pour conserver la suite existante.',
          );
          return;
        }
        for (const continuation of continuations)
          next.push(
            this.createConnection(
              continuation.sourceNodeId,
              continuation.sourceOutputId,
              existing.targetNodeId,
            ),
          );
      }
    } else {
      for (const source of destination.sources) {
        if (
          !canConnectFlowGraph(nodes, next, {
            sourceNodeId: source.sourceNodeId,
            sourceOutputId: source.output.id,
            targetNodeId: inputNodeId,
          })
        ) {
          this.notifications.show('Une branche a changé. Choisissez à nouveau où ajouter le pack.');
          return;
        }
        next.push(this.createConnection(source.sourceNodeId, source.output.id, inputNodeId));
      }
    }
    this.componentPacks.update((instances) => [...instances, instance]);
    this.setGraph(nodes, next);
    this.componentDestination.set(null);
  }

  protected addComponent(
    component: FlowComponentDefinition,
    position?: { x: number; y: number },
  ): void {
    if (component.kind === 'input') return;
    if (!isFlowComponentCompatible(component, this.flowMediaType())) {
      this.notifications.show('Ce bloc n’est pas compatible avec le type de média du flow.');
      return;
    }
    const destination = this.componentDestination();
    if (!destination && !position) return;
    const node: FlowGraphNode = {
      id: `flow-node-${this.nextNodeId++}`,
      component,
      position: { x: 0, y: 0 },
      configuration: createDefaultFlowComponentConfiguration(
        getFlowConfigurationSchema(component.configurationSchema, this.flowMediaType()),
      ),
    };
    if (position) {
      this.setGraph([...this.nodes(), node], this.connections());
      this.graphEditor()?.setCanvasPosition({ nodeId: node.id, position });
      this.componentDestination.set(null);
      return;
    }
    if (!destination) return;
    const packOwner =
      destination.kind === 'branch'
        ? this.componentPacks().find((pack) => pack.nodeIds.includes(destination.sourceNodeId))
        : undefined;
    const nodes = [...this.nodes(), node];
    if (destination.kind === 'continuation') {
      let next = [...this.connections()];
      for (const source of destination.sources) {
        if (
          !canConnectFlowGraph(nodes, next, {
            sourceNodeId: source.sourceNodeId,
            sourceOutputId: source.output.id,
            targetNodeId: node.id,
          })
        ) {
          this.notifications.show(
            'Une branche a changé. Choisissez à nouveau où ajouter la suite.',
          );
          return;
        }
        next.push(this.createConnection(source.sourceNodeId, source.output.id, node.id));
      }
      this.setGraph(nodes, next);
      const continuationPack = destination.sources.length
        ? this.componentPacks().find((pack) =>
            destination.sources.every((source) => pack.nodeIds.includes(source.sourceNodeId)),
          )
        : undefined;
      if (continuationPack)
        this.componentPacks.update((packs) =>
          packs.map((pack) =>
            pack.id === continuationPack.id
              ? {
                  ...pack,
                  nodeIds: [...pack.nodeIds, node.id],
                  outputs: [
                    ...pack.outputs.filter(
                      (port) =>
                        !destination.sources.some(
                          (source) =>
                            port.sourceNodeId === source.sourceNodeId &&
                            port.sourceOutputId === source.output.id,
                        ),
                    ),
                    ...node.component.outputs.map((output) => ({
                      id: `${node.id}:${output.id}`,
                      label: output.label,
                      sourceNodeId: node.id,
                      sourceOutputId: output.id,
                    })),
                  ],
                }
              : pack,
          ),
        );
      this.componentDestination.set(null);
      return;
    }
    const existing =
      destination.kind === 'branch'
        ? this.connections().find(
            (edge) =>
              edge.sourceNodeId === destination.sourceNodeId &&
              edge.sourceOutputId === destination.output.id,
          )
        : undefined;
    const remaining = this.connections().filter((edge) => edge.id !== existing?.id);
    if (
      destination.kind === 'branch' &&
      !canConnectFlowGraph(nodes, remaining, {
        sourceNodeId: destination.sourceNodeId,
        sourceOutputId: destination.output.id,
        targetNodeId: node.id,
      })
    ) {
      this.notifications.show('Cette sortie n’est plus disponible.');
      return;
    }
    const continuation =
      component.outputs.find((output) => output.id === 'success') ?? component.outputs[0];
    if (existing && !continuation) {
      this.notifications.show(
        'Une étape terminale ne peut pas être insérée avant une suite. Ajoutez-la à la fin d’un chemin.',
      );
      return;
    }
    const connections =
      destination.kind === 'branch'
        ? [
            ...remaining,
            this.createConnection(destination.sourceNodeId, destination.output.id, node.id),
            ...(existing && continuation
              ? [this.createConnection(node.id, continuation.id, existing.targetNodeId)]
              : []),
          ]
        : remaining;
    this.setGraph(nodes, connections);
    if (packOwner)
      this.componentPacks.update((packs) =>
        packs.map((pack) =>
          pack.id === packOwner.id
            ? {
                ...pack,
                nodeIds: [...pack.nodeIds, node.id],
                outputs: (() => {
                  const crossesBoundary =
                    !existing || !pack.nodeIds.includes(existing.targetNodeId);
                  const replaced = pack.outputs.some(
                    (port) =>
                      port.sourceNodeId === destination.sourceNodeId &&
                      port.sourceOutputId === destination.output.id,
                  );
                  return crossesBoundary
                    ? [
                        ...pack.outputs.filter(
                          (port) =>
                            !(
                              port.sourceNodeId === destination.sourceNodeId &&
                              port.sourceOutputId === destination.output.id
                            ),
                        ),
                        ...(replaced || !existing
                          ? continuation
                            ? [
                                {
                                  id: `${node.id}:${continuation.id}`,
                                  label: continuation.label,
                                  sourceNodeId: node.id,
                                  sourceOutputId: continuation.id,
                                },
                              ]
                            : []
                          : []),
                      ]
                    : pack.outputs;
                })(),
              }
            : pack,
        ),
      );
    this.componentDestination.set(null);
  }

  protected onConfigurationRequested(nodeId: string): void {
    this.paletteOpen.set(false);
    this.configuringNodeId.set(nodeId);
  }

  protected renameNode(name: string): void {
    const nodeId = this.configuringNodeId();
    this.nodes.update((nodes) =>
      nodes.map((node) => (node.id === nodeId ? { ...node, name: name || undefined } : node)),
    );
  }

  private async loadSavedFlow(id: string): Promise<void> {
    try {
      const [saved, plugins] = await Promise.all([
        this.serverValidation.get(id),
        this.serverValidation.catalog(),
      ]);
      const definition = saved.definition;
      this.savedName.set(definition.name);
      this.flowMediaType.set(definition.mediaType);
      const nodes = this.mapDefinitionNodes(definition, plugins);
      this.updateGraphCounters(definition, nodes);
      this.setGraph(nodes, definition.connections);
      this.componentPacks.set(definition.componentPacks ?? []);
      this.savedGraph.set(this.graphSnapshot());
      this.savedDocumentKey.set(flowDocumentKey(definition));
      this.storedVersion.set(flowPublishedVersion(saved));
      this.draftRevision.set(saved.draftRevision ?? undefined);
      this.backupStatus.set(saved.backupStatus);
      this.loadState.set('ready');
    } catch {
      this.loadState.set('error');
      this.notifications.show('Impossible de charger le flow enregistré.');
    }
  }

  private async loadTemplate(
    templateId: string,
    plugins: readonly FlowPluginCatalog[],
  ): Promise<void> {
    try {
      const template = await this.templates.get(templateId);
      const name =
        this.route.snapshot.queryParamMap.get('name') ?? `${template.template.name} — Copie`;
      const definition = {
        ...template.definition,
        id: this.validationFlowId,
        version: 1,
        name,
      };
      this.flowMediaType.set(definition.mediaType);
      this.savedName.set(name);
      const nodes = this.mapDefinitionNodes(definition, plugins);
      this.updateGraphCounters(definition, nodes);
      this.setGraph(nodes, definition.connections);
      this.componentPacks.set(definition.componentPacks ?? []);
      this.savedGraph.set(null);
      this.savedDocumentKey.set(null);
      this.storedVersion.set(0);
      this.draftRevision.set(undefined);
      this.backupStatus.set(null);
      this.loadState.set('ready');
    } catch {
      this.loadState.set('error');
      this.notifications.show('Impossible de charger ce modèle de flow.');
    }
  }

  private mapDefinitionNodes(
    definition: FlowDocument,
    plugins: readonly FlowPluginCatalog[],
  ): FlowGraphNode[] {
    return flowDocumentNodes(definition, plugins);
  }

  private readonly flowAssistant = inject(FlowEditorAssistant);
  protected async openAiAssistant(nodeId?: string): Promise<void> {
    await this.flowAssistant.open(nodeId);
  }
  private async prepareAiSession(nodeId?: string) {
    const currentDoc = await this.currentFlowDocument();
    if (!currentDoc) {
      this.notifications.show(
        'Impossible de préparer le contexte du flow. Réessayez après le chargement du catalogue.',
      );
      return null;
    }
    const node = this.nodes().find((n) => n.id === nodeId);
    return {
      flow: currentDoc,
      components: this.installedComponents(),
      label: node
        ? 'Étape sélectionnée : ' + (node.name || node.component.name)
        : 'Flow complet : ' + this.flowTitle(),
      prompt: node
        ? 'Explique ou améliore l’étape ' +
          (node.name || node.component.name) +
          ' (identifiant ' +
          node.id +
          '). Conserve le reste du flow sauf nécessité explicitement expliquée.'
        : '',
      apply: async (proposal: FlowDocument) => {
        const latest = await this.currentFlowDocument();
        if (!latest || flowDocumentKey(latest) !== flowDocumentKey(currentDoc)) {
          this.notifications.show(
            'Le flow a changé depuis la proposition. Relancez l’assistant pour conserver vos modifications.',
          );
          return false;
        }
        return this.loadAiFlow(proposal, currentDoc);
      },
    };
  }

  private async currentFlowDocument(): Promise<FlowDocument | null> {
    try {
      return await this.serverValidation.document(
        this.validationFlowId,
        this.storedVersion() + 1,
        this.flowTitle(),
        this.flowMediaType(),
        this.nodes(),
        this.connections(),
        this.componentPacks(),
      );
    } catch {
      return null;
    }
  }

  protected async restoreAiProposal(): Promise<void> {
    const previous = this.aiProposalRollback();
    if (!previous) return;
    try {
      const plugins = await this.serverValidation.catalog();
      this.savedName.set(previous.name);
      this.flowMediaType.set(previous.mediaType);
      const nodes = this.mapDefinitionNodes(previous, plugins);
      this.updateGraphCounters(previous, nodes);
      this.setGraph(nodes, previous.connections);
      this.componentPacks.set(previous.componentPacks ?? []);
      this.aiProposalRollback.set(null);
      this.notifications.show('État précédent du flow restauré.');
    } catch {
      this.notifications.show('Impossible de restaurer l’état précédent du flow.');
    }
  }

  private async loadAiFlow(
    definition: FlowDocument,
    rollback: FlowDocument | null,
  ): Promise<boolean> {
    const snapshot = this.graphSnapshot();
    try {
      const plugins = await this.serverValidation.catalog();
      if (snapshot !== this.graphSnapshot()) {
        this.notifications.show(
          'Le flow a changé pendant la préparation. La proposition n’a pas été appliquée.',
        );
        return false;
      }
      this.savedName.set(definition.name);
      this.flowMediaType.set(definition.mediaType);
      const nodes = this.mapDefinitionNodes(definition, plugins);
      this.updateGraphCounters(definition, nodes);
      this.setGraph(nodes, definition.connections);
      this.componentPacks.set(definition.componentPacks ?? []);
      this.aiProposalRollback.set(rollback);
      this.notifications.show(
        'Proposition IA appliquée aux deux vues. Enregistrez pour la publier.',
      );
      return true;
    } catch {
      this.notifications.show("Erreur lors de l'application du flow généré.");
      return false;
    }
  }

  private updateGraphCounters(definition: FlowDocument, nodes: readonly FlowGraphNode[]): void {
    this.nextNodeId =
      Math.max(0, ...nodes.map((node) => Number(node.id.match(/^flow-node-(\d+)$/)?.[1]) || 0)) + 1;
    this.nextConnectionId =
      Math.max(
        0,
        ...definition.connections.map(
          (edge) => Number(edge.id.match(/^connection-(\d+)$/)?.[1]) || 0,
        ),
      ) + 1;
  }

  protected async saveFlow(): Promise<void> {
    if (
      this.saving() ||
      this.loadState() !== 'ready' ||
      (!this.hasUnsavedChanges() && !this.hasDraft())
    )
      return;
    this.saving.set(true);
    const nodes = this.nodes(),
      connections = this.connections(),
      packs = this.componentPacks(),
      expected = this.storedVersion();
    const graphSnapshot = JSON.stringify([nodes, connections, packs]);
    try {
      const document = await this.serverValidation.document(
        this.validationFlowId,
        expected + 1,
        this.flowTitle(),
        this.flowMediaType(),
        nodes,
        connections,
        packs,
      );
      const unchanged = flowDocumentKey(document) === this.savedDocumentKey();
      if (unchanged && !this.hasDraft()) {
        this.savedGraph.set(graphSnapshot);
        this.notifications.show('Aucune modification à enregistrer.');
        return;
      }
      const result = await this.serverValidation.saveAutomatically(
        document,
        expected,
        this.draftRevision(),
        unchanged,
      );
      if ('noChanges' in result && result.noChanges) {
        this.notifications.show('Aucune modification à publier.');
        return;
      }
      const { saved, published, validation } = result;
      if (!saved) {
        this.notifications.show(
          'Le brouillon est déjà enregistré. Publication impossible : ' +
            validation.issues.map((issue) => issue.message).join(' '),
        );
        return;
      }
      this.savedGraph.set(graphSnapshot);
      this.savedDocumentKey.set(flowDocumentKey(saved.definition));
      this.storedVersion.set(flowPublishedVersion(saved));
      this.draftRevision.set(saved.draftRevision ?? undefined);
      this.backupStatus.set(saved.backupStatus);
      const changed =
        this.nodes() !== nodes ||
        this.connections() !== connections ||
        this.componentPacks() !== packs;
      if (!changed && this.flowId !== saved.definition.id)
        void this.editorRouter.navigate(['/flows', saved.definition.id, 'edit'], {
          replaceUrl: true,
        });
      this.notifications.show(
        !published
          ? 'Brouillon enregistré. Publication impossible : ' +
              validation.issues.map((issue) => issue.message).join(' ')
          : 'Version ' +
              saved.definition.version +
              ' publiée. Sauvegarde JSON : ' +
              (saved.backupStatus === 'ready' ? 'disponible.' : 'en attente.') +
              (changed ? ' Des modifications plus récentes restent à enregistrer.' : ''),
      );
    } catch (error) {
      const message =
        error instanceof HttpErrorResponse && error.status === 409
          ? 'Conflit de version : rechargez le flow avant de réessayer.'
          : error instanceof HttpErrorResponse && error.status === 422
            ? error.error.issues.map((issue: { message: string }) => issue.message).join(' ')
            : 'Enregistrement non confirmé. Votre flow reste dans l’éditeur.';
      this.notifications.show(message);
    } finally {
      this.saving.set(false);
    }
  }

  protected async validateGraph(): Promise<void> {
    if (this.validating()) return;
    this.validating.set(true);
    const checkedNodes = this.nodes();
    const checkedConnections = this.connections();
    const checkedPacks = this.componentPacks();
    try {
      const result = await this.serverValidation.validate(
        this.validationFlowId,
        this.flowTitle(),
        this.flowMediaType(),
        checkedNodes,
        checkedConnections,
        checkedPacks,
      );
      if (
        this.nodes() !== checkedNodes ||
        this.connections() !== checkedConnections ||
        this.componentPacks() !== checkedPacks
      ) {
        this.notifications.show(
          'Le flow a été modifié pendant la vérification. Vérifiez sa nouvelle version.',
        );
        return;
      }
      this.validationIssues.set(result.issues ?? []);
      this.validationSuccess.set(result.isValid);
      this.trayTab.set('diagnostics');
      this.trayOpen.set(true);
      this.notifications.show(
        result.isValid
          ? 'Le backend confirme la validité du flow.'
          : result.issues.map((issue) => issue.path + ' : ' + issue.message).join(' '),
      );
    } catch {
      this.notifications.show(
        'Validation backend indisponible. La validité du flow n’a pas été confirmée.',
      );
    } finally {
      this.validating.set(false);
    }
  }

  protected saveNodeConfiguration(configuration: FlowComponentConfiguration): void {
    const nodeId = this.configuringNodeId();
    if (!nodeId) return;
    if (this.configuringNode()?.component.kind === 'input') {
      const inputComponent = getFlowInputComponents(this.flowMediaType()).find(
        (component) => component.id === configuration['inputComponentId'],
      );
      if (!inputComponent) {
        this.notifications.show('Choisissez une entrée compatible avec le type de flow.');
        return;
      }
      this.nodes.update((nodes) =>
        nodes.map((node) =>
          node.id === nodeId
            ? { ...node, reference: undefined, component: inputComponent, configuration: {} }
            : node,
        ),
      );
      this.closeComponentEditor();
      return;
    }
    this.nodes.update((nodes) =>
      nodes.map((node) =>
        node.id === nodeId
          ? { ...node, configuration: { ...node.configuration, ...configuration } }
          : node,
      ),
    );
    this.closeComponentEditor();
  }

  protected onConnectionRequested(event: {
    readonly sourceNodeId: string;
    readonly sourceOutputId: string;
    readonly targetNodeId: string;
  }): void {
    const sourcePack = this.componentPacks().find((pack) =>
      pack.nodeIds.includes(event.sourceNodeId),
    );
    const targetPack = this.componentPacks().find((pack) =>
      pack.nodeIds.includes(event.targetNodeId),
    );
    const samePack = sourcePack?.id === targetPack?.id;
    if (
      (sourcePack &&
        !samePack &&
        !sourcePack.outputs.some(
          (port) =>
            port.sourceNodeId === event.sourceNodeId &&
            port.sourceOutputId === event.sourceOutputId,
        )) ||
      (targetPack && !samePack && event.targetNodeId !== targetPack.inputNodeId)
    ) {
      this.notifications.show('Cette liaison traverse le pack sans utiliser ses ports déclarés.');
      return;
    }
    if (!canConnectFlowGraph(this.nodes(), this.connections(), event)) {
      this.notifications.show(
        'Connexion impossible : vérifiez la sortie, le point d’entrée et l’absence de cycle.',
      );
      return;
    }
    const connections = [
      ...this.connections(),
      this.createConnection(event.sourceNodeId, event.sourceOutputId, event.targetNodeId),
    ];
    this.setGraph([...this.nodes()], connections);
    this.synchronizePackOutputs(connections);
  }

  protected onConnectionsChanged(connections: readonly FlowGraphConnection[]): void {
    const nodes = this.nodes();
    if (
      connections.some(
        (edge, index) =>
          !canConnectFlowGraph(
            nodes,
            connections.filter((_, other) => other !== index),
            edge,
          ),
      )
    ) {
      this.notifications.show('Cette réorganisation produirait une connexion invalide.');
      return;
    }
    if (
      connections.some((edge) => {
        const sourcePack = this.componentPacks().find((pack) =>
          pack.nodeIds.includes(edge.sourceNodeId),
        );
        const targetPack = this.componentPacks().find((pack) =>
          pack.nodeIds.includes(edge.targetNodeId),
        );
        const samePack = sourcePack?.id === targetPack?.id;
        return (
          (sourcePack &&
            !samePack &&
            !sourcePack.outputs.some(
              (port) =>
                port.sourceNodeId === edge.sourceNodeId &&
                port.sourceOutputId === edge.sourceOutputId,
            )) ||
          (targetPack && !samePack && edge.targetNodeId !== targetPack.inputNodeId)
        );
      })
    ) {
      this.notifications.show(
        'Cette réorganisation relierait un bloc en dehors des ports du pack.',
      );
      return;
    }
    this.setGraph(nodes, connections);
    this.synchronizePackOutputs(connections);
  }

  private synchronizePackOutputs(connections: readonly FlowGraphConnection[]): void {
    this.componentPacks.update((packs) =>
      packs.map((pack) => ({
        ...pack,
        outputs: pack.outputs.filter(
          (port) =>
            !connections.some(
              (edge) =>
                edge.sourceNodeId === port.sourceNodeId &&
                edge.sourceOutputId === port.sourceOutputId &&
                pack.nodeIds.includes(edge.targetNodeId),
            ),
        ),
      })),
    );
  }

  protected duplicateNode(nodeId: string): void {
    const original = this.nodes().find((n) => n.id === nodeId);
    if (
      !original ||
      original.component.kind === 'input' ||
      original.component.kind === 'pack' ||
      this.componentPacks().some((p) => p.nodeIds.includes(nodeId))
    ) {
      this.notifications.show(
        'Cette étape appartient à une entrée ou à un pack et ne peut pas être dupliquée seule.',
      );
      return;
    }
    const node = {
      ...original,
      id: 'flow-node-' + this.nextNodeId++,
      name: (original.name || original.component.name) + ' — Copie',
      configuration: structuredClone(original.configuration),
    };
    this.setGraph([...this.nodes(), node], this.connections());
    this.notifications.show('Copie ajoutée parmi les blocs non reliés, dans les deux vues.');
  }
  protected removeNode(nodeId: string): void {
    const visualPack = this.componentPacks().find((pack) => `flow-pack-${pack.id}` === nodeId);
    if (visualPack) {
      const removed = new Set(visualPack.nodeIds);
      this.setGraph(
        this.nodes().filter((node) => !removed.has(node.id)),
        this.connections().filter(
          (edge) => !removed.has(edge.sourceNodeId) && !removed.has(edge.targetNodeId),
        ),
      );
      this.componentPacks.update((packs) => packs.filter((pack) => pack.id !== visualPack.id));
      return;
    }
    const packOwner = this.componentPacks().find((pack) => pack.nodeIds.includes(nodeId));
    if (packOwner?.inputNodeId === nodeId) {
      this.notifications.show(
        'L’entrée du pack est son point d’ancrage ; supprimez le pack entier pour la retirer.',
      );
      return;
    }
    if (this.nodes().find((node) => node.id === nodeId)?.component.kind === 'input') return;
    const incoming = this.connections().filter((edge) => edge.targetNodeId === nodeId);
    const outgoing = this.connections().filter((edge) => edge.sourceNodeId === nodeId);
    const restoredPackOutputs =
      packOwner && outgoing.length === 1 && !packOwner.nodeIds.includes(outgoing[0].targetNodeId)
        ? incoming.flatMap((edge) => {
            const source = this.nodes().find((candidate) => candidate.id === edge.sourceNodeId);
            const output = source?.component.outputs.find(
              (candidate) => candidate.id === edge.sourceOutputId,
            );
            return source && output
              ? [
                  {
                    id: `${source.id}:${output.id}`,
                    label: output.label,
                    sourceNodeId: source.id,
                    sourceOutputId: output.id,
                  },
                ]
              : [];
          })
        : [];
    let remaining = this.connections().filter(
      (edge) => edge.sourceNodeId !== nodeId && edge.targetNodeId !== nodeId,
    );
    if (outgoing.length === 1)
      remaining = [
        ...remaining,
        ...incoming.map((edge) => ({ ...edge, targetNodeId: outgoing[0].targetNodeId })),
      ];
    this.setGraph(
      this.nodes().filter((node) => node.id !== nodeId),
      remaining,
    );
    if (packOwner)
      this.componentPacks.update((packs) =>
        packs
          .map((pack) =>
            pack.id === packOwner.id
              ? {
                  ...pack,
                  nodeIds: pack.nodeIds.filter((id) => id !== nodeId),
                  outputs: pack.outputs
                    .filter((port) => port.sourceNodeId !== nodeId)
                    .concat(restoredPackOutputs),
                }
              : pack,
          )
          .filter((pack) => pack.nodeIds.length > 0),
      );
    if (outgoing.length > 1)
      this.notifications.show(
        'Étape supprimée. Les blocs de ses branches sont conservés parmi les blocs non reliés.',
      );
  }

  protected removeConnection(connectionId: string): void {
    const removed = this.connections().find((connection) => connection.id === connectionId);
    const connections = this.connections().filter((connection) => connection.id !== connectionId);
    if (connections.length !== this.connections().length) {
      this.setGraph([...this.nodes()], connections);
      const owner =
        removed &&
        this.componentPacks().find(
          (pack) =>
            pack.nodeIds.includes(removed.sourceNodeId) &&
            pack.nodeIds.includes(removed.targetNodeId),
        );
      const source = removed && this.nodes().find((node) => node.id === removed.sourceNodeId);
      const output = source?.component.outputs.find(
        (candidate) => candidate.id === removed?.sourceOutputId,
      );
      if (
        owner &&
        removed &&
        source &&
        output &&
        !connections.some(
          (edge) =>
            edge.sourceNodeId === removed.sourceNodeId &&
            edge.sourceOutputId === removed.sourceOutputId,
        )
      ) {
        this.componentPacks.update((packs) =>
          packs.map((pack) =>
            pack.id === owner.id &&
            !pack.outputs.some(
              (port) => port.sourceNodeId === source.id && port.sourceOutputId === output.id,
            )
              ? {
                  ...pack,
                  outputs: [
                    ...pack.outputs,
                    {
                      id: `${source.id}:${output.id}`,
                      label: output.label,
                      sourceNodeId: source.id,
                      sourceOutputId: output.id,
                    },
                  ],
                }
              : pack,
          ),
        );
      }
    }
  }

  private createConnection(
    sourceNodeId: string,
    sourceOutputId: string,
    targetNodeId: string,
  ): FlowGraphConnection {
    return {
      id: `connection-${this.nextConnectionId++}`,
      sourceNodeId,
      sourceOutputId,
      targetNodeId,
    };
  }

  private setGraph(
    nodes: readonly FlowGraphNode[],
    connections: readonly FlowGraphConnection[],
  ): void {
    this.aiProposalRollback.set(null);
    this.nodes.set(nodes);
    this.connections.set(connections);
  }
}

function findEntryId(definition: FlowDocument): string | undefined {
  return definition.nodes.find(
    (node) =>
      node.component.pluginId === 'base' && node.component.componentId.startsWith('base-input-'),
  )?.id;
}
