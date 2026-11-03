import { computed, inject, Injectable, signal } from '@angular/core';
import { NotificationService } from '../../../shared/services/notification.service';
import {
  createFlowInputConfigurationSchema,
  getFlowInputComponents,
} from '../../../shared/models/flow-control-components';
import {
  getFlowConfigurationSchema,
  FlowComponentConfiguration,
} from '../../../shared/models/flow-component';
import { FlowGraphStore } from './flow-graph.store';
import { FlowComponentsStore } from './flow-components.store';

/**
 * Inspecteur latéral de l’éditeur : bloc en cours de configuration, schéma
 * de formulaire, valeurs et références de champs disponibles.
 */
@Injectable()
export class FlowInspectorStore {
  private readonly notifications = inject(NotificationService);
  readonly graph = inject(FlowGraphStore);
  readonly components = inject(FlowComponentsStore);

  readonly configuringNodeId = signal<string | undefined>(undefined);

  readonly configuringNode = computed(() =>
    this.graph.nodes().find((node) => node.id === this.configuringNodeId()),
  );

  readonly configuringSchema = computed(() =>
    this.configuringNode()?.component.kind === 'input'
      ? createFlowInputConfigurationSchema(this.components.flowMediaType())
      : getFlowConfigurationSchema(
          this.configuringNode()?.component.configurationSchema,
          this.components.flowMediaType(),
        ),
  );

  readonly configuringValues = computed<FlowComponentConfiguration>(() => {
    const node = this.configuringNode();
    return node?.component.kind === 'input'
      ? { inputComponentId: node.component.id }
      : (node?.configuration ?? {});
  });

  configurationReferences() {
    const current = this.configuringNode()?.id;
    const upstream = new Set<string>();
    const pending = current ? [current] : [];
    while (pending.length) {
      const target = pending.pop()!;
      for (const edge of this.graph.connections().filter((edge) => edge.targetNodeId === target)) {
        if (!upstream.has(edge.sourceNodeId)) {
          upstream.add(edge.sourceNodeId);
          pending.push(edge.sourceNodeId);
        }
      }
    }
    return {
      artifact: this.graph
        .nodes()
        .filter((node) => upstream.has(node.id) && node.component.producesFiles)
        .map((node) => ({
          value: 'artifact:' + encodeURIComponent(node.id) + '/0',
          label: 'Premier fichier produit · ' + (node.name || node.component.name),
        })),
      'flow-node': this.graph
        .nodes()
        .filter(
          (node) =>
            node.id !== current &&
            node.component.kind === 'condition' &&
            !['base', 'conditions', 'nodes'].includes(node.component.pluginId),
        )
        .map((node) => ({ value: node.id, label: node.name || node.component.name })),
    };
  }

  onConfigurationRequested(nodeId: string): void {
    this.configuringNodeId.set(nodeId);
  }

  closeComponentEditor(): void {
    this.configuringNodeId.set(undefined);
  }

  deleteConfiguredNode(nodeId: string, removeNode: (nodeId: string) => void): void {
    removeNode(nodeId);
    this.configuringNodeId.set(undefined);
  }

  renameNode(name: string): void {
    const nodeId = this.configuringNodeId();
    this.graph.nodes.update((nodes) =>
      nodes.map((node) => (node.id === nodeId ? { ...node, name: name || undefined } : node)),
    );
  }

  saveNodeConfiguration(configuration: FlowComponentConfiguration): void {
    const nodeId = this.configuringNodeId();
    if (!nodeId) return;
    if (this.configuringNode()?.component.kind === 'input') {
      const inputComponent = getFlowInputComponents(this.components.flowMediaType()).find(
        (component) => component.id === configuration['inputComponentId'],
      );
      if (!inputComponent) {
        this.notifications.show('Choisissez une entrée compatible avec le type de flow.');
        return;
      }
      this.graph.nodes.update((nodes) =>
        nodes.map((node) =>
          node.id === nodeId
            ? { ...node, reference: undefined, component: inputComponent, configuration: {} }
            : node,
        ),
      );
      this.closeComponentEditor();
      return;
    }
    this.graph.nodes.update((nodes) =>
      nodes.map((node) =>
        node.id === nodeId
          ? { ...node, configuration: { ...node.configuration, ...configuration } }
          : node,
      ),
    );
    this.closeComponentEditor();
  }

  /** Nettoie la sélection si l’annulation/rétablissement retire le bloc configuré. */
  reconcileAfterUndo(nodeExists: (nodeId: string) => boolean): void {
    const nodeId = this.configuringNodeId();
    if (nodeId && !nodeExists(nodeId)) this.configuringNodeId.set(undefined);
  }
}
