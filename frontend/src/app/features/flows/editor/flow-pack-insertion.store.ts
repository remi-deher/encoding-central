import { inject, Injectable } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { NotificationService } from '../../../shared/services/notification.service';
import { FlowValidation } from '../../../shared/services/flow-validation';
import { FlowComponentPacks } from '../../../shared/services/flow-component-packs';
import {
  FlowComponentPackDetails,
  FlowComponentPackSummary,
} from '../../../shared/models/flow-component-packs';
import { canConnectFlowGraph } from '../../../shared/models/flow-graph';
import { createUuid } from '../../../shared/utils/uuid';
import { FlowComponentPackInstance } from '../../../shared/models/flow-validation';
import { ComponentDestination, findEntryId, FlowGraphStore } from './flow-graph.store';

/**
 * Insertion d’un pack de composants dans le graphe : clonage des nœuds internes,
 * ports exposés et reconnexion à la destination courante.
 */
@Injectable()
export class FlowPackInsertionStore {
  private readonly notifications = inject(NotificationService);
  private readonly serverValidation = inject(FlowValidation);
  private readonly packApi = inject(FlowComponentPacks);
  readonly graph = inject(FlowGraphStore);

  async onPalettePackSelected(summary: FlowComponentPackSummary): Promise<void> {
    try {
      const pack = await this.packApi.get(summary.id);
      await this.insertPack(pack);
    } catch (error) {
      this.notifications.show(
        error instanceof HttpErrorResponse
          ? (error.error?.title ?? 'Chargement du pack impossible.')
          : 'Chargement du pack impossible.',
      );
    }
  }

  async insertPack(pack: FlowComponentPackDetails): Promise<void> {
    const destination: ComponentDestination = this.graph.componentDestination() ?? {
      kind: 'continuation',
      sources: [],
    };
    if (pack.pack.mediaType !== this.graph.components.flowMediaType()) {
      this.notifications.show('Ce pack n’est pas compatible avec le type de média du flow.');
      return;
    }
    const plugins = await this.serverValidation.catalog();
    const sourceNodes = this.graph
      .mapDefinitionNodes(pack.definition, plugins)
      .filter((node) => node.id !== findEntryId(pack.definition));
    const clonedIds = new Map(
      sourceNodes.map((node) => [node.id, `flow-node-${this.graph.nextNodeId++}`]),
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
        this.graph.createConnection(
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
        ? this.graph
            .connections()
            .find(
              (edge) =>
                edge.sourceNodeId === destination.sourceNodeId &&
                edge.sourceOutputId === destination.output.id,
            )
        : undefined;
    const remaining = this.graph.connections().filter((edge) => edge.id !== existing?.id);
    const nodes = [...this.graph.nodes(), ...clonedNodes];
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
        this.graph.createConnection(destination.sourceNodeId, destination.output.id, inputNodeId),
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
            this.graph.createConnection(
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
        next.push(this.graph.createConnection(source.sourceNodeId, source.output.id, inputNodeId));
      }
    }
    this.graph.componentPacks.update((instances) => [...instances, instance]);
    this.graph.setGraph(nodes, next);
    this.graph.componentDestination.set(null);
  }

}
