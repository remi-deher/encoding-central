import { MatButtonModule } from '@angular/material/button';
import { FlowComponentDrop, FlowPaletteDrop } from '../../shared/ui/flow-palette-drag';
import { FlowValidation } from '../../shared/services/flow-validation';
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
import { ActivatedRoute } from '@angular/router';
import { FlowEditorAssistant } from '../../shared/services/flow-editor-assistant.service';
import { MEDIA_INPUT_COMPONENTS } from '../../shared/models/flow-control-components';
import { NotificationService } from '../../shared/services/notification.service';
import { RecordFormComponent } from '../../shared/ui/record-form.component';
import { FlowGraphEditorComponent } from '../../shared/ui/flow-graph-editor.component';
import { ComponentConfigurationFormComponent } from '../../shared/ui/component-configuration-form.component';
import { FlowSimulation } from '../../shared/services/flow-simulation';
import { FlowWorkspaceTemplateComponent } from '../../shared/templates/flow-workspace-template.component';
import { FlowComponentDefinition, FlowComponentOutput } from '../../shared/models/flow-component';
import { DetailDrawerComponent } from '../../shared/ui/detail-drawer/detail-drawer';
import { FlowCommandBarComponent } from './editor/flow-command-bar.component';
import { FlowPaletteDockComponent } from './editor/flow-palette-dock.component';
import {
  FlowSimulationTrayComponent,
} from './editor/flow-simulation-tray.component';
import { FlowGraphStore } from './editor/flow-graph.store';
import { FlowGraphEditingStore } from './editor/flow-graph-editing.store';
import { FlowPackInsertionStore } from './editor/flow-pack-insertion.store';
import { FlowInspectorStore } from './editor/flow-inspector.store';
import { FlowPersistenceStore } from './editor/flow-persistence.store';
import { FlowComponentsStore } from './editor/flow-components.store';

/**
 * Page d’édition d’un flow : canevas, palette, inspecteur, bac de simulation et
 * barre de commandes. L’état du graphe, de l’inspecteur et de la persistance
 * vivent dans les stores du dossier `editor/`.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-flow-editor-page',
  providers: [
    FlowSimulation,
    FlowGraphStore,
    FlowGraphEditingStore,
    FlowPackInsertionStore,
    FlowInspectorStore,
    FlowPersistenceStore,
    FlowComponentsStore,
  ],
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
  private readonly flowAssistant = inject(FlowEditorAssistant);

  readonly graph = inject(FlowGraphStore);
  readonly editing = inject(FlowGraphEditingStore);
  readonly packInsertion = inject(FlowPackInsertionStore);
  readonly inspector = inject(FlowInspectorStore);
  readonly persistence = inject(FlowPersistenceStore);
  readonly components = inject(FlowComponentsStore);

  // Workbench shell state
  protected readonly graphEditor = viewChild(FlowGraphEditorComponent);
  protected readonly paletteOpen = signal(false);
  protected readonly palettePinned = signal(false);
  protected readonly trayOpen = signal(false);
  protected readonly autoLayoutRequest = signal(0);
  protected readonly trayTab = signal<'simulation' | 'diagnostics'>('simulation');
  private readonly connectionVisibilityOverride = signal<boolean | null>(null);
  protected readonly showFlowConnections = computed(
    () => this.connectionVisibilityOverride() ?? true,
  );

  constructor() {
    effect(() => {
      const ready = this.persistence.loadState() === 'ready',
        state = {
          nodes: this.graph.nodes(),
          connections: this.graph.connections(),
          packs: this.graph.componentPacks(),
          name: this.persistence.savedName(),
          mediaType: this.components.flowMediaType(),
        };
      untracked(() => {
        this.simulator.invalidate();
        if (!ready) {
          this.graph.resetHistory();
          return;
        }
        this.graph.recordHistory(state);
      });
    });
    this.graph.onExternalEdit((state) => {
      this.persistence.savedName.set(state.name);
      this.components.flowMediaType.set(state.mediaType);
      this.inspector.reconcileAfterUndo((nodeId) =>
        state.nodes.some((node) => node.id === nodeId),
      );
    });
    this.persistence.registerAssistant(this, (nodeId) => this.persistence.prepareAiSession(nodeId));
    inject(DestroyRef).onDestroy(() => this.persistence.unregisterAssistant(this));
    void this.persistence.loadPackCatalog();

    const requestedMediaType = this.route.snapshot.queryParamMap.get('mediaType');
    const templateId = this.route.snapshot.queryParamMap.get('templateId');
    if (templateId) this.persistence.loadState.set('loading');
    this.components.flowMediaType.set(this.components.mediaTypeFromParam(requestedMediaType));
    const entry = {
      id: 'flow-input',
      component: MEDIA_INPUT_COMPONENTS[this.components.flowMediaType()],
      position: { x: 64, y: 48 },
      configuration: {},
    };
    this.graph.setGraph([entry], []);
    void this.components
      .loadCatalog()
      .then(async (plugins) => {
        if (templateId && !this.persistence.flowId) {
          await this.persistence.loadTemplate(templateId, plugins);
        }
      })
      .catch(() =>
        this.notifications.show(
          'Catalogue des composants indisponible. Aucun nouveau bloc ne peut être ajouté.',
        ),
      );
    if (this.persistence.flowId && /^[0-9a-f]{8}-[0-9a-f-]{27}$/i.test(this.persistence.flowId)) {
      this.persistence.loadState.set('loading');
      void this.persistence.loadSavedFlow(this.persistence.flowId);
    }
  }

  /** Contrat du guard de navigation : vrai si des modifications sont non enregistrées. */
  hasUnsavedChanges(): boolean {
    return this.persistence.hasUnsavedChanges();
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
      this.graph.undoEdit(event.shiftKey);
      return;
    }
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
      event.preventDefault();
      void this.persistence.saveFlow();
    } else if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'b') {
      event.preventDefault();
      this.togglePalette();
    } else if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
      event.preventDefault();
      void this.openSimulator();
    } else if (event.key === 'Escape') {
      if (this.persistence.packCreationOpen()) {
        this.persistence.packCreationOpen.set(false);
      } else if (this.inspector.configuringNodeId()) {
        this.inspector.closeComponentEditor();
      } else if (this.graph.componentDestination()) {
        this.editing.cancelInsertion();
      }
    }
  }

  protected toggleFlowConnections(): void {
    this.connectionVisibilityOverride.set(!this.showFlowConnections());
  }

  protected setFlowConnections(visible: boolean): void {
    this.connectionVisibilityOverride.set(visible);
  }

  protected cancelInsertion(): void {
    this.editing.cancelInsertion();
  }

  protected openPalette(): void {
    this.inspector.configuringNodeId.set(undefined);
    this.paletteOpen.set(true);
  }
  protected togglePalette(): void {
    if (this.paletteOpen()) this.paletteOpen.set(false);
    else this.openPalette();
  }

  protected onPaletteComponentDropped(event: FlowPaletteDrop): void {
    if (this.persistence.loadState() !== 'ready') return;
    if (event.sourceNodeId && event.output) {
      this.graph.componentDestination.set({
        kind: 'branch',
        sourceNodeId: event.sourceNodeId,
        output: event.output,
      });
      this.editing.addComponent(event.component);
    } else {
      const previousCount = this.graph.nodes().length;
      const position = event.position ?? { x: 0, y: 0 };
      const result = this.editing.addComponent(event.component, position);
      if (result.added && this.graph.nodes().length > previousCount && !this.palettePinned())
        this.paletteOpen.set(false);
      if (result.added && result.nodeId)
        this.graphEditor()?.setCanvasPosition({ nodeId: result.nodeId, position });
    }
  }

  protected requestAutoLayout(): void {
    this.autoLayoutRequest.update((request) => request + 1);
  }

  protected async onPaletteComponentSelected(component: FlowComponentDefinition): Promise<void> {
    const previous = this.graph.nodes().length;
    const result = this.editing.addComponent(
      component,
      this.graph.componentDestination() ? undefined : { x: 0, y: 0 },
    );
    if (result.added && this.graph.nodes().length > previous && !this.palettePinned())
      this.paletteOpen.set(false);
  }

  protected async openSimulator(configure = false): Promise<void> {
    const nodes = this.graph.nodes(),
      connections = this.graph.connections(),
      packs = this.graph.componentPacks();
    try {
      const definition = await this.serverValidation.document(
        this.persistence.validationFlowId,
        this.persistence.storedVersion() + 1,
        this.persistence.flowTitle(),
        this.components.flowMediaType(),
        nodes,
        connections,
        packs,
      );
      if (
        nodes !== this.graph.nodes() ||
        connections !== this.graph.connections() ||
        packs !== this.graph.componentPacks()
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

  protected async runSimulation(
    values: Parameters<FlowSimulation['run']>[0],
    resume = false,
  ): Promise<void> {
    if (resume) await this.simulator.continue(values);
    else await this.simulator.run(values);
    this.trayOpen.set(this.simulator.result()?.status !== 'completed' || !!this.simulator.error());
  }

  protected onTerminationRequested(event: {
    readonly sourceNodeId: string;
    readonly output: FlowComponentOutput;
  }): void {
    this.editing.onTerminationRequested(event);
  }

  protected onBranchComponentRequested(event: {
    readonly sourceNodeId: string;
    readonly output: FlowComponentOutput;
  }): void {
    this.editing.onBranchComponentRequested(event);
    this.openPalette();
  }

  protected onContinuationRequested(sources: readonly FlowContinuationSource[]): void {
    this.editing.onContinuationRequested(sources);
    this.paletteOpen.set(true);
  }

  protected async validateGraph(): Promise<void> {
    await this.persistence.validateGraph();
    this.trayTab.set('diagnostics');
    this.trayOpen.set(true);
  }

  protected onConfigurationRequested(nodeId: string): void {
    this.paletteOpen.set(false);
    this.inspector.onConfigurationRequested(nodeId);
  }

  protected async openAiAssistant(nodeId?: string): Promise<void> {
    await this.flowAssistant.open(nodeId);
  }
}
