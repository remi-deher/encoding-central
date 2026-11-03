import { computed, inject, Injectable, signal } from '@angular/core';
import { flowDocumentNodes } from '../../../shared/models/flow-document-graph';
import { FlowDocument, FlowPluginCatalog } from '../../../shared/models/flow-validation';
import { FlowGraphConnection, FlowGraphNode } from '../../../shared/models/flow-component';
import { FlowComponentPackInstance } from '../../../shared/models/flow-validation';
import { FlowContinuationSource } from '../../../shared/models/flow-sequence';
import { FlowComponentOutput } from '../../../shared/models/flow-component';
import { MediaType } from '../../../shared/models/media-type';
import { FlowComponentsStore } from './flow-components.store';

export type ComponentDestination =
  | { readonly kind: 'continuation'; readonly sources: readonly FlowContinuationSource[] }
  | {
      readonly kind: 'branch';
      readonly sourceNodeId: string;
      readonly output: FlowComponentOutput;
    };

export interface FlowEditState {
  readonly nodes: readonly FlowGraphNode[];
  readonly connections: readonly FlowGraphConnection[];
  readonly packs: readonly FlowComponentPackInstance[];
  readonly name: string | null;
  readonly mediaType: MediaType;
}

/**
 * État central du graphe de l’éditeur : nœuds, connexions, packs, destination
 * d’insertion courante et historique annuler/rétablir. Les opérations d’édition
 * vivent dans {@link FlowGraphEditingStore}.
 */
@Injectable()
export class FlowGraphStore {
  readonly components = inject(FlowComponentsStore);

  readonly nodes = signal<readonly FlowGraphNode[]>([]);
  readonly connections = signal<readonly FlowGraphConnection[]>([]);
  readonly componentPacks = signal<readonly FlowComponentPackInstance[]>([]);
  readonly componentDestination = signal<ComponentDestination | null>(null);
  readonly aiProposalRollback = signal<FlowDocument | null>(null);

  nextNodeId = 1;
  nextConnectionId = 1;

  readonly destinationDescription = computed(() => {
    const dest = this.componentDestination();
    if (!dest) return null;
    if (dest.kind === 'branch') {
      const source = this.nodes().find((node) => node.id === dest.sourceNodeId);
      const sourceName = source?.name || source?.component.name || 'Étape';
      return `Insertion sur la sortie « ${dest.output.label} » de ${sourceName}`;
    }
    return `Insertion sur une suite commune (${dest.sources.length} branche${dest.sources.length > 1 ? 's' : ''})`;
  });

  private readonly editHistory = signal<readonly FlowEditState[]>([]);
  private readonly editHistoryIndex = signal(-1);
  readonly canUndoEdit = computed(() => this.editHistoryIndex() > 0);
  readonly canRedoEdit = computed(
    () => this.editHistoryIndex() + 1 < this.editHistory().length,
  );
  private readonly editCallbacks: ((state: FlowEditState) => void)[] = [];

  /** Renseigne les rappels appliqués à chaque annulation/rétablissement (nom, type de média…). */
  onExternalEdit(callback: (state: FlowEditState) => void): void {
    this.editCallbacks.push(callback);
  }

  /** Enregistre l’instantané courant dans l’historique (appelé par la page quand le graphe est prêt). */
  recordHistory(state: FlowEditState): void {
    const history = this.editHistory(),
      index = this.editHistoryIndex();
    if (JSON.stringify(history[index]) === JSON.stringify(state)) return;
    const next = [...history.slice(0, index + 1), state].slice(-60);
    this.editHistory.set(next);
    this.editHistoryIndex.set(next.length - 1);
  }

  resetHistory(): void {
    this.editHistory.set([]);
    this.editHistoryIndex.set(-1);
  }

  undoEdit(redo = false): void {
    const index = this.editHistoryIndex() + (redo ? 1 : -1),
      state = this.editHistory()[index];
    if (!state) return;
    this.editHistoryIndex.set(index);
    this.nodes.set(state.nodes);
    this.connections.set(state.connections);
    this.componentPacks.set(state.packs);
    this.aiProposalRollback.set(null);
    this.editCallbacks.forEach((callback) => callback(state));
  }

  graphSnapshot(): string {
    return JSON.stringify([this.nodes(), this.connections(), this.componentPacks()]);
  }

  setGraph(nodes: readonly FlowGraphNode[], connections: readonly FlowGraphConnection[]): void {
    this.aiProposalRollback.set(null);
    this.nodes.set(nodes);
    this.connections.set(connections);
  }

  createConnection(
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

  mapDefinitionNodes(
    definition: FlowDocument,
    plugins: readonly FlowPluginCatalog[],
  ): FlowGraphNode[] {
    return flowDocumentNodes(definition, plugins);
  }

  updateGraphCounters(definition: FlowDocument, nodes: readonly FlowGraphNode[]): void {
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
}

export function findEntryId(definition: FlowDocument): string | undefined {
  return definition.nodes.find(
    (node) =>
      node.component.pluginId === 'base' && node.component.componentId.startsWith('base-input-'),
  )?.id;
}
